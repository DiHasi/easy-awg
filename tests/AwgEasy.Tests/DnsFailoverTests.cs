using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Manual failover, end to end through the real panel: an agent reports where it is reachable, an
/// operator points the record at it, and the panel's claim about where clients go stays true even
/// when the provider refuses.
/// </summary>
public class DnsFailoverTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<(HttpClient Admin, HttpClient AgentClient, TestAgent Agent)> NodeReportingAsync(
        string nodeName,
        string? publicIp)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest(nodeName, null, null, null));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(agentClient, token.Token, hostname: nodeName);

        if (publicIp is not null)
        {
            await ReportAsync(agentClient, agent, publicIp);
        }

        return (admin, agentClient, agent);
    }

    private static async Task ReportAsync(HttpClient agentClient, TestAgent agent, string? publicIp)
    {
        var report = new NodeStatusReport(
            AppliedRevision: 1,
            InterfaceUp: true,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [],
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            PublicIp: publicIp);

        var response = await agentClient.SendAsync(agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();
    }

    private static async Task<NodeResponse> NodeAsync(HttpClient admin, string? nodeId)
    {
        var nodes = (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!;
        return nodes.Single(node => node.Id == nodeId);
    }

    [Fact]
    public async Task A_node_reports_where_it_is_reachable()
    {
        var (admin, _, agent) = await NodeReportingAsync("dns-reports", "203.0.113.9");

        Assert.Equal("203.0.113.9", (await NodeAsync(admin, agent.NodeId)).PublicIp);
    }

    // A lookup that failed this cycle says nothing about where the node is; forgetting the address
    // would quietly drop it out of the failover rotation.
    [Fact]
    public async Task A_report_without_an_address_keeps_the_last_one()
    {
        var (admin, agentClient, agent) = await NodeReportingAsync("dns-keeps", "198.51.100.7");

        await ReportAsync(agentClient, agent, publicIp: null);

        Assert.Equal("198.51.100.7", (await NodeAsync(admin, agent.NodeId)).PublicIp);
    }

    [Fact]
    public async Task A_node_with_no_known_address_cannot_be_made_active()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-unknown", publicIp: null);

        var response = await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiError>())!;
        Assert.Equal("node_public_ip_unknown", error.Code);
        Assert.False((await NodeAsync(admin, agent.NodeId)).IsActive);
    }

    [Fact]
    public async Task Activating_a_node_points_the_record_at_the_address_it_reported()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-activate", "203.0.113.20");

        var response = await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null);
        response.EnsureSuccessStatusCode();

        var status = (await response.Content.ReadFromJsonAsync<DnsStatusResponse>())!;
        Assert.Equal(agent.NodeId, status.ActiveNodeId);
        Assert.Equal("203.0.113.20", status.TargetAddress);
        // Unset AWG_DNS_RECORD_NAME means the record to move is the host clients already connect to.
        Assert.Equal("vpn.example.com", status.RecordName);
        Assert.Equal("A", status.RecordType);
        Assert.NotNull(status.ActivatedAt);

        var applied = fixture.Dns.Last!;
        Assert.Equal("vpn.example.com", applied.Name);
        Assert.Equal("203.0.113.20", applied.Address);
        Assert.Equal(60, applied.Ttl);

        Assert.True((await NodeAsync(admin, agent.NodeId)).IsActive);

        var events = (await admin.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        Assert.Contains(events, entry => entry.Kind == "node.activated" && entry.NodeId == agent.NodeId);
    }

    // The point of sharing one fleet identity: switching endpoints changes nothing a node runs and
    // nothing a client holds, so the revision every node compares itself against must not move.
    [Fact]
    public async Task Activating_a_node_does_not_change_what_nodes_run()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-revision", "203.0.113.30");

        var before = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null)).EnsureSuccessStatusCode();
        var after = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Only_one_node_is_active_at_a_time()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, first) = await NodeReportingAsync("dns-first", "203.0.113.41");
        var (_, _, second) = await NodeReportingAsync("dns-second", "203.0.113.42");

        (await admin.PostAsync($"/api/nodes/{first.NodeId}/activate", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/nodes/{second.NodeId}/activate", null)).EnsureSuccessStatusCode();

        Assert.False((await NodeAsync(admin, first.NodeId)).IsActive);
        Assert.True((await NodeAsync(admin, second.NodeId)).IsActive);
        Assert.Equal("203.0.113.42", fixture.Dns.Last!.Address);
    }

    // The stored active node means "this is where the record points". If the provider refused, it
    // does not point there, and the panel must not say otherwise.
    [Fact]
    public async Task A_provider_that_refuses_leaves_the_active_node_where_it_was()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, held) = await NodeReportingAsync("dns-held", "203.0.113.51");
        var (_, _, rejected) = await NodeReportingAsync("dns-rejected", "203.0.113.52");

        (await admin.PostAsync($"/api/nodes/{held.NodeId}/activate", null)).EnsureSuccessStatusCode();

        fixture.Dns.Outcome = DnsUpdateOutcome.Failed;
        var response = await admin.PostAsync($"/api/nodes/{rejected.NodeId}/activate", null);
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("dns_update_failed", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);

        Assert.True((await NodeAsync(admin, held.NodeId)).IsActive);
        Assert.False((await NodeAsync(admin, rejected.NodeId)).IsActive);
    }

    [Fact]
    public async Task A_revoked_node_cannot_be_made_active()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-revoked", "203.0.113.60");

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();
        var response = await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("node_revoked", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Activating_an_unknown_node_is_a_not_found()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.PostAsync("/api/nodes/does-not-exist/activate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_status_says_whether_the_record_resolves_to_the_active_node()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-resolves", "203.0.113.70");
        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null)).EnsureSuccessStatusCode();

        fixture.Resolver.Addresses = ["203.0.113.70"];
        var agreeing = (await admin.GetFromJsonAsync<DnsStatusResponse>("/api/dns"))!;
        Assert.True(agreeing.Matches);
        Assert.Null(agreeing.Warning);

        // The case an operator has to see: the record still answers with the node they switched away
        // from, because resolvers are holding the previous answer for its TTL.
        fixture.Resolver.Addresses = ["203.0.113.99"];
        var lagging = (await admin.GetFromJsonAsync<DnsStatusResponse>("/api/dns"))!;
        Assert.False(lagging.Matches);
        Assert.Contains("203.0.113.99", lagging.ResolvedAddresses);

        fixture.Resolver.Addresses = [];
    }

    [Fact]
    public async Task Removing_the_active_node_stops_the_panel_claiming_it()
    {
        fixture.Dns.Outcome = DnsUpdateOutcome.Applied;
        var (admin, _, agent) = await NodeReportingAsync("dns-removed", "203.0.113.80");
        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/activate", null)).EnsureSuccessStatusCode();

        (await admin.DeleteAsync($"/api/nodes/{agent.NodeId}")).EnsureSuccessStatusCode();

        var status = (await admin.GetFromJsonAsync<DnsStatusResponse>("/api/dns"))!;
        Assert.Null(status.ActiveNodeId);
        Assert.Null(status.TargetAddress);
    }
}

/// <summary>
/// A node discovers its own address from an outside service, and the panel writes that value into a
/// record clients follow. Anything unreachable from the internet is a bad target, so it is refused
/// at the point of discovery rather than after clients cannot connect.
/// </summary>
public class PublicIpAddressTests
{
    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("8.8.8.8")]
    [InlineData(" 1.2.3.4\n")]
    [InlineData("2606:4700:4700::1111")]
    public void A_reachable_address_is_accepted(string value)
    {
        Assert.True(PublicIpAddress.TryParse(value, out var normalized));
        Assert.NotEmpty(normalized);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.1")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.7.7")]
    [InlineData("100.64.3.2")]        // carrier-grade NAT: a node behind one is not reachable
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("<html>error</html>")]
    public void An_unreachable_or_unparseable_answer_is_refused(string value)
        => Assert.False(PublicIpAddress.TryParse(value, out _));

    // 100.x outside the carrier-NAT range is ordinary public space and must not be swept up with it.
    [Fact]
    public void The_carrier_nat_range_is_rejected_without_taking_its_neighbours()
    {
        Assert.True(PublicIpAddress.TryParse("100.63.255.255", out _));
        Assert.False(PublicIpAddress.TryParse("100.127.0.1", out _));
        Assert.True(PublicIpAddress.TryParse("100.128.0.1", out _));
    }
}
