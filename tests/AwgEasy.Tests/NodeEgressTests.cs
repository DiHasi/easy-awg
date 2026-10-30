using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Letting one node hand out several source addresses, so the people who need an address nobody
/// has geo-flagged can have one while everybody else keeps the address they are already known at.
///
/// The interesting part is the indirection. A peer picks a label and a node is handed addresses:
/// labels belong to the panel, because an address belongs to one node and a client has to keep
/// working on any of them. So what these cover is that the resolution happens here, that a label
/// no node offers costs a peer nothing but its pin, and that a change moves the revision - an
/// unbumped revision is a 304 and the node this is about never hears.
/// </summary>
public class NodeEgressTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private static readonly ServerObfuscationProfile Schema3Profile = new()
    {
        S1 = 16,
        S2 = 16,
        S3 = 16,
        S4 = 16,
        H1 = "1000000-1000500",
        HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
        RandomTrailers = true
    };

    private async Task<(TestAgent Agent, HttpClient Client)> EnrollAsync(HttpClient admin, string name)
    {
        var token = await TokenAsync(admin, "/api/nodes/tokens", new CreateNodeRequest(name, null, null, null));
        var client = Fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(client, token.Token, hostname: name);
        return (agent, client);
    }

    private static async Task<SignedBundle> EnvelopeAsync(HttpClient client, TestAgent agent)
    {
        var response = await client.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SignedBundle>())!;
    }

    private static async Task<DesiredStateBundle> BundleAsync(HttpClient client, TestAgent agent)
        => agent.AcceptBundle(await EnvelopeAsync(client, agent));

    private static async Task<ClientResponse> PeerAsync(HttpClient admin, string name, string? label = null)
    {
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null, null, label));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<long> RevisionAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

    /// <summary>
    /// Reports a status with a schema of the agent's choosing, which is how an older agent is
    /// described here: the panel stores what was reported and serves each node at that schema.
    /// </summary>
    private static async Task ReportAsync(
        HttpClient client,
        TestAgent agent,
        int schema = DesiredStateBundle.CurrentSchemaVersion,
        ReportedEgressAddress[]? addresses = null)
    {
        var report = new NodeStatusReport(
            AppliedRevision: 0,
            InterfaceUp: true,
            Backend: "Kernel module",
            AgentVersion: "test-agent/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [],
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: schema,
            PublicIp: "203.0.113.200",
            EgressAddresses: addresses);

        var response = await client.SendAsync(agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{agent.NodeId}/status",
            JsonContent.Create(report)));

        response.EnsureSuccessStatusCode();
    }

    private static async Task<NodeResponse> SetPlanAsync(HttpClient admin, string nodeId, string? defaultAddress, params EgressLabel[] labels)
    {
        var response = await admin.PutAsJsonAsync($"/api/nodes/{nodeId}/egress", new UpdateNodeEgressRequest(defaultAddress, labels));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeResponse>())!;
    }

    [Fact]
    public async Task A_node_is_handed_addresses_and_never_the_labels_they_were_chosen_by()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollAsync(admin, "egress-resolved");
        await SetPlanAsync(admin, agent.NodeId!, "203.0.113.9", new EgressLabel("203.0.113.5", "clean"));

        var peer = await PeerAsync(admin, "egress-pinned-peer", "clean");

        var envelope = await EnvelopeAsync(client, agent);
        var bundle = agent.AcceptBundle(envelope);

        Assert.Equal("203.0.113.9", bundle.Node.DefaultEgressAddress);
        Assert.Equal("203.0.113.5", bundle.Peers.Single(p => p.PublicKey == peer.PublicKey).EgressAddress);

        // The label is the panel's own vocabulary. A node needs the address and nothing else, and
        // a word an operator chose is as likely to name the person who needed it as anything.
        var payload = Encoding.UTF8.GetString(Base64Url.Decode(envelope.Payload));
        Assert.DoesNotContain("clean", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason a peer picks a label rather than an address: after failover it lands on a
    /// node that never had the address it was given. Falling back to that node's default is the
    /// behaviour - it costs the peer the property it asked for, never its traffic.
    /// </summary>
    [Fact]
    public async Task A_label_a_node_has_no_address_for_leaves_that_peer_on_the_node_default()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollAsync(admin, "egress-unlabelled");
        await SetPlanAsync(admin, agent.NodeId!, "203.0.113.19");

        var peer = await PeerAsync(admin, "egress-asks-elsewhere", "residential");

        var bundle = await BundleAsync(client, agent);

        Assert.Equal("203.0.113.19", bundle.Node.DefaultEgressAddress);
        Assert.Null(bundle.Peers.Single(p => p.PublicKey == peer.PublicKey).EgressAddress);
    }

    [Fact]
    public async Task A_peer_that_asked_for_nothing_is_handed_nothing_of_its_own()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollAsync(admin, "egress-plain-peer");
        var peer = await PeerAsync(admin, "egress-unpinned-peer");

        var bundle = await BundleAsync(client, agent);

        Assert.Null(bundle.Node.DefaultEgressAddress);
        Assert.Null(bundle.Peers.Single(p => p.PublicKey == peer.PublicKey).EgressAddress);
    }

    [Fact]
    public async Task Changing_a_nodes_plan_bumps_the_revision_and_a_save_that_changes_nothing_does_not()
    {
        var admin = await AdminAsync();
        var (agent, _) = await EnrollAsync(admin, "egress-revision");

        var before = await RevisionAsync(admin);
        await SetPlanAsync(admin, agent.NodeId!, "203.0.113.29", new EgressLabel("203.0.113.30", "clean"));
        var bumped = await RevisionAsync(admin);

        Assert.True(bumped > before);

        // The bundle is served against an ETag of the revision, so the node only hears about a
        // change that moved it - and only about one that happened.
        await SetPlanAsync(admin, agent.NodeId!, "203.0.113.29", new EgressLabel("203.0.113.30", "clean"));
        Assert.Equal(bumped, await RevisionAsync(admin));
    }

    [Fact]
    public async Task Changing_a_peers_egress_bumps_the_revision_unlike_renaming_it()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "egress-peer-moved");

        var before = await RevisionAsync(admin);
        var updated = await admin.PutAsJsonAsync($"/api/clients/{peer.Id}/egress", new UpdateClientEgressRequest("clean"));
        updated.EnsureSuccessStatusCode();

        Assert.Equal("clean", (await updated.Content.ReadFromJsonAsync<ClientResponse>())!.EgressLabel);
        Assert.True(await RevisionAsync(admin) > before);

        var unpinned = await admin.PutAsJsonAsync($"/api/clients/{peer.Id}/egress", new UpdateClientEgressRequest(null));
        unpinned.EnsureSuccessStatusCode();
        Assert.Null((await unpinned.Content.ReadFromJsonAsync<ClientResponse>())!.EgressLabel);
    }

    /// <summary>
    /// A peer may name a label no node offers yet, and that is deliberate: a node that has not
    /// reported its addresses, or has not been given a plan, must not make the reason a config is
    /// being issued unexpressible. Such a peer leaves through the default until one does.
    /// </summary>
    [Fact]
    public async Task A_label_no_node_offers_is_still_accepted()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "egress-ahead-of-the-nodes", "not-configured-anywhere");

        Assert.Equal("not-configured-anywhere", peer.EgressLabel);
    }

    [Theory]
    // Not an address at all, and an address no packet could carry out of a host.
    [InlineData("ens3", null, null, "node_egress_address_invalid")]
    [InlineData("127.0.0.1", null, null, "node_egress_address_invalid")]
    [InlineData("::1", null, null, "node_egress_address_invalid")]
    [InlineData(null, "203.0.113.5", "rm -rf /", "node_egress_label_invalid")]
    public async Task A_plan_a_node_could_not_apply_is_refused_here(
        string? defaultAddress,
        string? labelAddress,
        string? label,
        string code)
    {
        var admin = await AdminAsync();
        var (agent, _) = await EnrollAsync(admin, $"egress-refused-{code}-{labelAddress ?? defaultAddress}");

        EgressLabel[] labels = labelAddress is null ? [] : [new EgressLabel(labelAddress, label!)];
        var response = await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}/egress",
            new UpdateNodeEgressRequest(defaultAddress, labels));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// A label has to name one address, or "send this peer through clean" has no answer. The same
    /// for an address under two names: the panel would have to pick one and the operator would
    /// never know which.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.5", "clean", "203.0.113.6", "clean")]
    [InlineData("203.0.113.5", "clean", "203.0.113.5", "spare")]
    public async Task A_label_or_an_address_twice_on_one_node_is_refused(string first, string firstLabel, string second, string secondLabel)
    {
        var admin = await AdminAsync();
        var (agent, _) = await EnrollAsync(admin, $"egress-duplicate-{secondLabel}");

        var response = await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}/egress",
            new UpdateNodeEgressRequest(null, [new EgressLabel(first, firstLabel), new EgressLabel(second, secondLabel)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("node_egress_duplicate", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task A_label_a_peer_could_not_be_sent_through_is_refused_too()
    {
        var admin = await AdminAsync();
        var peer = await PeerAsync(admin, "egress-bad-label");

        var response = await admin.PutAsJsonAsync(
            $"/api/clients/{peer.Id}/egress",
            new UpdateClientEgressRequest(new string('x', 40)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client_egress_label_invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// Which addresses a node carries is a thing only the node can answer, so the panel offers an
    /// operator what was reported and nothing else. A round that could not read them reports null
    /// and the last known list stands - forgetting them over one failed lookup would empty the
    /// dialog an operator is in the middle of using.
    /// </summary>
    [Fact]
    public async Task The_addresses_a_node_reports_are_what_the_panel_has_to_offer()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollAsync(admin, "egress-reported");

        await ReportAsync(client, agent, addresses:
        [
            new ReportedEgressAddress("203.0.113.40", KernelDefault: true, Observed: "203.0.113.40"),
            new ReportedEgressAddress("203.0.113.41", Observed: "198.51.100.8")
        ]);

        var reported = (await NodeStateAsync(admin, agent.NodeId!)).EgressAddresses;
        Assert.Equal(["203.0.113.40", "203.0.113.41"], reported.Select(address => address.Address));
        Assert.True(reported[0].KernelDefault);

        // The second address presents as something else entirely, which is the question pinning
        // anybody to an address is asked in order to answer.
        Assert.Equal("198.51.100.8", reported[1].Observed);

        await ReportAsync(client, agent);
        Assert.Equal(2, (await NodeStateAsync(admin, agent.NodeId!)).EgressAddresses.Length);
    }

    /// <summary>
    /// The schema bump that carried the egress fields must not change what a node one release
    /// behind is served. It gets no plan - its agent would ignore it - and it keeps the 3.x
    /// profile, which is the thing its clients cannot handshake without: those two decisions used
    /// to be the same comparison, and were only ever right because the numbers matched.
    /// </summary>
    [Fact]
    public async Task A_node_a_schema_behind_gets_no_plan_and_keeps_the_3_x_profile()
    {
        var admin = await AdminAsync();
        (await admin.PutAsJsonAsync("/api/fleet/obfuscation", Schema3Profile)).EnsureSuccessStatusCode();

        var (agent, client) = await EnrollAsync(admin, "egress-older-agent");
        await SetPlanAsync(admin, agent.NodeId!, "203.0.113.49", new EgressLabel("203.0.113.50", "clean"));
        await PeerAsync(admin, "egress-peer-of-older-agent", "clean");

        await ReportAsync(client, agent, schema: BundleSchema.Schema3ObfuscationVersion);

        var bundle = await BundleAsync(client, agent);

        Assert.Equal(BundleSchema.Schema3ObfuscationVersion, bundle.SchemaVersion);
        Assert.Null(bundle.Node.DefaultEgressAddress);
        Assert.All(bundle.Peers, peer => Assert.Null(peer.EgressAddress));

        // Still serving this fleet's wire format: the plan is what it cannot apply, not the
        // profile every one of its clients needs.
        Assert.Equal(Schema3Profile.HeaderProtectionKey, bundle.Obfuscation!.HeaderProtectionKey);
        Assert.True(bundle.Obfuscation.RandomTrailers);

        // And the panel says so rather than leaving an operator to wonder why the plan is set and
        // nothing changed.
        Assert.False((await NodeStateAsync(admin, agent.NodeId!)).SupportsCurrentSchema);
    }
}
