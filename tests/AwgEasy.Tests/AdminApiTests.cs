using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The single-server version had no authentication at all. In a fleet that would mean anyone who
/// reached the panel could pull the identity every node runs on, so the closed door is worth
/// asserting rather than assuming.
/// </summary>
public class AdminAuthTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    [Theory]
    [InlineData("/api/clients")]
    [InlineData("/api/clients/stats")]
    [InlineData("/api/fleet")]
    [InlineData("/api/nodes")]
    [InlineData("/api/events")]
    public async Task Admin_endpoints_reject_anonymous_callers(string path)
    {
        var response = await fixture.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_client_anonymously_is_rejected()
    {
        var response = await fixture.CreateClient()
            .PostAsJsonAsync("/api/clients", new CreateClientRequest("smuggled", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Issuing_an_enrollment_token_anonymously_is_rejected()
    {
        var response = await fixture.CreateClient()
            .PostAsJsonAsync("/api/nodes/tokens", new CreateNodeRequest("rogue", null, null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_does_not_sign_in()
    {
        var response = await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest(ControlPlaneFixture.AdminUser, "not-the-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_user_does_not_sign_in()
    {
        var response = await fixture.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_stays_open_so_the_panel_can_be_probed()
    {
        var response = await fixture.CreateClient().GetAsync("/api/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_signed_in_admin_can_reach_the_fleet()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var fleet = await admin.GetFromJsonAsync<FleetResponse>("/api/fleet");

        Assert.NotNull(fleet);
        Assert.Equal("10.8.0.0/24", fleet.Subnet);
    }
}

/// <summary>
/// Adopting an existing single-server deployment. This is the migration path that decides whether
/// current users stay connected, so it gets its own fixture with no other tests mutating state.
/// </summary>
public class LegacyImportTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public LegacyImportTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private const string LegacyServerPrivateKey = "aGVsbG8tdGhpcy1pcy1hLWxlZ2FjeS1zZXJ2ZXIta2V5PQ==";
    private const string LegacyServerPublicKey = "cHVibGljLWhhbGYtb2YtdGhlLWxlZ2FjeS1zZXJ2ZXIta2V5";

    private static string LegacyStateJson() => JsonSerializer.Serialize(new
    {
        serverPrivateKey = LegacyServerPrivateKey,
        serverPublicKey = LegacyServerPublicKey,
        serverObfuscation = new { s1 = 15, h1 = "1234567891" },
        clients = new[]
        {
            new
            {
                id = "aaaa1111",
                name = "old-laptop",
                address = "10.8.0.7",
                privateKey = "bGVnYWN5LWNsaWVudC1vbmUtcHJpdmF0ZS1rZXktdmFsdWU=",
                publicKey = "bGVnYWN5LWNsaWVudC1vbmUtcHVibGljLWtleS12YWx1ZWFh",
                presharedKey = "bGVnYWN5LWNsaWVudC1vbmUtcHNrLXZhbHVlLXBhZGRlZC0=",
                enabled = true,
                createdAt = "2025-01-01T00:00:00+00:00",
                updatedAt = "2025-01-01T00:00:00+00:00"
            }
        }
    });

    [Fact]
    public async Task Importing_keeps_the_original_server_identity_so_existing_clients_keep_working()
    {
        var admin = await _fixture.CreateAdminClientAsync();

        var response = await admin.PostAsync(
            "/api/fleet/import?replace=true",
            new StringContent(LegacyStateJson(), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ImportResultResponse>())!;
        Assert.Equal(1, result.ClientsImported);

        // The whole point: already-issued client configs point at this public key. If the import
        // regenerated it, every existing user would be disconnected.
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        Assert.Equal(LegacyServerPublicKey, fleet.ServerPublicKey);

        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        var imported = Assert.Single(clients, c => c.Name == "old-laptop");
        Assert.Equal("10.8.0.7", imported.Address);
    }

    [Fact]
    public async Task An_imported_client_keeps_its_address_when_a_new_one_is_created_afterwards()
    {
        var admin = await _fixture.CreateAdminClientAsync();

        await admin.PostAsync(
            "/api/fleet/import?replace=true",
            new StringContent(LegacyStateJson(), Encoding.UTF8, "application/json"));

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("new-phone", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        // Allocation must route around the imported address rather than collide with it.
        Assert.NotEqual("10.8.0.7", client.Address);
    }

    [Fact]
    public async Task Malformed_state_is_refused_rather_than_partially_applied()
    {
        var admin = await _fixture.CreateAdminClientAsync();

        var response = await admin.PostAsync(
            "/api/fleet/import?replace=true",
            new StringContent("{\"clients\":[]}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// Share links hand a config to someone who has no account, so they are anonymous on purpose.
/// Knowing the token must be the only thing that grants access - and it must grant nothing else.
/// </summary>
public class ClientShareTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<(HttpClient Admin, ClientResponse Client)> ClientAsync(string name)
    {
        var admin = await fixture.CreateAdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        created.EnsureSuccessStatusCode();
        return (admin, (await created.Content.ReadFromJsonAsync<ClientResponse>())!);
    }

    [Fact]
    public async Task A_share_link_lets_an_anonymous_visitor_fetch_that_one_config()
    {
        var (admin, client) = await ClientAsync("shared-phone");

        var shareResponse = await admin.PostAsync($"/api/clients/{client.Id}/share", null);
        shareResponse.EnsureSuccessStatusCode();
        var share = (await shareResponse.Content.ReadFromJsonAsync<ClientShareResponse>())!;

        var anonymous = fixture.CreateClient();

        var details = await anonymous.GetFromJsonAsync<PublicShareResponse>($"/api/shares/{share.Token}");
        Assert.Equal("shared-phone", details!.ClientName);

        var config = await anonymous.GetAsync($"/api/shares/{share.Token}/config");
        config.EnsureSuccessStatusCode();

        var text = await config.Content.ReadAsStringAsync();
        Assert.Contains("[Interface]", text);
        Assert.Contains($"Address = {client.Address}/24", text);
    }

    [Fact]
    public async Task A_share_link_reveals_nothing_beyond_the_client_name_and_expiry()
    {
        var (admin, client) = await ClientAsync("private-details");

        var shareResponse = await admin.PostAsync($"/api/clients/{client.Id}/share", null);
        var share = (await shareResponse.Content.ReadFromJsonAsync<ClientShareResponse>())!;

        var payload = await fixture.CreateClient().GetStringAsync($"/api/shares/{share.Token}");

        // The lookup endpoint is a preview, not a config dump: no keys, no tunnel address.
        Assert.DoesNotContain(client.PublicKey, payload);
        Assert.DoesNotContain(client.Address, payload);
    }

    [Fact]
    public async Task An_unknown_share_token_is_not_found()
    {
        var response = await fixture.CreateClient().GetAsync("/api/shares/definitely-not-a-real-token");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_share_requires_an_admin()
    {
        var (_, client) = await ClientAsync("guarded");

        var response = await fixture.CreateClient().PostAsync($"/api/clients/{client.Id}/share", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_client_breaks_its_share_links()
    {
        var (admin, client) = await ClientAsync("temporary");

        var shareResponse = await admin.PostAsync($"/api/clients/{client.Id}/share", null);
        var share = (await shareResponse.Content.ReadFromJsonAsync<ClientShareResponse>())!;

        (await admin.DeleteAsync($"/api/clients/{client.Id}")).EnsureSuccessStatusCode();

        var response = await fixture.CreateClient().GetAsync($"/api/shares/{share.Token}/config");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
