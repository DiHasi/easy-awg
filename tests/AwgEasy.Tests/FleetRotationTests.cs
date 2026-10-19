using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;
using AwgEasy.Node;

namespace AwgEasy.Tests;

/// <summary>
/// Replacing the fleet identity, which is the one thing a compromised node leaves no alternative
/// to and the one thing nothing else in the panel may do.
///
/// Driven through the real control plane with the stand-in agent verifying bundles exactly as the
/// node does, because the whole claim being tested is that the fleet converges on the new identity
/// without anybody walking the servers. The tests inside this class run in order against one
/// fixture, so each asserts what its own rotation changed rather than an absolute generation.
/// </summary>
public class FleetRotationTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
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

    private static async Task<DesiredStateBundle> FetchAsync(HttpClient client, TestAgent agent, long appliedRevision = 0)
    {
        var response = await client.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;
        return agent.AcceptBundle(envelope, appliedRevision);
    }

    private static async Task<FleetRotationResponse> RotateAsync(HttpClient admin, bool reissueClientKeys = false)
    {
        var response = await admin.PostAsJsonAsync("/api/fleet/rotate", new RotateFleetRequest(reissueClientKeys));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FleetRotationResponse>())!;
    }

    // The point of the whole feature: both nodes end up serving an identity neither of them had
    // before, having been told nothing but "there is a new revision".
    [Fact]
    public async Task Every_node_converges_on_the_new_identity_on_its_next_poll()
    {
        var (admin, firstClient, first) = await EnrolledAgentAsync("rotate-node-a");
        var (_, secondClient, second) = await EnrolledAgentAsync("rotate-node-b");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-laptop", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var beforeFirst = await FetchAsync(firstClient, first);
        var beforeSecond = await FetchAsync(secondClient, second);
        var peerBefore = beforeFirst.Peers.Single(peer => peer.PublicKey == client.PublicKey);

        // Both nodes started from the same identity: that is what makes failover invisible.
        Assert.Equal(beforeFirst.Identity.ServerPublicKey, beforeSecond.Identity.ServerPublicKey);

        var rotation = await RotateAsync(admin);

        Assert.Equal(beforeFirst.Identity.Generation + 1, rotation.Generation);
        Assert.Equal(beforeFirst.Identity.Generation, rotation.PreviousGeneration);
        Assert.NotEqual(beforeFirst.Identity.ServerPublicKey, rotation.ServerPublicKey);
        Assert.True(rotation.Revision > beforeFirst.Revision);

        var afterFirst = await FetchAsync(firstClient, first, beforeFirst.Revision);
        var afterSecond = await FetchAsync(secondClient, second, beforeSecond.Revision);

        // Still one identity, and it is the one the panel reported.
        Assert.Equal(rotation.ServerPublicKey, afterFirst.Identity.ServerPublicKey);
        Assert.Equal(afterFirst.Identity.ServerPublicKey, afterSecond.Identity.ServerPublicKey);
        Assert.Equal(rotation.Generation, afterFirst.Identity.Generation);
        Assert.NotEqual(beforeFirst.Identity.ServerPrivateKey, afterFirst.Identity.ServerPrivateKey);

        // Every peer's preshared key was on the same disk as the identity, so it goes too.
        var peerAfter = afterFirst.Peers.Single(peer => peer.PublicKey == client.PublicKey);
        Assert.NotEqual(peerBefore.PresharedKey, peerAfter.PresharedKey);
        Assert.Equal(peerBefore.Address, peerAfter.Address);
        // And both nodes were handed the same new preshared key, or the peer would work on one
        // node and not the other - which is the failure failover exists to avoid.
        Assert.Equal(peerAfter.PresharedKey, afterSecond.Peers.Single(peer => peer.PublicKey == client.PublicKey).PresharedKey);

        // One revision for the whole change, not one per thing it touched.
        Assert.Equal(beforeFirst.Revision + 1, afterFirst.Revision);

        // The bundle is still enough on its own to bring an interface up.
        var config = ServerConfigRenderer.Render(afterFirst, "ens3");
        Assert.Contains($"PrivateKey = {afterFirst.Identity.ServerPrivateKey}", config);
        Assert.Contains($"AllowedIPs = {client.Address}/32", config);
    }

    // The invariant does not get a holiday because the fleet is being rotated: a node holds the
    // new private key and must still learn nothing about who the clients are.
    [Fact]
    public async Task The_rotated_bundle_still_carries_no_client_private_keys_or_names()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("rotate-node-secrets");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-bob-phone", null));
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        await RotateAsync(admin, reissueClientKeys: true);

        var response = await agentClient.SendAsync(agent.SignedRequest(HttpMethod.Get, $"/api/v1/agents/{agent.NodeId}/desired"));
        response.EnsureSuccessStatusCode();
        var envelope = (await response.Content.ReadFromJsonAsync<SignedBundle>())!;

        var payload = System.Text.Encoding.UTF8.GetString(Base64Url.Decode(envelope.Payload));
        Assert.DoesNotContain("rotate-bob-phone", payload);
        Assert.DoesNotContain("PRIV", payload[payload.IndexOf("peers", StringComparison.Ordinal)..]);

        // Still a bundle the agent accepts, and the peer is still on it - under its new key.
        var bundle = agent.AcceptBundle(envelope);
        var reissued = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(entry => entry.Id == client.Id);
        Assert.NotEqual(client.PublicKey, reissued.PublicKey);
        Assert.Contains(bundle.Peers, peer => peer.PublicKey == reissued.PublicKey);
        Assert.DoesNotContain(bundle.Peers, peer => peer.PublicKey == client.PublicKey);
    }

    // A node never held a client private key, so the default leaves them alone: rotating them is
    // a bigger change than the identity itself and is only called for if the panel's own database
    // has to be assumed read.
    [Fact]
    public async Task Client_key_pairs_survive_unless_the_operator_asks_for_them()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-keep-keys", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var kept = await RotateAsync(admin);
        var afterKeeping = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(entry => entry.Id == client.Id);

        Assert.False(kept.ClientKeysReissued);
        Assert.Equal(client.PublicKey, afterKeeping.PublicKey);
        // Named in the response either way: its preshared key changed, so its config is dead.
        var listed = kept.Clients.Single(entry => entry.Id == client.Id);
        Assert.Equal("rotate-keep-keys", listed.Name);
        Assert.False(listed.KeyPairReissued);

        var reissued = await RotateAsync(admin, reissueClientKeys: true);
        var afterReissuing = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(entry => entry.Id == client.Id);

        Assert.True(reissued.ClientKeysReissued);
        Assert.NotEqual(client.PublicKey, afterReissuing.PublicKey);
        Assert.Equal(client.Address, afterReissuing.Address);
        Assert.True(reissued.Clients.Single(entry => entry.Id == client.Id).KeyPairReissued);
    }

    /// <summary>
    /// A rotation leaves every config in the field dead, so the panel has to be able to say who
    /// has been handed a replacement. Rendering the config is the only moment it can observe that,
    /// and it is what the download, the QR code and the share link all go through.
    /// </summary>
    [Fact]
    public async Task A_peer_reads_as_stale_until_its_config_has_been_rendered_again()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-stale", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        // Nothing has been handed over yet, so it is on no generation - and not stale either:
        // its own material is current, and no config of its is in anybody's hands to be broken.
        Assert.Equal(0, client.ConfigGeneration);
        Assert.False(client.ConfigStale);

        (await admin.GetAsync($"/api/clients/{client.Id}/config")).EnsureSuccessStatusCode();

        var handed = await ReadAsync(admin, client.Id);
        Assert.False(handed.ConfigStale);
        Assert.Equal(handed.FleetGeneration, handed.ConfigGeneration);

        var rotation = await RotateAsync(admin);

        var stale = await ReadAsync(admin, client.Id);
        Assert.True(stale.ConfigStale);
        Assert.Equal(handed.ConfigGeneration, stale.ConfigGeneration);
        Assert.Equal(rotation.Generation, stale.FleetGeneration);

        // The fleet view counts them, which is what the Settings page reports after a rotation.
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        Assert.True(fleet.StaleConfigsCount > 0);

        (await admin.GetAsync($"/api/clients/{client.Id}/config")).EnsureSuccessStatusCode();

        var current = await ReadAsync(admin, client.Id);
        Assert.False(current.ConfigStale);
        Assert.Equal(rotation.Generation, current.ConfigGeneration);
    }

    // A peer created and left alone has nothing of its in anybody's hands, so a rotation leaves
    // the operator nothing to do about it - and the panel must not count it among the people it
    // is telling them to go and find.
    [Fact]
    public async Task A_peer_whose_config_was_never_rendered_is_not_counted_as_outstanding()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-never-handed", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        await RotateAsync(admin);

        var after = await ReadAsync(admin, client.Id);
        Assert.Equal(0, after.ConfigGeneration);
        Assert.False(after.ConfigStale);

        // And the number the Fleet page reports is the number of rows the peer list marks, so the
        // two places an operator reads this cannot tell them different things.
        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        Assert.Equal(
            fleet.StaleConfigsCount,
            (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Count(entry => entry.ConfigStale));
    }

    // The token travelled to whoever the config was for, over whatever channel was at hand. After
    // the identity it named has been replaced because a server was read, that link must not be
    // what hands the replacement key material to whoever else ended up holding it.
    [Fact]
    public async Task A_share_link_stops_resolving_once_the_identity_it_named_is_gone()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var anonymous = fixture.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("rotate-shared", null));
        var client = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;

        var shareResponse = await admin.PostAsync($"/api/clients/{client.Id}/share", null);
        shareResponse.EnsureSuccessStatusCode();
        var share = (await shareResponse.Content.ReadFromJsonAsync<ClientShareResponse>())!;

        (await anonymous.GetAsync($"/api/shares/{share.Token}")).EnsureSuccessStatusCode();
        (await anonymous.GetAsync($"/api/shares/{share.Token}/config")).EnsureSuccessStatusCode();

        var rotation = await RotateAsync(admin);
        Assert.True(rotation.ShareLinksRevoked > 0);

        var lookup = await anonymous.GetAsync($"/api/shares/{share.Token}");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal("share_not_found", (await lookup.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/shares/{share.Token}/config")).StatusCode);

        // A link made after the rotation works, so the operator can hand the new config over.
        var again = await admin.PostAsync($"/api/clients/{client.Id}/share", null);
        var fresh = (await again.Content.ReadFromJsonAsync<ClientShareResponse>())!;
        (await anonymous.GetAsync($"/api/shares/{fresh.Token}/config")).EnsureSuccessStatusCode();
    }

    // The wire format is in the config on a node's disk, and together with the keys it is what
    // makes this fleet's traffic recognizable as this fleet's. Rotating the keys and leaving the
    // format behind would refresh the secrets and keep the fingerprint.
    [Fact]
    public async Task The_fleet_wide_wire_format_is_redrawn_and_the_per_client_seeds_are_not()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var profile = new ServerObfuscationProfile
        {
            S1 = 16,
            S2 = 20,
            S3 = 24,
            S4 = 28,
            H1 = "1000000-1000500",
            H2 = "2000000",
            HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
            RandomTrailers = true,
            DefaultJc = 6,
            DefaultRekeyAfterTime = "110-130",
            DefaultRejectAfterTime = "170-190"
        };

        (await admin.PutAsJsonAsync("/api/fleet/obfuscation", profile)).EnsureSuccessStatusCode();

        await RotateAsync(admin);

        var fleet = (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!;
        var rotated = fleet.Obfuscation!;

        Assert.NotEqual(profile.H1, rotated.H1);
        Assert.NotEqual(profile.H2, rotated.H2);
        Assert.NotEqual(profile.HeaderProtectionKey, rotated.HeaderProtectionKey);
        Assert.False(rotated.S1 == profile.S1 && rotated.S2 == profile.S2 && rotated.S3 == profile.S3 && rotated.S4 == profile.S4);

        // The operator's tuning is not key material and is left exactly as they set it.
        Assert.Equal(6, rotated.DefaultJc);
        Assert.Equal("110-130", rotated.DefaultRekeyAfterTime);
        Assert.Equal("170-190", rotated.DefaultRejectAfterTime);
        Assert.True(rotated.RandomTrailers);

        // And it is a profile a node would accept, which is the whole reason it is drawn here.
        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(rotated, out var error), error.Message);
    }

    // Rotating the signing key pair would re-pin every agent, and an agent whose pinned key no
    // longer matches refuses bundles rather than fetching new ones. So it is left alone, and the
    // nodes that were enrolled before the rotation keep working without being touched.
    [Fact]
    public async Task The_bundle_signing_key_is_left_alone_so_no_node_has_to_be_re_enrolled()
    {
        var (admin, agentClient, agent) = await EnrolledAgentAsync("rotate-node-pinned");

        var pinnedKey = agent.PinnedSigningKey;
        var pinnedKeyId = agent.PinnedSigningKeyId;
        var before = await FetchAsync(agentClient, agent);

        await RotateAsync(admin);

        // AcceptBundle verifies against the key this agent pinned at enrollment, so a changed
        // signing key would fail here rather than in some later assertion.
        var after = await FetchAsync(agentClient, agent, before.Revision);

        Assert.Equal(pinnedKey, agent.PinnedSigningKey);
        Assert.Equal(pinnedKeyId, agent.PinnedSigningKeyId);
        Assert.True(after.Revision > before.Revision);
    }

    // In an incident the event log is usually the only thing that answers what changed and who
    // did it - and if the operator reading the channel is not the one who pressed it, the
    // notification is the most important thing the fleet could say.
    [Fact]
    public async Task A_rotation_is_recorded_and_announced()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var rotation = await RotateAsync(admin);

        var events = (await admin.GetFromJsonAsync<EventResponse[]>("/api/events"))!;
        var recorded = events.First(entry => entry.Kind == "fleet.rotated");
        Assert.Equal(ControlPlaneFixture.AdminUser, recorded.Actor);
        Assert.Contains($"generation {rotation.Generation}", recorded.Message);

        Assert.Contains(fixture.Notifications.Sent, sent => sent.Kind == "fleet.rotated");
    }

    // A scraper watching the fleet should see the generation move: it is the one number that says
    // an identity was replaced, and the alert worth having is one that never moves.
    [Fact]
    public async Task The_scrape_endpoint_reports_the_new_generation()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var rotation = await RotateAsync(admin);

        var metrics = fixture.CreateClient();
        metrics.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ControlPlaneFixture.MetricsToken);

        var text = await metrics.GetStringAsync("/api/metrics");
        Assert.Contains($"awg_fleet_generation {rotation.Generation}", text);
    }

    private static async Task<ClientResponse> ReadAsync(HttpClient admin, string clientId)
        => (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!.Single(entry => entry.Id == clientId);
}
