using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// What stands between an armed standby and a promotion.
///
/// This is the half of panel failover that cannot be tested end to end - arming it needs a second
/// panel, a grace period that has actually elapsed and a witness that answers - and it is also
/// the half where being wrong is expensive: a promotion made on no evidence is two panels both
/// allocating addresses out of one subnet. So the policy is pure, and every refusal has a test.
/// </summary>
public sealed class PanelFailoverPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly PanelOptions Options = TestPanelOptions.Active() with
    {
        SyncInterval = TimeSpan.FromSeconds(30),
        Grace = TimeSpan.FromMinutes(5),
        Cooldown = TimeSpan.FromMinutes(15)
    };

    [Fact]
    public void A_standby_in_touch_does_nothing()
    {
        var decision = Decide(State(syncedAt: Now.AddSeconds(-30)));

        Assert.Equal(PanelFailoverAction.None, decision.Action);
    }

    [Fact]
    public void Being_out_of_touch_inside_the_grace_period_is_still_nothing()
    {
        var decision = Decide(State(syncedAt: Now.AddMinutes(-4)));

        Assert.Equal(PanelFailoverAction.None, decision.Action);
    }

    [Fact]
    public void An_armed_standby_with_a_working_uplink_takes_over()
    {
        var decision = Decide(State(syncedAt: Now.AddMinutes(-6)), witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Promote, decision.Action);
    }

    /// <summary>
    /// The one that matters most. A standby that cannot reach the active has learned exactly one
    /// thing - that it cannot reach the active - and promoting on that alone is how a network
    /// split becomes two panels in charge.
    /// </summary>
    [Fact]
    public void A_standby_that_cannot_reach_anything_else_does_not_take_over()
    {
        var decision = Decide(State(syncedAt: Now.AddMinutes(-6)), witnessOnline: false);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("network it sits in", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_witness_at_all_it_refuses_rather_than_assuming()
    {
        var decision = Decide(
            State(syncedAt: Now.AddMinutes(-6)),
            witnessConfigured: false,
            witnessOnline: null);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("AWG_PANEL_WITNESS_URLS", decision.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A panel with no fleet state would publish an empty fleet, and every node would converge
    /// onto it and drop every peer it serves. Refused however armed, however long the silence.
    /// </summary>
    [Fact]
    public void A_standby_that_has_never_synced_is_refused()
    {
        var decision = Decide(State(syncedAt: null, snapshotAt: null), witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("never completed a sync", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_held_standby_never_takes_over()
    {
        var decision = Decide(State(syncedAt: Now.AddMinutes(-6)), held: true, witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("on hold", decision.Message, StringComparison.Ordinal);
    }

    /// <summary>Losing contact with the panel that revoked it is not a way back in.</summary>
    [Fact]
    public void A_revoked_standby_never_takes_over()
    {
        var decision = Decide(State(syncedAt: Now.AddMinutes(-6)), revoked: true, witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("revoked", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void In_manual_mode_it_only_recommends()
    {
        var decision = Decide(
            State(syncedAt: Now.AddMinutes(-6)),
            mode: PanelFailoverModes.Manual,
            witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Recommend, decision.Action);
        Assert.Contains("promote this panel by hand", decision.Message, StringComparison.Ordinal);
    }

    /// <summary>Without it, a pair broken in both directions hands the fleet back and forth every
    /// grace period - the same reluctance node failover has, for the same reason.</summary>
    [Fact]
    public void A_recent_role_change_holds_the_next_one()
    {
        var decision = Decide(
            State(syncedAt: Now.AddMinutes(-6), promotedAt: Now.AddMinutes(-2)),
            witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Hold, decision.Action);
    }

    [Fact]
    public void An_unenrolled_panel_has_no_fleet_to_take_over()
    {
        var decision = Decide(State(syncedAt: null, snapshotAt: null, enrolled: false), witnessOnline: true);

        Assert.Equal(PanelFailoverAction.Refused, decision.Action);
        Assert.Contains("not been enrolled", decision.Message, StringComparison.Ordinal);
    }

    private static PanelFailoverDecision Decide(
        PanelLocalState state,
        string mode = PanelFailoverModes.Automatic,
        bool held = false,
        bool revoked = false,
        bool witnessConfigured = true,
        bool? witnessOnline = null)
        => PanelFailoverPolicy.Decide(Now, mode, held, revoked, state, witnessConfigured, witnessOnline, Options);

    private static PanelLocalState State(
        DateTimeOffset? syncedAt,
        DateTimeOffset? snapshotAt = null,
        DateTimeOffset? promotedAt = null,
        bool enrolled = true)
        => new(
            PanelRoles.Standby,
            PanelId: "panel-b",
            AgentPrivateKey: "priv",
            AgentPublicKey: "pub",
            PinnedSigningKey: enrolled ? "key" : null,
            PinnedSigningKeyId: enrolled ? "abc" : null,
            PeerUrl: "http://panel-a.test",
            PromotedTerm: 0,
            PromotedAt: promotedAt,
            SyncedAt: syncedAt,
            // A panel that has synced has, by construction, applied a snapshot: the first
            // successful pull is the one that cannot answer 304.
            SnapshotAt: snapshotAt ?? syncedAt,
            SnapshotEtag: syncedAt is null ? null : "etag",
            SyncError: null,
            Hold: false,
            UsageCursor: null,
            NodeAvailabilityCursor: null,
            ProbeAvailabilityCursor: null);
}
