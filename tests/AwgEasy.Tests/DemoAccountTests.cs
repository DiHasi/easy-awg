using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The read-only demo account, which exists so a real fleet can be shown to somebody without
/// showing them anybody in it.
///
/// Two things are asserted rather than assumed, and they fail in opposite directions. That the
/// account changes nothing is the ordinary one: a refusal announces itself, and a missing one
/// would be noticed. That nothing real reaches it is the one worth a test, because a leak does
/// not announce itself - a page full of plausible stand-ins looks exactly the same whether or not
/// one real name slipped through it. So the assertions are made against the raw response bytes,
/// the way the metrics endpoint's are, rather than against a deserialized field somebody
/// remembered to check.
/// </summary>
public class DemoAccountTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private const string PeerName = "Ekaterina's laptop";
    private const string NodeName = "moscow-primary";
    private const string NodeAddress = "91.214.18.44";
    private const string RealEndpointHost = "vpn.example.com";
    private const string RealSubnet = "10.8.0.0/24";

    private async Task<HttpClient> DemoAsync() => await Fixture.CreateDemoClientAsync();

    private static async Task<string> RawAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<ClientResponse> PeerAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    [Fact]
    public async Task The_session_says_which_account_it_is()
    {
        var demo = await (await DemoAsync()).GetFromJsonAsync<SessionResponse>("/api/auth/me");
        var admin = await (await AdminAsync()).GetFromJsonAsync<SessionResponse>("/api/auth/me");

        Assert.True(demo!.Demo);
        Assert.Equal(ControlPlaneFixture.DemoUser, demo.Username);
        Assert.False(admin!.Demo);
    }

    [Theory]
    [InlineData("POST", "/api/clients")]
    [InlineData("POST", "/api/groups")]
    [InlineData("POST", "/api/nodes/tokens")]
    [InlineData("POST", "/api/probes/tokens")]
    [InlineData("POST", "/api/fleet/rotate")]
    [InlineData("PUT", "/api/fleet/obfuscation")]
    [InlineData("PUT", "/api/fleet/mtu")]
    [InlineData("PUT", "/api/failover")]
    [InlineData("POST", "/api/nodes/availability/reset")]
    public async Task Every_write_is_refused(string method, string path)
    {
        var response = await (await DemoAsync()).SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { name = "whatever", mode = "manual", mtu = 1280 })
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("demo_read_only", error!.Code);
    }

    /// <summary>Refusing every POST would otherwise leave a visitor unable to leave.</summary>
    [Fact]
    public async Task Signing_out_is_not_a_write()
    {
        var response = await (await DemoAsync()).PostAsync("/api/auth/logout", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>A scrape answers in Prometheus text, which the mask does not read.</summary>
    [Fact]
    public async Task The_scrape_endpoint_is_not_served()
    {
        var response = await (await DemoAsync()).GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_peer_keeps_its_shape_and_loses_everything_else()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, PeerName);

        var demo = await DemoAsync();
        var raw = await RawAsync(demo, "/api/clients");

        Assert.DoesNotContain(PeerName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Address, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PublicKey, raw, StringComparison.Ordinal);

        // The id is the handle the pages are built on and names nobody, so it stays - which is
        // also why a demo session must never be able to write through one.
        var masked = JsonSerializer.Deserialize<ClientResponse[]>(raw, JsonOptions)!.Single(peer => peer.Id == created.Id);
        Assert.NotEqual(PeerName, masked.Name);
        Assert.NotEmpty(masked.Name);
        Assert.Equal(created.Enabled, masked.Enabled);
        Assert.Equal(created.CreatedAt, masked.CreatedAt);
    }

    /// <summary>
    /// The property that makes the demo a fleet rather than a shuffle. A peer is one person on
    /// the peer list, in the traffic table and anywhere else it is named, or an operator reading
    /// the demo is reading noise.
    /// </summary>
    [Fact]
    public async Task One_peer_is_one_person_on_every_page()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Consistency check");

        var demo = await DemoAsync();
        var listed = (await demo.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(peer => peer.Id == created.Id);
        var usage = (await demo.GetFromJsonAsync<UsageSummaryResponse>("/api/usage?window=24h"))!
            .Clients.Single(peer => peer.Id == created.Id);

        Assert.Equal(listed.Name, usage.Name);
    }

    [Fact]
    public async Task A_node_loses_its_name_and_its_address()
    {
        var admin = await AdminAsync();
        await NodeAsync(admin, NodeName, NodeAddress);

        var raw = await RawAsync(await DemoAsync(), "/api/nodes");

        Assert.DoesNotContain(NodeName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(NodeAddress, raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The log is prose the panel wrote about peers and nodes, which is exactly where a mask that
    /// only knew about fields would leak.
    /// </summary>
    [Fact]
    public async Task The_log_does_not_name_anybody()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Pavel's phone");

        var demo = await DemoAsync();
        var raw = await RawAsync(demo, "/api/events");

        Assert.DoesNotContain("Pavel's phone", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Address, raw, StringComparison.Ordinal);

        // The operator who did it is a name too, and it is the one the log repeats most. Asserted
        // on the column rather than on the bytes, because an event kind is a fixed code that
        // happens to begin with the word "admin" whatever the operator is called - and the mask
        // leaves codes alone on purpose.
        var entries = (await demo.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        Assert.Contains(entries, entry => entry.Actor is not null);
        Assert.DoesNotContain(entries, entry => entry.Actor == ControlPlaneFixture.AdminUser);
    }

    /// <summary>
    /// Routing constants belong to nobody, and a mask that rewrote them would not be concealing
    /// anything - it would be showing a config that routes nothing.
    /// </summary>
    [Fact]
    public async Task A_default_route_is_still_a_default_route()
    {
        var masked = await (await DemoAsync()).GetFromJsonAsync<FleetResponse>("/api/fleet");
        Assert.Contains("0.0.0.0/0", masked!.ClientAllowedIps, StringComparison.Ordinal);
    }

    /// <summary>
    /// An event kind is part of the API and a status is a fixed word. An operator unlucky enough
    /// to be called "admin" would otherwise have "admin.login" rewritten into a code no page
    /// knows, which is a mask breaking the panel rather than protecting anybody.
    /// </summary>
    [Fact]
    public async Task Codes_and_ids_survive_the_mask()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Code check");

        var demo = await DemoAsync();
        var events = (await demo.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        var peers = (await demo.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;

        Assert.Contains(events, entry => entry.Kind == "client.created");
        Assert.Contains(peers, peer => peer.Id == created.Id);
    }

    [Fact]
    public async Task The_fleet_does_not_give_up_its_identity_or_its_subnet()
    {
        var admin = await AdminAsync();
        var real = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        var raw = await RawAsync(await DemoAsync(), "/api/fleet");

        Assert.DoesNotContain(real.ServerPublicKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(RealEndpointHost, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(RealSubnet, raw, StringComparison.Ordinal);

        // What the demo is for survives: the same counts, the same revision, the same generation.
        var masked = JsonSerializer.Deserialize<FleetResponse>(raw, JsonOptions)!;
        Assert.Equal(real.Revision, masked.Revision);
        Assert.Equal(real.Generation, masked.Generation);
        Assert.Equal(real.ClientsCount, masked.ClientsCount);
        Assert.Equal(real.TunnelMtu, masked.TunnelMtu);
    }

    /// <summary>
    /// S1-S4, H1-H4 and the header protection key are what make this fleet's packets recognizable
    /// as this fleet's - the thing a rotation draws afresh. Publishing them to whoever is looking
    /// at the demo would hand a censor the one piece they are short of.
    /// </summary>
    [Fact]
    public async Task The_fleet_wire_format_is_not_published()
    {
        var admin = await AdminAsync();
        var profile = new ServerObfuscationProfile
        {
            S1 = 29, S2 = 41, S3 = 33, S4 = 37,
            H1 = "1000-1100", H2 = "2000-2100", H3 = "3000-3100", H4 = "4000-4100",
            DefaultJc = 5
        };

        (await admin.PutAsJsonAsync("/api/fleet/obfuscation", profile)).EnsureSuccessStatusCode();

        var masked = (await (await DemoAsync()).GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Obfuscation!;

        Assert.NotEqual(profile.S1, masked.S1);
        Assert.NotEqual(profile.H1, masked.H1);

        // Still a profile a node would accept, and still recognizably a range where the fleet
        // carries one: the shape is what decides whether a node needs AmneziaWG 3.x, so changing
        // it would have the demo contradict the schema badge beside it.
        Assert.Contains('-', masked.H1!);
        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(masked, out var error), error.Message);

        // The half that may differ per peer describes nobody, so it is the operator's own tuning
        // and comes through untouched.
        Assert.Equal(profile.DefaultJc, masked.DefaultJc);

        (await admin.PutAsJsonAsync("/api/fleet/obfuscation", new ServerObfuscationProfile())).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_config_is_a_real_config_belonging_to_nobody()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Config subject");
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        var response = await (await DemoAsync()).GetAsync($"/api/clients/{created.Id}/config");
        response.EnsureSuccessStatusCode();
        var config = await response.Content.ReadAsStringAsync();

        // Nothing of the fleet's in it.
        Assert.DoesNotContain(created.Address, config, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PublicKey, config, StringComparison.Ordinal);
        Assert.DoesNotContain(fleet.ServerPublicKey, config, StringComparison.Ordinal);
        Assert.DoesNotContain(RealEndpointHost, config, StringComparison.Ordinal);
        Assert.DoesNotContain("Config subject", response.Content.Headers.ContentDisposition?.FileNameStar ?? string.Empty, StringComparison.Ordinal);

        // And still the file somebody came to look at: a peer, a server, and an endpoint to reach it.
        Assert.Contains("[Interface]", config, StringComparison.Ordinal);
        Assert.Contains("PrivateKey", config, StringComparison.Ordinal);
        Assert.Contains("[Peer]", config, StringComparison.Ordinal);
        Assert.Contains("Endpoint", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rendering a config records that this peer was in a position to be handed its current one,
    /// which is how the panel knows who to go and find after a rotation. A visitor looking at a
    /// stand-in was handed nothing, and a stamp written for them would send an operator chasing
    /// somebody who is holding exactly what they were before.
    /// </summary>
    [Fact]
    public async Task Handing_a_demo_a_config_records_nothing()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Unstamped");
        Assert.Equal(0, created.ConfigGeneration);

        (await (await DemoAsync()).GetAsync($"/api/clients/{created.Id}/config")).EnsureSuccessStatusCode();

        var after = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(peer => peer.Id == created.Id);
        Assert.Equal(0, after.ConfigGeneration);
    }

    /// <summary>The mask is for the demo and nobody else: an operator who cannot read their own
    /// fleet has been handed a panel that is worse than no panel.</summary>
    [Fact]
    public async Task An_operator_still_sees_their_own_fleet()
    {
        var admin = await AdminAsync();
        var created = await PeerAsync(admin, "Visible to the operator");

        var raw = await RawAsync(admin, "/api/clients");

        Assert.Contains("Visible to the operator", raw, StringComparison.Ordinal);
        Assert.Contains(created.Address, raw, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
