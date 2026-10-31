using System.Net;
using System.Net.Http.Json;
using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// Whether a peer has reached a limit, as a pure function of what the panel holds.
///
/// Pure and in one place for the same reason the node health evaluator is: the sweep that switches
/// peers off, the refusal an operator meets when they enable an expired one, and the words the
/// peer list draws all ask this, and three readers disagreeing about whether somebody's config
/// works would be worse than none of them.
/// </summary>
public class PeerLimitPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_peer_with_no_limits_is_never_touched()
    {
        Assert.Equal(PeerLimitAction.None, PeerLimitPolicy.Evaluate(PeerLimits.None, enabled: true, Now));
    }

    [Fact]
    public void An_expiry_in_the_past_expires_the_peer()
    {
        var limits = new PeerLimits(ExpiresAt: Now.AddMinutes(-1));

        Assert.Equal(PeerLimitAction.Expire, PeerLimitPolicy.Evaluate(limits, enabled: true, Now));
    }

    [Fact]
    public void An_expiry_in_the_future_leaves_it_alone()
    {
        var limits = new PeerLimits(ExpiresAt: Now.AddDays(1));

        Assert.Equal(PeerLimitAction.None, PeerLimitPolicy.Evaluate(limits, enabled: true, Now));
    }

    [Fact]
    public void An_allowance_that_has_been_used_exhausts_the_peer()
    {
        var limits = new PeerLimits(QuotaBytes: 1_000, QuotaStartedAt: Now.AddDays(-1), QuotaUsedBytes: 1_000);

        Assert.Equal(PeerLimitAction.Exhaust, PeerLimitPolicy.Evaluate(limits, enabled: true, Now));
    }

    /// <summary>
    /// A peer that is both out of time and out of allowance is out of time: resetting the
    /// allowance would not put it back, so naming the quota would send the operator to fix the
    /// wrong thing.
    /// </summary>
    [Fact]
    public void Being_expired_is_reported_before_being_out_of_allowance()
    {
        var limits = new PeerLimits(
            ExpiresAt: Now.AddMinutes(-1),
            QuotaBytes: 1_000,
            QuotaStartedAt: Now.AddDays(-1),
            QuotaUsedBytes: 5_000);

        Assert.Equal(PeerLimitAction.Expire, PeerLimitPolicy.Evaluate(limits, enabled: true, Now));
    }

    [Fact]
    public void A_peer_that_is_already_off_is_left_where_it_is()
    {
        var limits = new PeerLimits(ExpiresAt: Now.AddYears(-1));

        Assert.Equal(PeerLimitAction.None, PeerLimitPolicy.Evaluate(limits, enabled: false, Now));
    }

    /// <summary>
    /// And the one exception to that: a monthly allowance renews whether the peer is on or off,
    /// because one that only renewed for the peers it had not switched off would renew for
    /// everybody except the people waiting for it.
    /// </summary>
    [Fact]
    public void A_monthly_allowance_renews_in_a_new_month_even_for_a_peer_that_is_off()
    {
        var limits = new PeerLimits(
            QuotaBytes: 1_000,
            QuotaWindow: PeerQuotaWindows.Monthly,
            QuotaStartedAt: Now.AddMonths(-1),
            QuotaUsedBytes: 1_000,
            DisabledBy: PeerDisableReasons.Quota);

        Assert.Equal(PeerLimitAction.Renew, PeerLimitPolicy.Evaluate(limits, enabled: false, Now));
    }

    [Fact]
    public void A_monthly_allowance_inside_its_own_month_does_not_renew()
    {
        var limits = new PeerLimits(
            QuotaBytes: 1_000,
            QuotaWindow: PeerQuotaWindows.Monthly,
            QuotaStartedAt: PeerLimitPolicy.MonthStart(Now),
            QuotaUsedBytes: 1_000);

        Assert.Equal(PeerLimitAction.Exhaust, PeerLimitPolicy.Evaluate(limits, enabled: true, Now));
    }

    /// <summary>A total allowance is a trial: nothing about a new month changes it.</summary>
    [Fact]
    public void A_total_allowance_never_renews()
    {
        var limits = new PeerLimits(
            QuotaBytes: 1_000,
            QuotaWindow: PeerQuotaWindows.Total,
            QuotaStartedAt: Now.AddYears(-1),
            QuotaUsedBytes: 1_000,
            DisabledBy: PeerDisableReasons.Quota);

        Assert.Equal(PeerLimitAction.None, PeerLimitPolicy.Evaluate(limits, enabled: false, Now));
    }

    [Fact]
    public void What_is_left_of_an_allowance_never_goes_negative()
    {
        var limits = new PeerLimits(QuotaBytes: 1_000, QuotaUsedBytes: 4_000);

        Assert.Equal(0, limits.QuotaRemaining);
        Assert.Null(PeerLimits.None.QuotaRemaining);
    }
}

/// <summary>
/// Expiry and allowance through the endpoints that set them, and through the sweep that applies
/// them.
///
/// The sweep is run on demand rather than waited for: it is the same method the background loop
/// calls, so what is asserted here is what runs in production and not something shaped like it.
/// </summary>
public class PeerLimitApiTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    [Fact]
    public async Task A_peer_past_its_expiry_is_disabled_by_the_sweep()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "ran-out-of-time");

        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddSeconds(-1), null, null));
        var sweep = await Sweep(client);

        Assert.True(sweep.Expired >= 1);

        var after = await Read(client, peer.Id);
        Assert.False(after.Enabled);
        Assert.Equal("expiry", after.DisabledBy);
    }

    /// <summary>
    /// Setting a limit is the panel's own bookkeeping and reaches no node; the peer disappearing
    /// from the bundle afterwards is an ordinary disable, and that is what moves the revision.
    /// </summary>
    [Fact]
    public async Task Setting_a_limit_does_not_move_the_revision_but_applying_one_does()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "bookkeeping-only");

        var before = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddDays(30), 5_000_000_000, "monthly"));

        var afterSetting = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        Assert.Equal(before, afterSetting);

        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddSeconds(-1), null, null));
        await Sweep(client);

        var afterSweep = (await client.GetFromJsonAsync<FleetResponse>("/api/fleet"))!.Revision;
        Assert.True(afterSweep > afterSetting);
    }

    /// <summary>
    /// Switching an expired peer back on would be undone by the next sweep, which is a config
    /// that comes back and dies with nothing to say why. The refusal names the decision the
    /// operator actually has to make.
    /// </summary>
    [Fact]
    public async Task Enabling_a_peer_that_is_still_expired_is_refused_with_what_to_fix()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "still-expired");

        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddSeconds(-1), null, null));
        await Sweep(client);

        var response = await client.PostAsync($"/api/clients/{peer.Id}/enable", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client_expired", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// Extending the expiry is what the panel tells an operator to do when it switches a peer off,
    /// so it puts them back rather than leaving them to find the enable button and be refused.
    /// </summary>
    [Fact]
    public async Task Extending_the_expiry_puts_the_peer_back()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "extended-again");

        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddSeconds(-1), null, null));
        await Sweep(client);
        Assert.False((await Read(client, peer.Id)).Enabled);

        var extended = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddDays(7), null, null));

        Assert.True(extended.Enabled);
        Assert.Null(extended.DisabledBy);
    }

    /// <summary>
    /// A peer an operator switched off by hand carries no reason, and raising a limit must not
    /// quietly undo their decision.
    /// </summary>
    [Fact]
    public async Task A_peer_disabled_by_hand_is_not_put_back_by_a_limit_change()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "off-on-purpose");

        (await client.PostAsync($"/api/clients/{peer.Id}/disable", null)).EnsureSuccessStatusCode();
        var updated = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddYears(1), null, null));

        Assert.False(updated.Enabled);
        Assert.Null(updated.DisabledBy);
    }

    [Fact]
    public async Task Clearing_the_limits_leaves_the_peer_unlimited()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "unlimited-again");

        await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(DateTimeOffset.UtcNow.AddDays(1), 1_000_000, "total"));
        var cleared = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(null, null, null));

        Assert.Null(cleared.ExpiresAt);
        Assert.Null(cleared.QuotaBytes);
        Assert.Null(cleared.QuotaRemaining);
        Assert.Equal(0, cleared.QuotaUsedBytes);
    }

    [Fact]
    public async Task An_allowance_of_nothing_and_an_unknown_window_are_both_refused()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "bad-allowance");

        var zero = await client.PutAsJsonAsync(
            $"/api/clients/{peer.Id}/limits",
            new UpdateClientLimitsRequest(null, 0, "total"));

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal("client_quota_invalid", (await zero.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var window = await client.PutAsJsonAsync(
            $"/api/clients/{peer.Id}/limits",
            new UpdateClientLimitsRequest(null, 1_000, "fortnightly"));

        Assert.Equal(HttpStatusCode.BadRequest, window.StatusCode);
        Assert.Equal("client_quota_window_invalid", (await window.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Resetting_an_allowance_a_peer_does_not_have_is_refused()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "nothing-to-reset");

        var response = await client.PostAsync($"/api/clients/{peer.Id}/quota/reset", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client_quota_not_set", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// Raising the allowance leaves what has been used alone - that is a change of allowance, not
    /// a new one - while switching how it is counted starts it again, because the old count was
    /// measured over a different window.
    /// </summary>
    [Fact]
    public async Task Raising_an_allowance_keeps_the_count_and_changing_the_window_restarts_it()
    {
        var client = await fixture.CreateAdminClientAsync();
        var peer = await Create(client, "allowance-changes");

        var first = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(null, 1_000_000, "total"));
        var started = first.QuotaStartedAt;
        Assert.NotNull(started);

        var raised = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(null, 2_000_000, "total"));
        Assert.Equal(started, raised.QuotaStartedAt);

        var monthly = await SetLimits(client, peer.Id, new UpdateClientLimitsRequest(null, 2_000_000, "monthly"));
        Assert.NotEqual(started, monthly.QuotaStartedAt);
        Assert.Equal(0, monthly.QuotaUsedBytes);
    }

    private static async Task<ClientResponse> Create(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/clients", new CreateClientRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<ClientResponse> SetLimits(HttpClient client, string id, UpdateClientLimitsRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/clients/{id}/limits", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private static async Task<ClientResponse> Read(HttpClient client, string id)
    {
        var peers = (await client.GetFromJsonAsync<ClientResponse[]>("/api/clients"))!;
        return Assert.Single(peers, peer => peer.Id == id);
    }

    private static async Task<PeerLimitSweep> Sweep(HttpClient client)
    {
        var response = await client.PostAsync("/api/clients/limits/sweep", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PeerLimitSweep>())!;
    }
}
