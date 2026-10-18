using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The per-node half of a node's configuration, which used to be real in the schema and in
/// <c>FleetService.BuildBundle</c> with no way in from outside: the create form carried the three
/// fields and the token endpoint dropped them.
///
/// Two of the three reach the node inside its bundle and are applied in one shot, so the
/// interesting cases are that they arrive at all, that a value `awg` would refuse is refused here
/// instead, and that changing one moves the fleet revision while renaming a node does not.
/// </summary>
public class NodeSettingsTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private async Task<(TestAgent Agent, HttpClient Client)> EnrollWithAsync(HttpClient admin, CreateNodeRequest request)
    {
        var token = await TokenAsync(admin, "/api/nodes/tokens", request);
        var client = Fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(client, token.Token, hostname: request.Name);
        return (agent, client);
    }

    private static async Task<DesiredStateBundle> BundleAsync(HttpClient client, TestAgent agent)
    {
        var response = await client.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        return agent.AcceptBundle((await response.Content.ReadFromJsonAsync<SignedBundle>())!);
    }

    private static async Task<long> RevisionAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

    [Fact]
    public async Task Settings_given_at_enrollment_are_in_the_first_bundle_the_node_fetches()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollWithAsync(
            admin,
            new CreateNodeRequest("settings-born-with", "ams.vpn.example.com", "ens3", 1300));

        var bundle = await BundleAsync(client, agent);

        // Before this, a node that needed a narrower MTU had to be corrected after it had already
        // brought an interface up at the fleet value.
        Assert.Equal("ens3", bundle.Node.EgressInterface);
        Assert.Equal(1300, bundle.Node.Mtu);

        var node = await NodeStateAsync(admin, agent.NodeId!);
        Assert.Equal("ams.vpn.example.com", node.EndpointHost);
        Assert.Equal("ens3", node.EgressInterface);
        Assert.Equal(1300, node.Mtu);
    }

    /// <summary>
    /// No override means the fleet value, not an empty one: a node that says nothing must run what
    /// the rest of the fleet runs, and its agent must keep detecting its own egress interface.
    /// </summary>
    [Fact]
    public async Task A_node_without_overrides_still_runs_the_fleet_value()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollWithAsync(admin, new CreateNodeRequest("settings-plain", null, null, null));

        var bundle = await BundleAsync(client, agent);
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        Assert.Null(bundle.Node.EgressInterface);
        Assert.Equal(fleet.TunnelMtu, bundle.Node.Mtu);
    }

    [Fact]
    public async Task Changing_the_mtu_of_one_node_changes_what_that_node_runs_and_bumps_the_revision()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollWithAsync(admin, new CreateNodeRequest("settings-narrowed", null, null, null));

        var before = await RevisionAsync(admin);
        var updated = await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}",
            new UpdateNodeRequest("settings-narrowed", null, "enp1s0", 1320));

        updated.EnsureSuccessStatusCode();

        // The bundle is served against an ETag of the revision, so a change a node has to hear
        // about has to move it - and the node takes its interface down once to apply the MTU.
        Assert.True(await RevisionAsync(admin) > before);

        var bundle = await BundleAsync(client, agent);
        Assert.Equal("enp1s0", bundle.Node.EgressInterface);
        Assert.Equal(1320, bundle.Node.Mtu);
    }

    /// <summary>
    /// A rename changes no peer material and nothing any node runs, exactly like renaming a client:
    /// the revision is what tells every node to re-apply, and spending it on a label would restart
    /// work across the fleet for the operator's own bookkeeping.
    /// </summary>
    [Fact]
    public async Task Renaming_a_node_does_not_bump_the_revision()
    {
        var admin = await AdminAsync();
        var (agent, _) = await EnrollWithAsync(admin, new CreateNodeRequest("settings-before-rename", null, null, null));

        var before = await RevisionAsync(admin);
        var updated = await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}",
            new UpdateNodeRequest("settings-after-rename", "edge.vpn.example.com", null, null));

        updated.EnsureSuccessStatusCode();

        Assert.Equal(before, await RevisionAsync(admin));
        var node = (await updated.Content.ReadFromJsonAsync<NodeResponse>())!;
        Assert.Equal("settings-after-rename", node.Name);
        Assert.Equal("edge.vpn.example.com", node.EndpointHost);
    }

    /// <summary>
    /// A node applies its bundle in one shot, so a value `awg` or `iptables` refuses does not
    /// produce a complaint - it produces a node whose interface does not come up at all. These are
    /// refused in the panel, where someone is looking.
    /// </summary>
    [Theory]
    [InlineData("eth0; rm -rf /", null, "node_egress_interface_invalid")]
    [InlineData("an-interface-name-far-too-long", null, "node_egress_interface_invalid")]
    [InlineData(null, 1000, "invalid_mtu")]
    [InlineData(null, 9000, "invalid_mtu")]
    public async Task A_setting_a_node_could_not_apply_is_refused_here(string? egress, int? mtu, string code)
    {
        var admin = await AdminAsync();
        var response = await admin.PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("settings-refused", null, egress, mtu));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task The_same_checks_apply_to_a_node_that_already_exists()
    {
        var admin = await AdminAsync();
        var (agent, _) = await EnrollWithAsync(admin, new CreateNodeRequest("settings-checked-again", null, null, null));

        var response = await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}",
            new UpdateNodeRequest("settings-checked-again", null, "eth0 eth1", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("node_egress_interface_invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Clearing_an_override_puts_the_node_back_on_the_fleet_value()
    {
        var admin = await AdminAsync();
        var (agent, client) = await EnrollWithAsync(admin, new CreateNodeRequest("settings-cleared", null, "ens3", 1300));

        (await admin.PutAsJsonAsync(
            $"/api/nodes/{agent.NodeId}",
            new UpdateNodeRequest("settings-cleared", null, "   ", null))).EnsureSuccessStatusCode();

        var bundle = await BundleAsync(client, agent);
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        // A blank field means "leave it to the fleet", not an interface named nothing.
        Assert.Null(bundle.Node.EgressInterface);
        Assert.Equal(fleet.TunnelMtu, bundle.Node.Mtu);
    }

    [Fact]
    public async Task Editing_a_node_that_is_not_there_is_a_not_found()
    {
        var admin = await AdminAsync();
        var response = await admin.PutAsJsonAsync("/api/nodes/ghost", new UpdateNodeRequest("ghost", null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("node_not_found", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Editing_a_node_anonymously_is_rejected()
    {
        var response = await Fixture.CreateClient()
            .PutAsJsonAsync("/api/nodes/whatever", new UpdateNodeRequest("whatever", null, null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
