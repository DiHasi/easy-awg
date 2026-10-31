using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Who is still on a node, through the real endpoints.
///
/// The question is asked with a hand on the power switch, and the answer has to survive the two
/// things that make it hard: every node reports every peer, so most of what the panel holds is
/// nodes saying they have never seen somebody; and a peer that moved leaves its old handshake
/// behind on the node it came from, for good.
/// </summary>
public class NodePeersTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private async Task<(TestAgent Agent, HttpClient Client)> EnrollAsync(HttpClient admin, string name)
    {
        var token = await TokenAsync(admin, "/api/nodes/tokens", new CreateNodeRequest(name, null, null, null));
        var client = Fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(client, token.Token, hostname: name);
        return (agent, client);
    }

    private static async Task ReportAsync(HttpClient client, TestAgent agent, params PeerStatus[] peers)
    {
        var report = new NodeStatusReport(
            AppliedRevision: 0,
            InterfaceUp: true,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: peers,
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            PublicIp: "203.0.113.210");

        var response = await client.SendAsync(agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();
    }

    private static async Task<ClientResponse> PeerAsync(HttpClient admin, string name)
    {
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<NodePeersResponse> PeersOnAsync(HttpClient admin, string nodeId)
        => (await admin.GetFromJsonAsync<NodePeersResponse>($"/api/nodes/{nodeId}/peers"))!;

    /// <summary>
    /// The case the feature exists for: the record has been repointed, the peer has not
    /// reconnected, and the panel has to say that the node an operator wants to switch off is
    /// still carrying somebody.
    /// </summary>
    [Fact]
    public async Task A_peer_is_credited_to_the_node_that_saw_its_newest_handshake()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "peers-moved");
        var (first, firstClient) = await EnrollAsync(admin, "peers-node-first");
        var (second, secondClient) = await EnrollAsync(admin, "peers-node-second");

        var now = DateTimeOffset.UtcNow;
        await ReportAsync(firstClient, first, new PeerStatus(peer.PublicKey, now.AddSeconds(-20), 1_000, 100));
        // The node it has not reached: it carries the peer in its configuration and has never seen
        // it, which is what most of this table looks like.
        await ReportAsync(secondClient, second, new PeerStatus(peer.PublicKey, null, 0, 0));

        var carrying = await PeersOnAsync(admin, first.NodeId!);
        Assert.Equal(1, carrying.Online);
        Assert.Equal(0, carrying.Idle);
        Assert.Equal(peer.Id, Assert.Single(carrying.Peers).Id);
        Assert.Equal(peer.Address, carrying.Peers[0].Address);
        Assert.True(carrying.Peers[0].Online);
        Assert.NotNull(carrying.EvidenceAt);

        Assert.Empty((await PeersOnAsync(admin, second.NodeId!)).Peers);

        // And the same reading on the card, so the number and the list cannot disagree.
        var state = await NodeStateAsync(admin, first.NodeId!);
        Assert.Equal(1, state.PeersOnline);
        Assert.Equal(0, state.PeersIdle);

        // Then it reconnects to the other node, and the first keeps its stale handshake forever.
        await ReportAsync(secondClient, second, new PeerStatus(peer.PublicKey, DateTimeOffset.UtcNow, 10, 1));

        Assert.Equal(0, (await PeersOnAsync(admin, first.NodeId!)).Online);
        Assert.Equal(1, (await PeersOnAsync(admin, second.NodeId!)).Online);
    }

    /// <summary>
    /// A handshake older than the window is not somebody who has gone: it is the device whose
    /// client is still pointed here and will find nothing when it wakes up. That is the number a
    /// shutdown costs, so it has to be carried separately rather than rounded to zero.
    /// </summary>
    [Fact]
    public async Task A_peer_that_has_gone_quiet_still_counts_as_pointed_at_the_node()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "peers-asleep");
        var (agent, client) = await EnrollAsync(admin, "peers-node-quiet");

        await ReportAsync(client, agent, new PeerStatus(peer.PublicKey, DateTimeOffset.UtcNow.AddMinutes(-40), 5, 5));

        var carrying = await PeersOnAsync(admin, agent.NodeId!);
        Assert.Equal(0, carrying.Online);
        Assert.Equal(1, carrying.Idle);
        Assert.False(Assert.Single(carrying.Peers).Online);
    }

    /// <summary>
    /// Revoking stops configuration reaching a node, not traffic leaving through it, which is why
    /// a rotation warns about exactly this. The count is what turns that warning into a number.
    /// </summary>
    [Fact]
    public async Task A_revoked_node_still_says_who_it_is_carrying()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "peers-on-revoked");
        var (agent, client) = await EnrollAsync(admin, "peers-node-revoked");

        await ReportAsync(client, agent, new PeerStatus(peer.PublicKey, DateTimeOffset.UtcNow.AddSeconds(-10), 7, 7));
        (await admin.PostAsync($"/api/nodes/{agent.NodeId}/revoke", null)).EnsureSuccessStatusCode();

        var carrying = await PeersOnAsync(admin, agent.NodeId!);
        Assert.Equal(1, carrying.Online);
        Assert.Equal(peer.Id, Assert.Single(carrying.Peers).Id);
    }

    /// <summary>
    /// The peer list refuses to call a disabled peer online, so a node claiming to carry one would
    /// be the panel contradicting itself on two pages about the same person.
    /// </summary>
    [Fact]
    public async Task A_disabled_peer_is_counted_nowhere()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "peers-disabled");
        var (agent, client) = await EnrollAsync(admin, "peers-node-disabled");

        await ReportAsync(client, agent, new PeerStatus(peer.PublicKey, DateTimeOffset.UtcNow.AddSeconds(-10), 3, 3));
        Assert.Equal(1, (await PeersOnAsync(admin, agent.NodeId!)).Online);

        (await admin.PostAsync($"/api/clients/{peer.Id}/disable", null)).EnsureSuccessStatusCode();

        var carrying = await PeersOnAsync(admin, agent.NodeId!);
        Assert.Equal(0, carrying.Online);
        Assert.Empty(carrying.Peers);
    }

    [Fact]
    public async Task Asking_about_a_node_that_is_not_there_is_a_not_found()
    {
        var admin = await AdminAsync();
        var response = await admin.GetAsync("/api/nodes/ghost/peers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("node_not_found", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }
}
