using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Shared steps for driving a real panel with stand-in agents and probes. The fixture is shared per
/// test class and failover state is fleet-wide, so every test first revokes the nodes it did not
/// create: whatever an earlier test left behind can then never be the node traffic moves to.
/// </summary>
public abstract class FleetScenario(ControlPlaneFixture fixture)
{
    protected ControlPlaneFixture Fixture => fixture;

    protected sealed record EnrolledNode(string Id, string Name, string PublicIp, HttpClient Client, TestAgent Agent);

    protected async Task<HttpClient> AdminAsync() => await fixture.CreateAdminClientAsync();

    protected async Task<EnrolledNode> NodeAsync(HttpClient admin, string name, string publicIp)
    {
        var token = await TokenAsync(admin, "/api/nodes/tokens", new CreateNodeRequest(name, null, null, null));
        var client = fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(client, token.Token, hostname: name);

        var node = new EnrolledNode(agent.NodeId!, name, publicIp, client, agent);
        await ReportAsync(node, interfaceUp: true);
        return node;
    }

    protected static async Task ReportAsync(EnrolledNode node, bool interfaceUp)
    {
        var report = new NodeStatusReport(
            AppliedRevision: 0,
            InterfaceUp: interfaceUp,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [],
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            PublicIp: node.PublicIp);

        var response = await node.Client.SendAsync(node.Agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{node.Id}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Revokes every node but these, so only they can be picked.</summary>
    protected static async Task IsolateAsync(HttpClient admin, params EnrolledNode[] keep)
    {
        var ids = keep.Select(node => node.Id).ToHashSet();
        foreach (var node in (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!)
        {
            if (!node.Revoked && !ids.Contains(node.Id))
            {
                (await admin.PostAsync($"/api/nodes/{node.Id}/revoke", null)).EnsureSuccessStatusCode();
            }
        }
    }

    protected static async Task<NodeResponse> NodeStateAsync(HttpClient admin, string id)
        => (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!.Single(node => node.Id == id);

    protected static async Task SetModeAsync(HttpClient admin, string mode)
        => (await admin.PutAsJsonAsync("/api/failover", new UpdateFailoverRequest(mode))).EnsureSuccessStatusCode();

    protected static async Task<FailoverStatusResponse> EvaluateAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/failover/evaluate", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FailoverStatusResponse>())!;
    }

    protected static async Task<EnrollmentTokenResponse> TokenAsync<T>(HttpClient admin, string path, T request)
    {
        var response = await admin.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;
    }

    protected async Task<(HttpClient Client, TestAgent Probe)> ProbeAsync(HttpClient admin, string name)
    {
        var token = await TokenAsync(admin, "/api/probes/tokens", new CreateProbeRequest(name));
        var client = fixture.CreateClient();
        var probe = new TestAgent();
        (await probe.EnrollAsProbeAsync(client, token.Token, name)).EnsureSuccessStatusCode();
        return (client, probe);
    }

    protected static async Task<ProbeAssignment> AssignmentAsync(HttpClient client, TestAgent probe)
    {
        var response = await client.SendAsync(probe.SignedRequest(HttpMethod.Get, $"/api/v1/probes/{probe.NodeId}/assignment"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProbeAssignment>())!;
    }

    protected static async Task<HttpResponseMessage> ReportProbeAsync(HttpClient client, TestAgent probe, params ProbeResult[] results)
        => await client.SendAsync(probe.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/probes/{probe.NodeId}/results",
            JsonContent.Create(new ProbeReport("test-probe/1.0", DateTimeOffset.UtcNow, results))));

    protected static ProbeResult Result(EnrolledNode node, string outcome, bool? handshake = null)
        => new(
            node.Id,
            node.PublicIp,
            outcome,
            DateTimeOffset.UtcNow,
            outcome == ProbeOutcomes.Reachable ? 40 : null,
            null,
            handshake ?? outcome == ProbeOutcomes.Reachable);
}

/// <summary>
/// Automatic failover end to end: agents report, the monitor judges, and the record moves through
/// the same activation path the button uses - or deliberately does not.
/// </summary>
public class AutomaticFailoverTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    [Fact]
    public async Task Moves_clients_off_an_active_node_whose_tunnel_went_down()
    {
        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var admin = await AdminAsync();
        var active = await NodeAsync(admin, "auto-down-a", "203.0.113.101");
        var standby = await NodeAsync(admin, "auto-down-b", "203.0.113.102");
        await IsolateAsync(admin, active, standby);

        (await admin.PostAsync($"/api/nodes/{active.Id}/activate", null)).EnsureSuccessStatusCode();
        await SetModeAsync(admin, FailoverModes.Automatic);
        var revisionBefore = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

        await ReportAsync(active, interfaceUp: false);
        var status = await EvaluateAsync(admin);

        Assert.Equal("switch", status.Action);
        Assert.Equal(standby.Id, status.TargetNodeId);
        Assert.True((await NodeStateAsync(admin, standby.Id)).IsActive);
        Assert.Equal(standby.PublicIp, Fixture.Dns.Last!.Address);

        // Same invariant as the button: switching changes nothing any node runs.
        Assert.Equal(revisionBefore, (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision);

        Assert.Contains(Fixture.Notifications.Sent, sent => sent.Kind == "failover.switched" && sent.NodeId == standby.Id);
        var events = (await admin.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        Assert.Contains(events, entry => entry.Kind == "node.activated" && entry.NodeId == standby.Id && entry.Actor == FailoverMonitor.Actor);

        await SetModeAsync(admin, FailoverModes.Manual);
    }

    // Manual mode still watches. It says what it would do and leaves the record alone.
    [Fact]
    public async Task Only_recommends_a_switch_while_failover_is_manual()
    {
        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var admin = await AdminAsync();
        var active = await NodeAsync(admin, "manual-a", "203.0.113.111");
        var standby = await NodeAsync(admin, "manual-b", "203.0.113.112");
        await IsolateAsync(admin, active, standby);

        (await admin.PostAsync($"/api/nodes/{active.Id}/activate", null)).EnsureSuccessStatusCode();
        await SetModeAsync(admin, FailoverModes.Manual);

        await ReportAsync(active, interfaceUp: false);
        var status = await EvaluateAsync(admin);

        Assert.Equal("recommend", status.Action);
        Assert.Equal(standby.Id, status.TargetNodeId);
        Assert.True((await NodeStateAsync(admin, active.Id)).IsActive);
        Assert.Contains(Fixture.Notifications.Sent, sent => sent.Kind == "failover.recommended" && sent.NodeId == active.Id);
    }

    // A node the operator excluded is never where traffic goes without them.
    [Fact]
    public async Task Leaves_the_record_alone_when_the_only_standby_is_excluded()
    {
        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var admin = await AdminAsync();
        var active = await NodeAsync(admin, "excluded-a", "203.0.113.121");
        var standby = await NodeAsync(admin, "excluded-b", "203.0.113.122");
        await IsolateAsync(admin, active, standby);

        (await admin.PutAsJsonAsync($"/api/nodes/{standby.Id}/failover", new UpdateNodeFailoverRequest(100, AutoFailover: false))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/nodes/{active.Id}/activate", null)).EnsureSuccessStatusCode();
        await SetModeAsync(admin, FailoverModes.Automatic);

        await ReportAsync(active, interfaceUp: false);
        var status = await EvaluateAsync(admin);

        Assert.Equal("stuck", status.Action);
        Assert.True((await NodeStateAsync(admin, active.Id)).IsActive);

        await SetModeAsync(admin, FailoverModes.Manual);
    }

    // The stored active node means "the record points here". A refused switch leaves both where
    // they were, and says so.
    [Fact]
    public async Task A_refused_switch_leaves_the_active_node_in_place()
    {
        var admin = await AdminAsync();
        var active = await NodeAsync(admin, "refused-a", "203.0.113.131");
        var standby = await NodeAsync(admin, "refused-b", "203.0.113.132");
        await IsolateAsync(admin, active, standby);

        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        (await admin.PostAsync($"/api/nodes/{active.Id}/activate", null)).EnsureSuccessStatusCode();
        await SetModeAsync(admin, FailoverModes.Automatic);

        await ReportAsync(active, interfaceUp: false);
        Fixture.Dns.Outcome = DnsUpdateOutcome.Failed;
        var status = await EvaluateAsync(admin);
        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;

        Assert.Equal("failed", status.Action);
        Assert.True((await NodeStateAsync(admin, active.Id)).IsActive);
        Assert.False((await NodeStateAsync(admin, standby.Id)).IsActive);

        await SetModeAsync(admin, FailoverModes.Manual);
    }

    [Fact]
    public async Task Cannot_be_armed_without_a_dns_provider()
    {
        var admin = await AdminAsync();
        Fixture.Dns.IsConfigured = false;

        var response = await admin.PutAsJsonAsync("/api/failover", new UpdateFailoverRequest(FailoverModes.Automatic));
        Fixture.Dns.IsConfigured = true;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("failover_requires_dns_provider", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    // A zone or record problem is found while someone is looking, not when a node dies.
    [Fact]
    public async Task Cannot_be_armed_while_the_record_could_not_be_moved()
    {
        var admin = await AdminAsync();
        Fixture.Dns.CheckError = new ApiError("dns_record_ambiguous", "Two A records.");

        var response = await admin.PutAsJsonAsync("/api/failover", new UpdateFailoverRequest(FailoverModes.Automatic));
        Fixture.Dns.CheckError = null;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dns_record_ambiguous", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(FailoverModes.Manual, (await admin.GetFromJsonAsync<FailoverStatusResponse>("/api/failover"))!.Mode);
    }

    [Fact]
    public async Task Rejects_an_unknown_mode()
    {
        var admin = await AdminAsync();

        var response = await admin.PutAsJsonAsync("/api/failover", new UpdateFailoverRequest("sometimes"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// Probes through the real panel: enrollment, what they are asked to check, how their results
/// turn into a node's health, and what they must never be able to get.
/// </summary>
public class ProbeProtocolTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    [Fact]
    public async Task A_probe_is_asked_to_handshake_with_every_node_that_has_an_address()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-target", "203.0.113.141");
        var (client, probe) = await ProbeAsync(admin, "probe-assign");

        var assignment = await AssignmentAsync(client, probe);

        var target = Assert.Single(assignment.Targets, target => target.NodeId == node.Id);
        Assert.Equal(node.PublicIp, target.Address);
        Assert.Contains($"Endpoint = {node.PublicIp}:51820", target.Config);
        // Installs no routes on the probe host, yet accepts replies from anywhere: the check
        // fetches a resource on the internet through the node.
        Assert.Contains("Table = off", target.Config);
        Assert.Contains("AllowedIPs = 0.0.0.0/0", target.Config);
        Assert.Contains("PrivateKey = ", target.Config);

        // A handshake alone is not the check: the common block lets it through and drops the rest.
        Assert.NotEmpty(assignment.CheckUrls!);
        Assert.True(assignment.TrafficTimeoutSeconds > 0);
    }

    // The probe sits in the network that does the blocking. It is a client of the fleet and must
    // never be mistaken for a node - above all, never handed a bundle with the fleet private key.
    [Fact]
    public async Task A_probe_cannot_fetch_a_bundle()
    {
        var admin = await AdminAsync();
        var (client, probe) = await ProbeAsync(admin, "probe-no-bundle");

        var response = await client.SendAsync(probe.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{probe.NodeId}/desired"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Its key has to be on every node, or no handshake it attempts could succeed - but it is not a
    // person, so it stays out of the client list.
    [Fact]
    public async Task A_probe_is_a_peer_on_every_node_but_not_a_listed_client()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-peer-node", "203.0.113.151");
        var before = await PeersAsync(node);

        await ProbeAsync(admin, "probe-peer");

        Assert.Equal(before + 1, await PeersAsync(node));
        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.DoesNotContain(clients, entry => entry.Name.StartsWith("probe:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_node_token_cannot_enroll_a_probe()
    {
        var admin = await AdminAsync();
        var token = await TokenAsync(admin, "/api/nodes/tokens", new CreateNodeRequest("not-a-probe", null, null, null));

        var response = await new TestAgent().EnrollAsProbeAsync(Fixture.CreateClient(), token.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("enrollment_wrong_kind", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    // The other direction matters more: a probe token that enrolled a node would hand the fleet
    // private key to whatever host it was pasted on.
    [Fact]
    public async Task A_probe_token_cannot_enroll_a_node()
    {
        var admin = await AdminAsync();
        var token = await TokenAsync(admin, "/api/probes/tokens", new CreateProbeRequest("not-a-node"));

        var response = await Fixture.CreateClient().PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token.Token, "host", new TestAgent().PublicKey, "test-agent/1.0", DesiredStateBundle.CurrentSchemaVersion));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("enrollment_wrong_kind", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    // The case failover exists for, and the one no node can report about itself.
    [Fact]
    public async Task A_node_its_agent_calls_healthy_but_no_probe_reaches_is_blocked()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-blocked", "203.0.113.161");
        var (client, probe) = await ProbeAsync(admin, "probe-blocked-probe");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();

        var state = await NodeStateAsync(admin, node.Id);
        Assert.Equal(NodeHealthStates.Blocked, state.Health);
        Assert.Equal(NodeHealthEvaluator.ProbeSource, state.HealthSource);
        Assert.Equal(0, state.ProbesReachable);
        Assert.Equal(1, state.ProbesReporting);
    }

    [Fact]
    public async Task A_handshake_with_no_traffic_after_it_is_blocked_and_shown_as_such()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-dpi", "203.0.113.165");
        var (client, probe) = await ProbeAsync(admin, "probe-dpi-probe");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable, handshake: true))).EnsureSuccessStatusCode();

        var state = await NodeStateAsync(admin, node.Id);
        Assert.Equal(NodeHealthStates.Blocked, state.Health);
        Assert.Contains("no traffic came back", state.HealthReason);

        var probes = (await admin.GetFromJsonAsync<ProbeResponse[]>("/api/probes"))!;
        var result = probes.Single(entry => entry.Id == probe.NodeId).Results.Single(entry => entry.NodeId == node.Id);
        Assert.True(result.Handshake);
        Assert.Equal(ProbeOutcomes.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task Switches_away_from_a_blocked_node_to_one_probes_reach()
    {
        Fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var admin = await AdminAsync();
        var active = await NodeAsync(admin, "probe-switch-a", "203.0.113.171");
        var standby = await NodeAsync(admin, "probe-switch-b", "203.0.113.172");
        await IsolateAsync(admin, active, standby);
        var (client, probe) = await ProbeAsync(admin, "probe-switch-probe");

        (await admin.PostAsync($"/api/nodes/{active.Id}/activate", null)).EnsureSuccessStatusCode();
        await SetModeAsync(admin, FailoverModes.Automatic);

        (await ReportProbeAsync(client, probe, Result(active, ProbeOutcomes.Unreachable), Result(standby, ProbeOutcomes.Reachable)))
            .EnsureSuccessStatusCode();
        var status = await EvaluateAsync(admin);

        Assert.Equal("switch", status.Action);
        Assert.True((await NodeStateAsync(admin, standby.Id)).IsActive);
        Assert.Equal(standby.PublicIp, Fixture.Dns.Last!.Address);

        await SetModeAsync(admin, FailoverModes.Manual);
    }

    // A probe that could not run its own check says nothing about the node. Counting it would let
    // a broken probe move a fleet's traffic.
    [Fact]
    public async Task A_probe_error_does_not_count_against_the_node()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-error-node", "203.0.113.181");
        var (client, probe) = await ProbeAsync(admin, "probe-error");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Error))).EnsureSuccessStatusCode();

        var state = await NodeStateAsync(admin, node.Id);
        Assert.Equal(NodeHealthStates.Healthy, state.Health);
        Assert.Equal(NodeHealthEvaluator.AgentSource, state.HealthSource);
    }

    [Fact]
    public async Task A_revoked_probe_stops_counting_and_stops_being_a_peer()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-revoked-node", "203.0.113.191");
        var (client, probe) = await ProbeAsync(admin, "probe-revoked");
        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();
        var peersWithProbe = await PeersAsync(node);

        (await admin.PostAsync($"/api/probes/{probe.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        Assert.Equal(NodeHealthStates.Healthy, (await NodeStateAsync(admin, node.Id)).Health);
        Assert.Equal(peersWithProbe - 1, await PeersAsync(node));
        var refused = await client.SendAsync(probe.SignedRequest(HttpMethod.Get, $"/api/v1/probes/{probe.NodeId}/assignment"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task A_report_with_an_invalid_outcome_is_refused_whole()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-invalid-node", "203.0.113.195");
        var (client, probe) = await ProbeAsync(admin, "probe-invalid");

        var response = await ReportProbeAsync(
            client,
            probe,
            Result(node, ProbeOutcomes.Unreachable),
            Result(node, "maybe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(NodeHealthEvaluator.AgentSource, (await NodeStateAsync(admin, node.Id)).HealthSource);
    }

    [Fact]
    public async Task A_probe_cannot_report_for_another_probe()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "probe-spoof-node", "203.0.113.197");
        var (client, probe) = await ProbeAsync(admin, "probe-spoof");
        var (_, other) = await ProbeAsync(admin, "probe-spoof-other");

        var response = await client.SendAsync(probe.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/probes/{other.NodeId}/results",
            JsonContent.Create(new ProbeReport("x", DateTimeOffset.UtcNow, [Result(node, ProbeOutcomes.Unreachable)]))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<int> PeersAsync(EnrolledNode node)
    {
        var response = await node.Client.SendAsync(node.Agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{node.Id}/desired"));
        response.EnsureSuccessStatusCode();
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;
        return node.Agent.AcceptBundle(envelope).Peers.Length;
    }
}
