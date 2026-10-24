using AwgEasy.Control;

namespace AwgEasy.Tests;

/// <summary>
/// How a panel of a pair is described to the operator. Pure, so the one number they check before
/// switching can be pinned without a panel or a network.
/// </summary>
public sealed class PanelResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The one that matters. A snapshot is re-fetched only when the fleet actually changes, so on
    /// a quiet fleet the moment it was published ages without bound while the replica is perfectly
    /// current. Reading that out as "behind" would send an operator looking for a replication
    /// fault that is not there - and it is the number they weigh before promoting.
    /// </summary>
    [Fact]
    public void A_quiet_fleet_does_not_make_a_healthy_replica_look_behind()
    {
        var standby = Panel("b", role: PanelRoles.Standby)
            with
            {
                SnapshotAt = Now.AddHours(-2),
                LastSeenAt = Now.AddSeconds(-20)
            };

        var described = PanelResponse.From(standby, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(PanelStates.Replicating, described.State);
        Assert.Equal(20, described.LagSeconds);
        Assert.Equal(Now.AddHours(-2), described.SnapshotAt);
    }

    /// <summary>Both halves are reported, because they answer different questions: what this panel
    /// holds, and whether it is still keeping up.</summary>
    [Fact]
    public void The_snapshot_stamp_is_still_reported_beside_it()
    {
        var standby = Panel("b", role: PanelRoles.Standby) with { SnapshotAt = Now.AddMinutes(-7), LastSeenAt = Now };

        var described = PanelResponse.From(standby, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(Now.AddMinutes(-7), described.SnapshotAt);
        Assert.Equal(0, described.LagSeconds);
    }

    /// <summary>The panel in charge is the source, not a copy of anything, so it is behind nothing.</summary>
    [Fact]
    public void The_panel_in_charge_is_not_behind_at_all()
    {
        var active = Panel("a", role: PanelRoles.Active) with { SnapshotAt = Now.AddHours(-3), LastSeenAt = Now };

        var described = PanelResponse.From(active, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(PanelStates.Active, described.State);
        Assert.Null(described.LagSeconds);
    }

    /// <summary>For itself a panel uses its own clock rather than the row it last wrote, which is
    /// the same fact one step closer to the truth.</summary>
    [Fact]
    public void A_panel_measures_itself_from_its_own_last_pull()
    {
        var self = Panel("b", role: PanelRoles.Standby) with { SnapshotAt = Now.AddDays(-1), LastSeenAt = Now.AddMinutes(-4) };

        var described = PanelResponse.From(
            self,
            selfId: "b",
            activePanelId: "a",
            selfSnapshotAt: Now.AddDays(-1),
            selfSyncedAt: Now.AddSeconds(-11),
            Grace,
            Now);

        Assert.True(described.IsSelf);
        Assert.Equal(11, described.LagSeconds);
    }

    [Fact]
    public void A_replica_that_has_never_reported_is_silent_and_behind_by_nothing_knowable()
    {
        var standby = Panel("b", role: PanelRoles.Standby);

        var described = PanelResponse.From(standby, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(PanelStates.Silent, described.State);
        Assert.Null(described.LagSeconds);
        Assert.Contains("never reported in", described.StateReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replica_out_of_touch_past_the_grace_period_reads_as_silent()
    {
        var standby = Panel("b", role: PanelRoles.Standby)
            with
            {
                SnapshotAt = Now.AddMinutes(-20),
                LastSeenAt = Now.AddMinutes(-6)
            };

        var described = PanelResponse.From(standby, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(PanelStates.Silent, described.State);
    }

    /// <summary>A revoked panel was cut off deliberately, and that outranks whatever it last said
    /// about itself.</summary>
    [Fact]
    public void Revoked_outranks_every_other_state()
    {
        var standby = Panel("b", role: PanelRoles.Standby) with { Revoked = true, LastSeenAt = Now, SnapshotAt = Now };

        var described = PanelResponse.From(standby, selfId: "a", activePanelId: "a", null, null, Grace, Now);

        Assert.Equal(PanelStates.Revoked, described.State);
    }

    private static PanelRecord Panel(string id, string role)
        => new(
            id,
            Name: $"panel-{id}",
            Hostname: null,
            AgentPublicKey: "pub",
            Revoked: false,
            EnrolledAt: Now.AddDays(-7),
            Url: $"https://{id}.example.com",
            PublicIp: "203.0.113.1",
            AgentVersion: "1.11.0.0",
            Role: role);
}
