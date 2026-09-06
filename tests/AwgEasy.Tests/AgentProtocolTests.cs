using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using AwgEasy.Node;

namespace AwgEasy.Tests;

/// <summary>
/// End-to-end coverage of the loop the whole design rests on: an operator issues a token, an
/// agent enrolls, pulls a signed bundle, and can turn it into an interface config.
/// </summary>
public class AgentProtocolTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<(HttpClient Admin, HttpClient Agent, TestAgent Node)> EnrolledAgentAsync(string nodeName)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest(nodeName, null, null, null));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(agentClient, token.Token);

        return (admin, agentClient, agent);
    }

    [Fact]
    public async Task An_enrolled_agent_pulls_a_bundle_it_can_verify_and_render()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-render");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("laptop", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();

        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;
        var bundle = agent.AcceptBundle(envelope);

        Assert.Equal(agent.NodeId, bundle.NodeId);
        Assert.Contains(bundle.Peers, peer => peer.PublicKey == client.PublicKey && peer.Address == client.Address);

        // The bundle must be enough on its own to produce a working interface config.
        var config = ServerConfigRenderer.Render(bundle, "ens3");
        Assert.Contains("[Interface]", config);
        // Host .1 of the subnet is the tunnel gateway every node holds.
        Assert.Contains("Address = 10.8.0.1/24", config);
        Assert.Contains($"AllowedIPs = {client.Address}/32", config);
    }

    // A node holds the fleet private key; it must never also learn who the clients are or hold
    // anything that would let someone impersonate them.
    [Fact]
    public async Task The_bundle_carries_no_client_private_keys_or_names()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-secrets");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("alice-phone", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;

        var payload = System.Text.Encoding.UTF8.GetString(Base64Url.Decode(envelope.Payload));
        Assert.DoesNotContain("alice-phone", payload);
        Assert.DoesNotContain("PRIV", payload[payload.IndexOf("peers", StringComparison.Ordinal)..]);

        var bundle = agent.AcceptBundle(envelope);
        Assert.Contains(bundle.Peers, peer => peer.PublicKey == client.PublicKey);
    }

    [Fact]
    public async Task Disabling_a_client_removes_its_peer_and_advances_the_revision()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-disable");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("tablet", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var before = agent.AcceptBundle((await Fetch(agentClient, agent))!);
        Assert.Contains(before.Peers, peer => peer.PublicKey == client.PublicKey);

        (await admin.PostAsync($"/api/clients/{client.Id}/disable", null)).EnsureSuccessStatusCode();

        var after = agent.AcceptBundle((await Fetch(agentClient, agent))!, appliedRevision: before.Revision);
        Assert.DoesNotContain(after.Peers, peer => peer.PublicKey == client.PublicKey);
        Assert.True(after.Revision > before.Revision);
    }

    [Fact]
    public async Task An_unchanged_fleet_answers_not_modified()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-etag");

        var first = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        var etag = first.Headers.ETag!.Tag;

        var request = agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        Assert.Equal(HttpStatusCode.NotModified, (await agentClient.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task An_unsigned_request_is_rejected()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-unsigned");

        var response = await agentClient.GetAsync($"/api/v1/agents/{agent.NodeId}/desired");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_signed_by_the_wrong_key_is_rejected()
    {
        var (_, agentClient, agent) = await EnrolledAgentAsync("node-wrongkey");

        // A different agent signs, but claims to be the enrolled node.
        using var impostor = new TestAgent();
        var response = await agentClient.SendAsync(
            impostor.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired", asNodeId: agent.NodeId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_node_loses_access_on_its_very_next_request()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-revoked");

        (await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired")))
            .EnsureSuccessStatusCode();

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        var afterRevoke = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task An_enrollment_token_cannot_be_used_twice()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-once", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        using var first = new TestAgent();
        await first.EnrollAsync(agentClient, token.Token);

        using var second = new TestAgent();
        var response = await agentClient.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token.Token, "impostor", second.PublicKey, "test-agent/1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_node_cannot_fetch_the_bundle_of_another_node()
    {
        var (_, agentClient, first) = await EnrolledAgentAsync("node-a");
        var (_, _, second) = await EnrolledAgentAsync("node-b");

        // Correctly signed by the first node, but aimed at the second node's path.
        var response = await agentClient.SendAsync(first.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{second.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_status_report_updates_the_node_and_aggregates_client_traffic()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-status");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("desktop", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var report = new NodeStatusReport(
            AppliedRevision: 7,
            InterfaceUp: true,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [new PeerStatus(client.PublicKey, DateTimeOffset.UtcNow, 1024, 2048)],
            Metrics: null,
            LastError: null);

        var response = await agentClient.SendAsync(agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();

        var nodes = (await admin.GetFromJsonAsync<NodeResponse[]>("/api/nodes"))!;
        var node = nodes.Single(n => n.Id == agent.NodeId);
        Assert.True(node.InterfaceUp);
        Assert.Equal("healthy", node.Status);
        Assert.Equal(7, node.AppliedRevision);

        var stats = (await admin.GetFromJsonAsync<ClientStatsResponse[]>("/api/clients/stats"))!;
        var clientStats = stats.Single(s => s.Id == client.Id);
        Assert.True(clientStats.Online);
        Assert.Equal(1024, clientStats.ReceivedBytes);
        Assert.Equal(agent.NodeId, clientStats.NodeId);
    }

    // An operator who revokes a node, then deletes it, must not be left with a server that can
    // never rejoin. A fresh token is the authorization to adopt it again.
    [Fact]
    public async Task A_deleted_node_can_rejoin_with_a_fresh_token()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-rejoin");
        var originalNodeId = agent.NodeId;

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/nodes/{agent.NodeId}")).EnsureSuccessStatusCode();

        var rejected = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // The same agent, same key pair, new token - exactly what re-running the installer does.
        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-rejoin-again", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;
        await agent.EnrollAsync(agentClient, token.Token);

        Assert.NotEqual(originalNodeId, agent.NodeId);

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
    }

    // Revoking without deleting is a deliberate act; enrolling over it should say so rather than
    // failing on a database constraint.
    [Fact]
    public async Task Enrolling_a_server_that_is_still_registered_is_refused_with_a_reason()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("node-still-there");

        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        var tokenResponse = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("node-duplicate", null, null, null));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var response = await agentClient.PostAsJsonAsync(
            "/api/v1/agents/enroll",
            new EnrollRequest(token.Token, "same-server", agent.PublicKey, "test-agent/1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<AwgEasy.Contracts.ApiError>())!;
        Assert.Equal("enrollment_key_registered", error.Code);
        Assert.Contains("Delete that node", error.Message);
    }

    private static async Task<SignedBundle?> Fetch(HttpClient client, TestAgent agent)
    {
        var response = await client.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedBundle>();
    }
}
