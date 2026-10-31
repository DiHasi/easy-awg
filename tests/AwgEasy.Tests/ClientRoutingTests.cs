using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// What one peer routes into the tunnel, and which resolver its config names.
///
/// The reason these can be per peer at all is that they are render-time only: a client's
/// AllowedIPs is what that device sends through the tunnel, while the node's AllowedIPs for the
/// same peer is that peer's tunnel address - two settings that share a name and decide different
/// things. So nothing here reaches a node and nothing moves the revision.
/// </summary>
public class ClientRoutingTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0/0, ::/0")]
    [InlineData(" 192.168.0.0/16 , 10.0.0.0/8 ")]
    public void A_route_list_of_cidr_ranges_is_accepted(string value)
    {
        Assert.True(ClientRouting.TryReadAllowedIps(value, out var normalized, out _));
        Assert.NotNull(normalized);
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("everything")]
    [InlineData("10.0.0.0/8, nonsense")]
    public void A_route_list_that_awg_quick_would_refuse_is_refused_here(string value)
    {
        Assert.False(ClientRouting.TryReadAllowedIps(value, out _, out var error));
        Assert.Equal("client_allowed_ips_invalid", error.Code);
    }

    /// <summary>
    /// A blank field means the fleet value, never an empty setting: a config whose AllowedIPs is
    /// empty comes up and routes nothing, which is a tunnel that connects and carries no traffic.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_field_means_the_fleet_value(string? value)
    {
        Assert.True(ClientRouting.TryReadAllowedIps(value, out var allowedIps, out _));
        Assert.Null(allowedIps);

        Assert.True(ClientRouting.TryReadDns(value, out var dns, out _));
        Assert.Null(dns);
    }

    /// <summary>Search domains are accepted because wg-quick accepts them, and the panel has no
    /// business being stricter than the tool it writes configs for.</summary>
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("1.1.1.1, 1.0.0.1")]
    [InlineData("10.8.0.1, internal.example.com")]
    public void A_resolver_list_takes_addresses_and_search_domains(string value)
    {
        Assert.True(ClientRouting.TryReadDns(value, out var normalized, out _));
        Assert.NotNull(normalized);
    }

    [Theory]
    [InlineData("the office one")]
    [InlineData("1.1.1.1; 8.8.8.8")]
    public void A_resolver_list_that_is_prose_is_refused(string value)
    {
        Assert.False(ClientRouting.TryReadDns(value, out _, out var error));
        Assert.Equal("client_dns_invalid", error.Code);
    }

    /// <summary>
    /// The whole point, in the only place it can be seen: the file this person is handed.
    /// </summary>
    [Fact]
    public async Task A_peers_own_values_are_written_into_its_config_and_the_fleet_keeps_the_rest()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "split-tunnel");
        var other = await Create(client, "fleet-defaults");

        var updated = await SetRouting(client, peer.Id, new UpdateClientRoutingRequest("10.0.0.0/8, 192.168.0.0/16", "10.0.0.53"));
        Assert.Equal("10.0.0.0/8, 192.168.0.0/16", updated.AllowedIps);
        Assert.Equal("10.0.0.53", updated.Dns);

        var config = await Config(client, peer.Id);
        Assert.Contains("AllowedIPs = 10.0.0.0/8, 192.168.0.0/16", config, StringComparison.Ordinal);
        Assert.Contains("DNS = 10.0.0.53", config, StringComparison.Ordinal);

        // And nobody else moved: these are one peer's settings, which is the whole reason they
        // are not the fleet's.
        var fleet = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        var untouched = await Config(client, other.Id);
        Assert.Contains($"AllowedIPs = {fleet.ClientAllowedIps}", untouched, StringComparison.Ordinal);
        Assert.Contains($"DNS = {fleet.ClientDns}", untouched, StringComparison.Ordinal);
    }

    /// <summary>
    /// Render-time only, so no node has anything to re-apply - the same place in the model as a
    /// rename. If this ever starts moving the revision, the model has drifted.
    /// </summary>
    [Fact]
    public async Task Changing_what_a_peer_routes_does_not_move_the_fleet_revision()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "no-revision-bump");

        var before = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        await SetRouting(client, peer.Id, new UpdateClientRoutingRequest("10.0.0.0/8", "1.1.1.1"));
        var after = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Clearing_the_overrides_puts_the_peer_back_on_the_fleet_values()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "back-to-the-fleet");
        var fleet = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        await SetRouting(client, peer.Id, new UpdateClientRoutingRequest("10.0.0.0/8", "1.1.1.1"));
        var cleared = await SetRouting(client, peer.Id, new UpdateClientRoutingRequest(null, null));

        Assert.Null(cleared.AllowedIps);
        Assert.Null(cleared.Dns);

        var config = await Config(client, peer.Id);
        Assert.Contains($"AllowedIPs = {fleet.ClientAllowedIps}", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_list_the_tunnel_could_not_apply_is_refused_by_the_endpoint()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "bad-routes");

        var response = await client.PutAsJsonAsync(
            $"/api/clients/{peer.Id}/routing",
            new UpdateClientRoutingRequest("everything, please", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client_allowed_ips_invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    private static async Task<ClientResponse> Create(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<ClientResponse> SetRouting(HttpClient client, string id, UpdateClientRoutingRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/clients/{id}/routing", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<string> Config(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/api/clients/{id}/config");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
