using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// The panel handing over a copy of itself, and the way back from one.
///
/// What makes this worth having is narrow and absolute: a node can be rebuilt from the install
/// one-liner, but the fleet key pair and every client's own key pair exist in one SQLite file, and
/// losing it means every config in every person's hands stops working with nothing to hand out in
/// its place.
///
/// Its own fixture, because a restore replaces the fleet - including the peers and the operator
/// accounts every other test in a class would be working with.
/// </summary>
public class FleetBackupTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task An_export_carries_the_identity_and_the_peers_with_their_keys()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "exported-peer");

        var backup = await Export(client);

        Assert.Equal(1, backup.FormatVersion);
        Assert.Equal(PanelSchema.CurrentVersion, backup.Snapshot.SchemaVersion);
        Assert.Equal(PanelRoles.Active, backup.PanelRole);

        var fleet = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        Assert.Equal(fleet.ServerPublicKey, backup.Snapshot.Fleet.ServerPublicKey);
        Assert.Equal(fleet.Generation, backup.Generation);

        // The whole reason the file exists: without the private keys it would restore a fleet
        // nobody can connect to.
        Assert.NotEmpty(backup.Snapshot.Fleet.ServerPrivateKey);
        Assert.NotEmpty(backup.Snapshot.Fleet.SigningPrivateKey);

        var row = Assert.Single(backup.Snapshot.Clients, entry => entry.Id == peer.Id);
        Assert.Equal(peer.PublicKey, row.PublicKey);
        Assert.NotEmpty(row.PrivateKey);

        // The operators travel too, password hashes included - a restored panel nobody can sign
        // in to would be no use at all.
        Assert.Contains(backup.Snapshot.Admins, admin => admin.Username == ControlPlaneFixture.AdminUser);
        Assert.All(backup.Snapshot.Admins, admin => Assert.NotEmpty(admin.PasswordHash));
    }

    /// <summary>
    /// A download of the fleet private key is worth a line in the log on its own: nothing about
    /// the panel changed, and the only question afterwards is who took a copy and when.
    /// </summary>
    [Fact]
    public async Task An_export_is_written_to_the_event_log()
    {
        var client = await fixture.CreateAdminClientAsync();
        await Export(client);

        var events = (await client.GetFromJsonAsync<EventResponse[]>("/api/events"))!;

        // Contains rather than Single: the fixture is shared, so another test in this class may
        // have taken a copy too - and what is being asserted is that a download leaves a trace
        // naming who took it, not how many there are.
        Assert.Contains(
            events,
            entry => entry.Kind == "fleet.exported" && entry.Actor == ControlPlaneFixture.AdminUser);
    }

    /// <summary>
    /// The one read in the panel that is refused to a demo session rather than masked. Everything
    /// else it reads is a description of the fleet, and the mask is built from the panel's own
    /// tables so an unexpected field still comes out masked - but a key pair is not a name. There
    /// is nothing to map it to, so a masked export would be the real keys with the names changed.
    /// </summary>
    [Fact]
    public async Task The_demo_account_is_refused_the_export_rather_than_handed_a_masked_one()
    {
        var demo = await fixture.CreateDemoClientAsync();

        var response = await demo.GetAsync("/api/fleet/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("demo_read_only", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_export_the_panel()
    {
        var response = await fixture.CreateClient().GetAsync("/api/fleet/export");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_an_export_is_refused_rather_than_thrown()
    {
        var client = await fixture.CreateAdminClientAsync();

        var response = await Restore(client, "{ this is not json", replace: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("backup_unreadable", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// Restoring an export of nothing over a working fleet is the most destructive thing this
    /// endpoint could do by accident - the same reason a panel with no fleet state is never
    /// promoted.
    /// </summary>
    [Fact]
    public async Task An_export_with_no_peers_and_no_nodes_is_refused()
    {
        var client = await fixture.CreateAdminClientAsync();
        var backup = await Export(client);
        var empty = backup with { Snapshot = backup.Snapshot with { Clients = [], Nodes = [] } };

        var response = await Restore(client, JsonSerializer.Serialize(empty, Json), replace: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("backup_empty", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task An_export_from_a_newer_panel_is_refused_whole()
    {
        var client = await fixture.CreateAdminClientAsync();
        var backup = await Export(client);
        var newer = backup with { FormatVersion = FleetBackupService.CurrentFormatVersion + 1 };

        var response = await Restore(client, JsonSerializer.Serialize(newer, Json), replace: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("backup_too_new", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task An_export_carrying_an_unsupported_schema_is_refused_whole()
    {
        var client = await fixture.CreateAdminClientAsync();
        var backup = await Export(client);
        var newer = backup with { Snapshot = backup.Snapshot with { SchemaVersion = PanelSchema.CurrentVersion + 1 } };

        var response = await Restore(client, JsonSerializer.Serialize(newer, Json), replace: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("backup_schema_unsupported", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// A restore is wholesale, so running one on a panel that is already serving replaces that
    /// fleet and stops every config it ever issued. Asking for the flag is the difference between
    /// a recovery and an accident.
    /// </summary>
    [Fact]
    public async Task Restoring_over_a_fleet_that_has_peers_needs_the_replacement_confirmed()
    {
        var client = await fixture.CreateAdminClientAsync();
        await Create(client, "in-the-way");
        var backup = await Export(client);

        var refused = await Restore(client, JsonSerializer.Serialize(backup, Json), replace: false);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("fleet_not_empty", (await refused.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// The whole point, end to end: a peer deleted after the export is back afterwards, with the
    /// same key material - which is what makes the config in somebody's hands keep working - and
    /// the identity is the exported one rather than whatever this panel had drawn for itself.
    /// </summary>
    [Fact]
    public async Task A_restore_puts_the_peers_and_the_identity_back_as_they_were()
    {
        var client = await fixture.CreateAdminClientAsync();
        var kept = await Create(client, "survives-the-restore");
        var backup = await Export(client);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/clients/{kept.Id}")).StatusCode);
        var afterDelete = await Create(client, "created-after-the-export");

        var response = await Restore(client, JsonSerializer.Serialize(backup, Json), replace: true);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<FleetRestoreResponse>())!;

        Assert.Equal(backup.Snapshot.Clients.Length, result.PeersRestored);
        Assert.NotEmpty(result.Warning);

        var peers = (await client.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        var restored = Assert.Single(peers, peer => peer.Id == kept.Id);
        Assert.Equal(kept.PublicKey, restored.PublicKey);
        Assert.Equal(kept.Address, restored.Address);

        // Wholesale, not merged: a peer created after the export was taken is not in the file and
        // is therefore not in the fleet afterwards. That is the restore doing what it says.
        Assert.DoesNotContain(peers, peer => peer.Id == afterDelete.Id);

        var fleet = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        Assert.Equal(backup.Snapshot.Fleet.ServerPublicKey, fleet.ServerPublicKey);

        // And this panel is the one in charge afterwards, above the term the export named: the
        // panel it was taken from is usually the one being replaced, and if it is still running
        // it finds out on its next hello.
        Assert.True(result.PanelTerm > backup.Snapshot.Term);
    }

    /// <summary>
    /// A restored peer's config has to be the one that is already in somebody's hands, down to the
    /// preshared key - that is the only reason to keep a file rather than reissue everything.
    /// </summary>
    [Fact]
    public async Task A_restored_peer_is_handed_the_same_config_it_had()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "same-config-after");
        var before = await (await client.GetAsync($"/api/clients/{peer.Id}/config")).Content.ReadAsStringAsync();

        var backup = await Export(client);
        (await client.DeleteAsync($"/api/clients/{peer.Id}")).EnsureSuccessStatusCode();
        (await Restore(client, JsonSerializer.Serialize(backup, Json), replace: true)).EnsureSuccessStatusCode();

        var after = await (await client.GetAsync($"/api/clients/{peer.Id}/config")).Content.ReadAsStringAsync();

        Assert.Equal(before, after);
    }

    private static async Task<ClientResponse> Create(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<FleetBackupFile> Export(HttpClient client)
    {
        var response = await client.GetAsync("/api/fleet/export");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<FleetBackupFile>(await response.Content.ReadAsStringAsync(), Json)!;
    }

    private static Task<HttpResponseMessage> Restore(HttpClient client, string body, bool replace)
        => client.PostAsync(
            $"/api/fleet/restore?replace={(replace ? "true" : "false")}",
            new StringContent(body, Encoding.UTF8, "application/json"));
}
