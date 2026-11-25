using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using AwgEasy.Node;
using Microsoft.Extensions.DependencyInjection;

namespace AwgEasy.Tests;

public class AddressChecksTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private const string Primary = "91.132.59.9";
    private const string Secondary = "157.228.112.119";

    private async Task<(HttpClient Admin, EnrolledNode Node, HttpClient ProbeClient, TestAgent Probe)> SetupAsync(string name)
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, name, Primary);
        await IsolateAsync(admin, node);
        await StatusAsync(node, 0);
        var (client, probe) = await ProbeAsync(admin, name + "-probe");
        return (admin, node, client, probe);
    }

    private static async Task StatusAsync(EnrolledNode node, long revision,
        string[]? ingress = null, ReportedEgressAddress[]? egress = null)
    {
        var report = new NodeStatusReport(revision, true, "Kernel module", "test", DateTimeOffset.UtcNow,
            [], null, null, DesiredStateBundle.CurrentSchemaVersion, Primary,
            egress ?? [new(Primary), new(Secondary)], ingress ?? [Primary, Secondary, "10.0.0.1", Secondary]);
        (await node.Client.SendAsync(node.Agent.SignedRequest(HttpMethod.Post,
            $"/api/v1/agents/{node.Id}/status", JsonContent.Create(report)))).EnsureSuccessStatusCode();
    }

    private static async Task<DesiredStateBundle> BundleAsync(EnrolledNode node)
    {
        var response = await node.Client.SendAsync(node.Agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{node.Id}/desired"));
        response.EnsureSuccessStatusCode();
        return node.Agent.AcceptBundle((await response.Content.ReadFromJsonAsync<SignedBundle>())!);
    }

    private static async Task<NodeAddressesResponse> ChecksAsync(HttpClient admin, EnrolledNode node)
        => (await admin.GetFromJsonAsync<NodeAddressesResponse>($"/api/nodes/{node.Id}/addresses"))!;

    private static ProbeResult Checked(ProbeTarget target, string outcome, bool handshake = true)
        => new(target.NodeId, target.Address, outcome, DateTimeOffset.UtcNow, 15, "test path", handshake,
            target.EgressAddress, target.EgressAddress);

    private Task EvaluateAddressesAsync() => Fixture.Services.GetRequiredService<AddressCheckService>()
        .ReportTransitionsAsync(Fixture.Services.GetRequiredService<NodeRepository>().List(), DateTimeOffset.UtcNow, CancellationToken.None);

    [Fact]
    public async Task Discovers_public_entries_and_waits_for_exit_peers_to_be_applied()
    {
        var (admin, node, client, probe) = await SetupAsync("address-assignment");
        var before = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        var assignment = await AssignmentAsync(client, probe);
        Assert.Equal(Primary, Assert.Single(assignment.Targets).Address);
        Assert.Equal(Secondary, Assert.Single(assignment.AddressTargets!).Address);
        var bundle = await BundleAsync(node);
        Assert.True(bundle.Revision > before.Revision);
        Assert.Equal(new[] { Primary, Secondary }.Order(), bundle.Peers.Where(peer => peer.EgressAddress is not null).Select(peer => peer.EgressAddress).Order());
        Assert.Empty(await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients") ?? []);

        await StatusAsync(node, bundle.Revision);
        var ready = await AssignmentAsync(client, probe);
        Assert.Equal(2, ready.AddressTargets!.Count(target => target.EgressAddress is not null));
        Assert.Equal(new[] { Primary, Secondary }.Order(), ready.AddressTargets!.Where(target => target.EgressAddress is not null)
            .Select(target => target.ExpectedEgressAddress).Order());
        Assert.Equal(bundle.Revision, (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision);
        Assert.Equal(before.Generation, (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Generation);
        var checks = await ChecksAsync(admin, node);
        Assert.Equal(new[] { Primary, Secondary }, checks.Ingress.Select(row => row.Address));
        Assert.All(checks.Egress, row => Assert.Equal("unknown", row.Outcome));
    }

    [Fact]
    public async Task A_failed_secondary_entry_does_not_replace_primary_failover_evidence()
    {
        var (admin, node, client, probe) = await SetupAsync("address-secondary");
        var assignment = await AssignmentAsync(client, probe);
        var primary = Assert.Single(assignment.Targets);
        var secondary = Assert.Single(assignment.AddressTargets!);
        (await ReportProbeAsync(client, probe, Checked(primary, ProbeOutcomes.Reachable), Checked(secondary, ProbeOutcomes.Unreachable, false))).EnsureSuccessStatusCode();
        var checks = await ChecksAsync(admin, node);
        Assert.Equal(ProbeOutcomes.Reachable, checks.Ingress.Single(row => row.Address == Primary).Outcome);
        Assert.Equal(ProbeOutcomes.Unreachable, checks.Ingress.Single(row => row.Address == Secondary).Outcome);
        Assert.Equal(NodeHealthStates.Healthy, (await NodeStateAsync(admin, node.Id)).Health);
        var observations = Fixture.Services.GetRequiredService<ProbeRepository>().Observations();
        Assert.Equal(Primary, Assert.Single(observations, row => row.ProbeId == probe.NodeId && row.NodeId == node.Id).Address);
    }

    [Fact]
    public async Task A_working_secondary_entry_cannot_hide_a_failed_primary()
    {
        var (admin, node, client, probe) = await SetupAsync("address-primary-failed");
        var assignment = await AssignmentAsync(client, probe);
        (await ReportProbeAsync(client, probe, Checked(assignment.Targets[0], ProbeOutcomes.Unreachable, false),
            Checked(Assert.Single(assignment.AddressTargets!), ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        Assert.Equal(NodeHealthStates.Blocked, (await NodeStateAsync(admin, node.Id)).Health);
        Assert.Equal(ProbeOutcomes.Reachable, (await ChecksAsync(admin, node)).Ingress.Single(row => row.Address == Secondary).Outcome);
    }

    [Fact]
    public async Task A_result_for_an_exit_not_yet_applied_by_the_node_is_ignored()
    {
        var (admin, node, client, probe) = await SetupAsync("address-premature-exit");
        var entry = (await AssignmentAsync(client, probe)).Targets[0];
        (await ReportProbeAsync(client, probe,
            Checked(entry with { EgressAddress = Primary }, ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();
        Assert.All((await ChecksAsync(admin, node)).Egress, row => Assert.Empty(row.Probes));
    }

    [Fact]
    public async Task Two_probes_never_share_the_peer_or_tunnel_address_used_to_check_an_exit()
    {
        var (admin, node, client, probe) = await SetupAsync("address-distinct-probes");
        await AssignmentAsync(client, probe);
        var (otherClient, other) = await ProbeAsync(admin, "address-other-probe");
        await AssignmentAsync(otherClient, other);
        var paths = Fixture.Services.GetRequiredService<ProbePathRepository>().List().Where(path => path.NodeId == node.Id).ToArray();
        var own = paths.Single(path => path.ProbeId == probe.NodeId && path.Address == Primary);
        var another = paths.Single(path => path.ProbeId == other.NodeId && path.Address == Primary);
        var clients = Fixture.Services.GetRequiredService<ClientRepository>();
        Assert.NotEqual(clients.FindProbeClient(own.ClientId)!.PublicKey, clients.FindProbeClient(another.ClientId)!.PublicKey);
        Assert.NotEqual(clients.FindProbeClient(own.ClientId)!.Address, clients.FindProbeClient(another.ClientId)!.Address);
    }

    [Fact]
    public async Task An_exit_is_checked_through_its_own_peer_without_condemning_other_addresses()
    {
        var (admin, node, client, probe) = await SetupAsync("address-exit");
        await AssignmentAsync(client, probe);
        var bundle = await BundleAsync(node);
        await StatusAsync(node, bundle.Revision);
        var assignment = await AssignmentAsync(client, probe);
        var exits = assignment.AddressTargets!.Where(target => target.EgressAddress is not null).ToArray();
        Assert.NotEqual(exits[0].Config, exits[1].Config);
        (await ReportProbeAsync(client, probe, Checked(assignment.Targets[0], ProbeOutcomes.Reachable),
            Checked(exits.Single(target => target.EgressAddress == Primary), ProbeOutcomes.Reachable),
            Checked(exits.Single(target => target.EgressAddress == Secondary), ProbeOutcomes.Unreachable))).EnsureSuccessStatusCode();
        var checks = await ChecksAsync(admin, node);
        Assert.Equal(ProbeOutcomes.Reachable, checks.Egress.Single(row => row.Address == Primary).Outcome);
        Assert.Equal(ProbeOutcomes.Unreachable, checks.Egress.Single(row => row.Address == Secondary).Outcome);
        Assert.Equal(NodeHealthStates.Healthy, (await NodeStateAsync(admin, node.Id)).Health);
        Assert.Equal(Secondary, Assert.Single(checks.Egress.Single(row => row.Address == Secondary).Probes).ObservedAddress);
    }

    [Fact]
    public async Task A_missing_ingress_handshake_is_no_evidence_about_an_exit()
    {
        var (admin, node, client, probe) = await SetupAsync("address-entry-unavailable");
        await AssignmentAsync(client, probe);
        await StatusAsync(node, (await BundleAsync(node)).Revision);
        var target = (await AssignmentAsync(client, probe)).AddressTargets!.First(target => target.EgressAddress is not null);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Unreachable, false))).EnsureSuccessStatusCode();
        var exit = (await ChecksAsync(admin, node)).Egress.Single(row => row.Address == target.EgressAddress);
        Assert.Equal("unknown", exit.Outcome);
        Assert.Equal(ProbeOutcomes.Error, Assert.Single(exit.Probes).Outcome);
    }

    [Fact]
    public async Task Fresh_exit_traffic_still_counts_while_the_node_agent_is_silent()
    {
        var (admin, node, client, probe) = await SetupAsync("address-fail-static");
        await AssignmentAsync(client, probe);
        var revision = (await BundleAsync(node)).Revision;
        await StatusAsync(node, revision);
        var target = (await AssignmentAsync(client, probe)).AddressTargets!.First(target => target.EgressAddress is not null);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        Fixture.Services.GetRequiredService<NodeRepository>().RecordStatus(node.Id,
            new NodeStatusReport(revision, true, "Kernel module", "test", DateTimeOffset.UtcNow.AddHours(-1),
                [], null, null, DesiredStateBundle.CurrentSchemaVersion), NodeStatuses.Healthy);
        Assert.Equal(ProbeOutcomes.Reachable, (await ChecksAsync(admin, node)).Egress.Single(row => row.Address == target.EgressAddress).Outcome);
    }

    [Fact]
    public async Task Stale_results_and_probe_errors_never_mean_an_address_is_down()
    {
        var (admin, node, client, probe) = await SetupAsync("address-stale");
        var target = (await AssignmentAsync(client, probe)).Targets[0];
        var repository = Fixture.Services.GetRequiredService<ProbeAddressRepository>();
        repository.Record(probe.NodeId!, [Checked(target, ProbeOutcomes.Unreachable)], DateTimeOffset.UtcNow.AddHours(-1));
        var stale = (await ChecksAsync(admin, node)).Ingress.Single(row => row.Address == Primary);
        Assert.Equal("unknown", stale.Outcome);
        Assert.False(Assert.Single(stale.Probes).Fresh);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Error))).EnsureSuccessStatusCode();
        Assert.Equal("unknown", (await ChecksAsync(admin, node)).Ingress.Single(row => row.Address == Primary).Outcome);
    }

    [Fact]
    public async Task Address_failures_and_recoveries_are_announced_once()
    {
        var (admin, node, client, probe) = await SetupAsync("address-alerts");
        var target = Assert.Single((await AssignmentAsync(client, probe)).AddressTargets!);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Unreachable, false))).EnsureSuccessStatusCode();
        await EvaluateAddressesAsync();
        await EvaluateAddressesAsync();
        Assert.Single(Fixture.Notifications.Sent, item => item.NodeId == node.Id && item.Kind == "address.ingress.unreachable");
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        await EvaluateAddressesAsync();
        Assert.Single(Fixture.Notifications.Sent, item => item.NodeId == node.Id && item.Kind == "address.ingress.recovered");
    }

    [Fact]
    public async Task Unknown_and_removed_addresses_cannot_acquire_a_status()
    {
        var (admin, node, client, probe) = await SetupAsync("address-stale-assignment");
        var target = Assert.Single((await AssignmentAsync(client, probe)).AddressTargets!);
        await StatusAsync(node, (await BundleAsync(node)).Revision, ingress: [Primary], egress: [new(Primary)]);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Unreachable),
            Checked(target with { Address = "8.8.8.8" }, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        var checks = await ChecksAsync(admin, node);
        Assert.Equal(Primary, Assert.Single(checks.Ingress).Address);
        Assert.Equal(Primary, Assert.Single(checks.Egress).Address);
        Assert.DoesNotContain(Fixture.Services.GetRequiredService<ProbeAddressRepository>().List(),
            row => row.ProbeId == probe.NodeId && row.Address != Primary);
        await AssignmentAsync(client, probe);
        var paths = Fixture.Services.GetRequiredService<ProbePathRepository>().List();
        Assert.DoesNotContain(paths, path => path.ProbeId == probe.NodeId && path.Address == Secondary);
    }

    [Fact]
    public async Task Revoking_a_probe_removes_every_extra_peer_and_its_evidence()
    {
        var (admin, node, client, probe) = await SetupAsync("address-revoke");
        var target = (await AssignmentAsync(client, probe)).Targets[0];
        var paths = Fixture.Services.GetRequiredService<ProbePathRepository>().List().Where(path => path.ProbeId == probe.NodeId).ToArray();
        var peerKeys = paths.Select(path => Fixture.Services.GetRequiredService<ClientRepository>().FindProbeClient(path.ClientId)!.PublicKey).ToArray();
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/probes/{probe.NodeId}/revoke", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await BundleAsync(node)).Peers, peer => peerKeys.Contains(peer.PublicKey));
        Assert.DoesNotContain(Fixture.Services.GetRequiredService<ProbePathRepository>().List(), path => path.ProbeId == probe.NodeId);
        Assert.Empty((await ChecksAsync(admin, node)).Ingress.Single(row => row.Address == Primary).Probes);
    }

    [Fact]
    public async Task Address_checks_are_masked_for_demo_and_require_authentication()
    {
        var (admin, node, client, probe) = await SetupAsync("address-mask");
        var target = (await AssignmentAsync(client, probe)).Targets[0];
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        using var anonymous = Fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/nodes/{node.Id}/addresses")).StatusCode);
        using var demo = await Fixture.CreateDemoClientAsync();
        var response = await demo.GetAsync($"/api/nodes/{node.Id}/addresses");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Primary, json);
        Assert.DoesNotContain(Secondary, json);
        Assert.DoesNotContain(node.Name, json);
    }

    [Fact]
    public async Task Replica_and_export_keep_the_egress_peer_mapping_and_address_reports()
    {
        var (admin, node, client, probe) = await SetupAsync("address-replica");
        await AssignmentAsync(client, probe);
        await StatusAsync(node, (await BundleAsync(node)).Revision);
        var target = (await AssignmentAsync(client, probe)).AddressTargets!.First(target => target.EgressAddress is not null);
        (await ReportProbeAsync(client, probe, Checked(target, ProbeOutcomes.Reachable))).EnsureSuccessStatusCode();
        var source = Fixture.Services.GetRequiredService<PanelSnapshotRepository>();
        var snapshot = source.BuildSnapshot("address-standby");
        var backup = Fixture.Services.GetRequiredService<FleetBackupService>().Export("test");
        Assert.Equal(snapshot.EgressPeers, backup.Snapshot.EgressPeers);
        Assert.Contains(snapshot.EgressPeers!, path => path.ProbeId == probe.NodeId && path.NodeId == node.Id);
        using var standby = new ControlPlaneFixture();
        _ = await standby.CreateAdminClientAsync();
        var replica = standby.Services.GetRequiredService<PanelSnapshotRepository>();
        replica.ApplySnapshot(snapshot);
        replica.ApplyStream(source.BuildStream("address-standby", PanelStreamCursors.Beginning, 10000));
        var restoredNode = standby.Services.GetRequiredService<NodeRepository>().Find(node.Id)!;
        Assert.Equal(new[] { Primary, Secondary }, NodeIngress.Addresses(restoredNode));
        var checks = standby.Services.GetRequiredService<AddressCheckService>().Describe(restoredNode, DateTimeOffset.UtcNow);
        Assert.Equal(ProbeOutcomes.Reachable, checks.Egress.Single(row => row.Address == target.EgressAddress).Outcome);
        Assert.Equal(snapshot.EgressPeers, replica.BuildSnapshot("again").EgressPeers);
    }
}

public class AddressEchoParsingTests
{
    [Theory]
    [InlineData("91.132.59.9\n", "91.132.59.9")]
    [InlineData("fl=12\nip=157.228.112.119\nts=123\n", "157.228.112.119")]
    [InlineData("<html>service unavailable</html>", null)]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("127.0.0.1", null)]
    public void Reads_source_ipv4_addresses_from_echo_responses(string body, string? expected)
        => Assert.Equal(expected, TunnelTrafficCheck.ObservedAddress(body));
}

