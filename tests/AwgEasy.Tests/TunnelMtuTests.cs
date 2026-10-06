using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using AwgEasy.Node;
using Microsoft.Extensions.DependencyInjection;

namespace AwgEasy.Tests;

/// <summary>
/// The tunnel MTU, which is the one fleet setting that is both panel-side and node-side: it is
/// written into every client config and travels in the bundle as <c>NodeSettings.Mtu</c>. So the
/// things worth asserting are that both halves get the same number and that a per-node override
/// is not overwritten by the fleet value. Nothing here moves the setting;
/// <see cref="TunnelMtuSettingTests"/> owns that.
/// </summary>
public class TunnelMtuTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<DesiredStateBundle> BundleAsync(string nodeName, int? nodeMtu)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var tokenResponse = await admin.PostAsJsonAsync(
            "/api/nodes/tokens",
            new CreateNodeRequest(nodeName, null, null, nodeMtu));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;

        var agentClient = fixture.CreateClient();
        var agent = new TestAgent();
        await agent.EnrollAsync(agentClient, token.Token);

        var response = await agentClient.SendAsync(
            agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();

        return agent.AcceptBundle((await response.Content.ReadFromJsonAsync<SignedBundle>())!);
    }

    [Fact]
    public async Task A_new_fleet_starts_on_the_recommended_mtu()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var fleet = await admin.GetFromJsonAsync<FleetResponse>("/api/fleet");

        Assert.Equal(FleetService.DefaultTunnelMtu, fleet!.TunnelMtu);
    }

    /// <summary>
    /// The node half. Left to awg-quick this would be route-MTU minus 80, which does not cover
    /// what AmneziaWG 3.x adds to every transport packet - and a node that renders no MTU line at
    /// all is how a fleet ends up fragmenting in the download direction.
    /// </summary>
    [Fact]
    public async Task A_node_without_an_override_runs_the_fleet_mtu()
    {
        var bundle = await BundleAsync("node-mtu-default", nodeMtu: null);

        Assert.Equal(FleetService.DefaultTunnelMtu, bundle.Node.Mtu);
        Assert.Contains($"MTU = {FleetService.DefaultTunnelMtu}", ServerConfigRenderer.Render(bundle, "ens3"));
    }

    /// <summary>
    /// A node whose own egress is below 1500 needs less, and must not be raised to whatever the
    /// rest of the fleet runs.
    ///
    /// Driven through the node row rather than the API on purpose: `CreateNodeRequest` carries an
    /// `Mtu`, but `EnrollmentService` writes the node with `Mtu: null` regardless, so the override
    /// has no way in from outside yet. The resolution is still what decides what a node runs, so
    /// it is worth pinning here.
    /// </summary>
    [Fact]
    public void A_per_node_override_wins_over_the_fleet_mtu()
    {
        var nodes = fixture.Services.GetRequiredService<NodeRepository>();
        var fleet = fixture.Services.GetRequiredService<FleetService>();

        var node = new NodeRecord(
            Id: Guid.NewGuid().ToString("N"),
            Name: "node-mtu-override",
            Hostname: "override.example.com",
            EndpointHost: null,
            AgentPublicKey: "AGENT_PUBLIC",
            AgentVersion: null,
            Status: NodeStatuses.Provisioning,
            AppliedRevision: 0,
            InterfaceUp: false,
            Backend: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            EgressInterface: null,
            Mtu: 1300,
            PublicIp: null,
            LastSeenAt: null,
            LastError: null,
            Revoked: false,
            EnrolledAt: DateTimeOffset.UtcNow);

        nodes.Insert(node);

        // Decoded rather than re-serialized, the same way an agent reads it: the signature covers
        // these bytes verbatim.
        var bundle = JsonSerializer.Deserialize(
            Base64Url.Decode(fleet.BuildBundle(node).Payload),
            ContractsJsonContext.Default.DesiredStateBundle)!;

        Assert.Equal(1300, bundle.Node.Mtu);
    }

}

/// <summary>
/// Editing the setting. Its own fixture, and its own class, so a test that moves a fleet-wide
/// value can never decide what <see cref="TunnelMtuTests"/> reads: the fixture is per class, and
/// restoring the old value in a finally block would still leave a failed assertion mid-test able
/// to strand the fleet on the wrong number.
/// </summary>
public class TunnelMtuSettingTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private static async Task<long> RevisionAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

    [Fact]
    public async Task Changing_the_mtu_bumps_the_revision_because_it_changes_what_a_node_runs()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var before = await RevisionAsync(admin);

        var response = await admin.PutAsJsonAsync("/api/fleet/mtu", new UpdateTunnelMtuRequest(1340));
        response.EnsureSuccessStatusCode();

        var fleet = (await response.Content.ReadFromJsonAsync<FleetResponse>())!;
        Assert.Equal(1340, fleet.TunnelMtu);
        Assert.True(await RevisionAsync(admin) > before);

        // And the configs an operator hands out carry it, which is the point of the setting.
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("mtu-peer", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        Assert.Contains("MTU = 1340", await admin.GetStringAsync($"/api/clients/{client.Id}/config"));
    }

    [Theory]
    [InlineData(1279)]
    [InlineData(1421)]
    [InlineData(0)]
    public async Task An_mtu_outside_the_usable_range_is_refused(int mtu)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var before = await RevisionAsync(admin);

        var response = await admin.PutAsJsonAsync("/api/fleet/mtu", new UpdateTunnelMtuRequest(mtu));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_mtu", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(before, await RevisionAsync(admin));
    }
}
