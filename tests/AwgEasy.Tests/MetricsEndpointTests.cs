using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The scrape endpoint, which is the one route that answers without an admin session. Two things
/// are worth asserting rather than assuming: that the token is actually checked, and that what
/// comes out carries nothing about a person - a metrics store is federated, backed up and shared
/// far more casually than this database, and a peer label would put "who" into all of it.
/// </summary>
public class MetricsEndpointTests(ControlPlaneFixture fixture) : FleetScenario(fixture), IClassFixture<ControlPlaneFixture>
{
    private HttpClient Scraper(string? token)
    {
        var client = Fixture.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private async Task<string> ScrapeAsync()
    {
        var response = await Scraper(ControlPlaneFixture.MetricsToken).GetAsync("/api/metrics");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Without_a_token_nothing_is_served()
    {
        var response = await Scraper(null).GetAsync("/api/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("metrics_unauthorized", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task A_wrong_token_is_refused()
    {
        var response = await Scraper(ControlPlaneFixture.MetricsToken + "-nope").GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_fleet_revision_and_peer_counts_are_exported()
    {
        var admin = await AdminAsync();
        (await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("metrics-peer", null))).EnsureSuccessStatusCode();
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;

        var scrape = await ScrapeAsync();

        Assert.Contains("# TYPE awg_up gauge", scrape, StringComparison.Ordinal);
        Assert.Contains("awg_up 1", scrape, StringComparison.Ordinal);
        Assert.Contains($"awg_fleet_revision {fleet.Revision}", scrape, StringComparison.Ordinal);
        Assert.Contains("awg_fleet_peers{state=\"enabled\"}", scrape, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state set: one series per state with a 1 on the current one, so a dashboard can alert on
    /// awg_node_health{state="blocked"} without knowing what the other states are called.
    /// </summary>
    [Fact]
    public async Task A_nodes_health_is_exported_as_a_state_set()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "metrics-node", "203.0.113.61");

        var scrape = await ScrapeAsync();
        var health = (await NodeStateAsync(admin, node.Id)).Health;

        Assert.Contains(
            $"awg_node_health{{node=\"{node.Name}\",node_id=\"{node.Id}\",state=\"{health}\"}} 1",
            scrape,
            StringComparison.Ordinal);

        // Every other state is present as a zero rather than missing, which is what makes the
        // series continuous enough to alert on.
        Assert.Contains(
            $"awg_node_health{{node=\"{node.Name}\",node_id=\"{node.Id}\",state=\"blocked\"}} 0",
            scrape,
            StringComparison.Ordinal);

        Assert.Contains($"awg_node_interface_up{{node=\"{node.Name}\",node_id=\"{node.Id}\"}} 1", scrape, StringComparison.Ordinal);
        Assert.Contains($"awg_node_revision_lag{{node=\"{node.Name}\",node_id=\"{node.Id}\"}}", scrape, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_probe_last_saw_of_a_node_is_exported()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "metrics-probed", "203.0.113.62");
        var (client, probe) = await ProbeAsync(admin, "metrics-vantage");

        (await ReportProbeAsync(client, probe, Result(node, ProbeOutcomes.Unreachable, handshake: true))).EnsureSuccessStatusCode();

        var scrape = await ScrapeAsync();
        var labels = $"{{probe=\"metrics-vantage\",probe_id=\"{probe.NodeId}\",node=\"{node.Name}\",node_id=\"{node.Id}\"}}";

        Assert.Contains($"awg_probe_reachable{labels} 0", scrape, StringComparison.Ordinal);
        Assert.Contains($"awg_probe_handshake{labels} 1", scrape, StringComparison.Ordinal);
    }

    /// <summary>
    /// The privacy line, asserted on the bytes rather than trusted: a scrape is about servers, and
    /// the question of who moved what belongs to the Traffic page and the session in front of it.
    /// </summary>
    [Fact]
    public async Task Nothing_about_a_person_is_exported()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("metrics-private-name", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var scrape = await ScrapeAsync();

        Assert.DoesNotContain("metrics-private-name", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain(client.PublicKey, scrape, StringComparison.Ordinal);
        Assert.DoesNotContain(client.Address, scrape, StringComparison.Ordinal);
    }

    /// <summary>
    /// A node address is what a blocker wants, and a scrape is copied into places this panel does
    /// not control. The id and the operator's own label are enough to join on.
    /// </summary>
    [Fact]
    public async Task A_nodes_address_is_not_exported()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "metrics-address", "203.0.113.63");

        Assert.DoesNotContain(node.PublicIp, await ScrapeAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two series an operator draining a node alerts on, and the reason the third one beside
    /// them cannot be reused: it counts the peers a node is configured with, which in a fleet is
    /// nearly the whole client list.
    /// </summary>
    [Fact]
    public async Task The_peers_a_node_is_carrying_are_exported_apart_from_the_peers_it_is_configured_with()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("metrics-carried", null));
        created.EnsureSuccessStatusCode();
        var peer = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var node = await NodeAsync(admin, "metrics-carrying", "203.0.113.66");
        var report = new NodeStatusReport(
            AppliedRevision: 0,
            InterfaceUp: true,
            Backend: "test",
            AgentVersion: "test/1.0",
            ReportedAt: DateTimeOffset.UtcNow,
            Peers: [new PeerStatus(peer.PublicKey, DateTimeOffset.UtcNow.AddSeconds(-10), 10, 10)],
            Metrics: null,
            LastError: null,
            BundleSchemaVersion: DesiredStateBundle.CurrentSchemaVersion,
            PublicIp: node.PublicIp);

        (await node.Client.SendAsync(node.Agent.SignedRequest(
            HttpMethod.Post,
            $"/api/v1/agents/{node.Id}/status",
            JsonContent.Create(report)))).EnsureSuccessStatusCode();

        var scrape = await ScrapeAsync();
        var labels = $"{{node=\"{node.Name}\",node_id=\"{node.Id}\"}}";

        Assert.Contains($"awg_node_peers_online{labels} 1", scrape, StringComparison.Ordinal);
        Assert.Contains($"awg_node_peers_idle{labels} 0", scrape, StringComparison.Ordinal);

        // Counts of people, never a label naming one.
        Assert.DoesNotContain("metrics-carried", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.PublicKey, scrape, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Address, scrape, StringComparison.Ordinal);
    }

    /// <summary>
    /// And neither are the spare ones. A node that hands out several addresses has several a
    /// blocker would want, and an exported label would put the operator's reason for each of them
    /// into every copy of the scrape as well.
    /// </summary>
    [Fact]
    public async Task A_nodes_other_egress_addresses_are_not_exported_either()
    {
        var admin = await AdminAsync();
        var node = await NodeAsync(admin, "metrics-egress", "203.0.113.64");

        (await admin.PutAsJsonAsync(
            $"/api/nodes/{node.Id}/egress",
            new UpdateNodeEgressRequest("203.0.113.64", [new EgressLabel("203.0.113.65", "clean")]))).EnsureSuccessStatusCode();

        var scrape = await ScrapeAsync();

        Assert.DoesNotContain("203.0.113.65", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain("clean", scrape, StringComparison.Ordinal);
    }
}
