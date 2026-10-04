using System.Net;
using System.Net.Http.Json;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Grouping peers by the person who holds them, and the order the operator dragged them into.
///
/// All of it is panel-side bookkeeping, so the thing worth asserting is what it must never do:
/// change what a node runs, or lose a config when a label goes away. The arrangement is stored
/// here rather than in a browser precisely so a second device reads the same list, which is what
/// a fresh GET in these tests stands in for.
/// </summary>
public class ClientGroupTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private async Task<ClientResponse> PeerAsync(HttpClient admin, string name)
    {
        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private async Task<ClientGroupResponse> GroupAsync(HttpClient admin, string name)
    {
        var created = await admin.PostAsJsonAsync("/api/groups", new CreateClientGroupRequest(name));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ClientGroupResponse>())!;
    }

    private static async Task<HttpResponseMessage> ArrangeAsync(HttpClient admin, params ClientGroupPlacement[] buckets)
        => await admin.PutAsJsonAsync("/api/clients/arrangement", new ArrangeClientsRequest(buckets));

    /// <summary>Only the peers this test made, so a sibling test's peers cannot decide the order.</summary>
    private static string[] OrderWithin(IEnumerable<ClientResponse> clients, params ClientResponse[] mine)
    {
        var ids = mine.Select(peer => peer.Id).ToHashSet(StringComparer.Ordinal);
        return clients.Where(peer => ids.Contains(peer.Id)).Select(peer => peer.Id).ToArray();
    }

    private static async Task<long> RevisionAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;

    [Fact]
    public async Task A_group_holds_the_devices_of_one_person_in_the_order_they_were_dragged_into()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Alice");

        var laptop = await PeerAsync(admin, "alice-laptop");
        var phone = await PeerAsync(admin, "alice-phone");
        var loose = await PeerAsync(admin, "alice-spare");

        var response = await ArrangeAsync(
            admin,
            new ClientGroupPlacement(group.Id, [phone.Id, laptop.Id]),
            new ClientGroupPlacement(null, [loose.Id]));

        response.EnsureSuccessStatusCode();

        // Another device asks the panel, not its own memory.
        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        var filed = clients.Where(peer => peer.GroupId == group.Id).Select(peer => peer.Id).ToArray();

        Assert.Equal([phone.Id, laptop.Id], filed);
        Assert.Null(clients.Single(peer => peer.Id == loose.Id).GroupId);

        // The list arrives in the order it is drawn in, so a second panel does not have to know
        // how to sort it: a group's peers together, and the peers filed under nobody last.
        Assert.Equal([phone.Id, laptop.Id, loose.Id], OrderWithin(clients, phone, laptop, loose));

        // And it survives a second drag inside the group.
        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [laptop.Id, phone.Id]))).EnsureSuccessStatusCode();

        clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.Equal([laptop.Id, phone.Id], clients.Where(peer => peer.GroupId == group.Id).Select(peer => peer.Id));
    }

    [Fact]
    public async Task Filing_a_peer_under_a_person_is_not_a_fleet_change()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var peer = await PeerAsync(admin, "quiet-move");
        var revision = await RevisionAsync(admin);

        var group = await GroupAsync(admin, "Bob");
        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [peer.Id]))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/groups/order", new ReorderClientGroupsRequest([group.Id]))).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/groups/{group.Id}")).EnsureSuccessStatusCode();

        // A label changes no peer material, so no node has anything to re-apply - the same
        // reason a rename does not bump the revision.
        Assert.Equal(revision, await RevisionAsync(admin));
    }

    [Fact]
    public async Task Deleting_a_group_hands_its_peers_back_rather_than_deleting_them()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Carol");
        var peer = await PeerAsync(admin, "carol-tablet");

        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [peer.Id]))).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/groups/{group.Id}")).EnsureSuccessStatusCode();

        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        var kept = clients.Single(item => item.Id == peer.Id);

        Assert.Null(kept.GroupId);
        Assert.True(kept.Enabled);

        var groups = (await admin.GetFromJsonAsync<ClientGroupResponse[]>("/api/groups"))!;
        Assert.DoesNotContain(group.Id, groups.Select(item => item.Id));
    }

    [Fact]
    public async Task Two_groups_cannot_carry_the_same_name()
    {
        var admin = await fixture.CreateAdminClientAsync();
        await GroupAsync(admin, "Dave");

        var response = await admin.PostAsJsonAsync("/api/groups", new CreateClientGroupRequest("dave"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_arrangement_naming_a_peer_that_is_gone_moves_nothing()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Erin");
        var peer = await PeerAsync(admin, "erin-phone");

        var response = await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [peer.Id, "not-a-peer"]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Half an arrangement would leave a list neither the operator nor the panel arranged.
        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.Null(clients.Single(item => item.Id == peer.Id).GroupId);
    }

    [Fact]
    public async Task A_peer_cannot_be_filed_under_a_group_that_is_gone()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var peer = await PeerAsync(admin, "frank-phone");

        var response = await ArrangeAsync(admin, new ClientGroupPlacement("not-a-group", [peer.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_peer_can_only_sit_in_one_group()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var first = await GroupAsync(admin, "Grace");
        var second = await GroupAsync(admin, "Heidi");
        var peer = await PeerAsync(admin, "grace-laptop");

        var response = await ArrangeAsync(
            admin,
            new ClientGroupPlacement(first.Id, [peer.Id]),
            new ClientGroupPlacement(second.Id, [peer.Id]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_group_the_arrangement_left_out_keeps_its_peers()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var mine = await GroupAsync(admin, "Ivan");
        var theirs = await GroupAsync(admin, "Judy");

        var kept = await PeerAsync(admin, "ivan-phone");
        var moved = await PeerAsync(admin, "judy-phone");

        (await ArrangeAsync(
            admin,
            new ClientGroupPlacement(mine.Id, [kept.Id]),
            new ClientGroupPlacement(theirs.Id, [moved.Id]))).EnsureSuccessStatusCode();

        // A browser that only dragged within one group sends only that group.
        (await ArrangeAsync(admin, new ClientGroupPlacement(theirs.Id, [moved.Id]))).EnsureSuccessStatusCode();

        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.Equal(mine.Id, clients.Single(item => item.Id == kept.Id).GroupId);
    }

    [Fact]
    public async Task The_order_of_the_groups_themselves_is_remembered()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var first = await GroupAsync(admin, "Mallory");
        var second = await GroupAsync(admin, "Niaj");

        var ordered = (await admin.PutAsJsonAsync("/api/groups/order", new ReorderClientGroupsRequest([second.Id, first.Id])));
        ordered.EnsureSuccessStatusCode();

        var groups = (await admin.GetFromJsonAsync<ClientGroupResponse[]>("/api/groups"))!;
        var mine = groups.Where(group => group.Id == first.Id || group.Id == second.Id).Select(group => group.Id).ToArray();

        Assert.Equal([second.Id, first.Id], mine);
    }

    [Fact]
    public async Task A_new_peer_lands_at_the_end_of_the_list_it_was_added_to()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Olivia");

        var first = await PeerAsync(admin, "olivia-one");
        var second = await PeerAsync(admin, "olivia-two");
        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [second.Id, first.Id]))).EnsureSuccessStatusCode();

        // Created after the arrangement, so it must appear below both rather than on top of them.
        var third = await PeerAsync(admin, "olivia-three");
        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [second.Id, first.Id, third.Id]))).EnsureSuccessStatusCode();

        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.Equal([second.Id, first.Id, third.Id], OrderWithin(clients, first, second, third));
    }

    [Fact]
    public async Task A_peer_can_be_created_straight_into_a_group()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Trent");

        var created = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("trent-phone", null, group.Id));
        created.EnsureSuccessStatusCode();

        var peer = (await created.Content.ReadFromJsonAsync<ClientResponse>())!;
        Assert.Equal(group.Id, peer.GroupId);
    }

    [Fact]
    public async Task A_peer_cannot_be_created_into_a_group_that_is_gone()
    {
        var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/clients", new CreateClientRequest("victor-phone", null, "not-a-group"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Renaming_a_group_keeps_its_peers()
    {
        var admin = await fixture.CreateAdminClientAsync();
        var group = await GroupAsync(admin, "Peggy");
        var peer = await PeerAsync(admin, "peggy-phone");

        (await ArrangeAsync(admin, new ClientGroupPlacement(group.Id, [peer.Id]))).EnsureSuccessStatusCode();

        var renamed = await admin.PutAsJsonAsync($"/api/groups/{group.Id}", new UpdateClientGroupRequest("Peggy Sue"));
        renamed.EnsureSuccessStatusCode();
        Assert.Equal("Peggy Sue", (await renamed.Content.ReadFromJsonAsync<ClientGroupResponse>())!.Name);

        var clients = (await admin.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        Assert.Equal(group.Id, clients.Single(item => item.Id == peer.Id).GroupId);
    }
}
