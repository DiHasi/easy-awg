namespace AwgEasy.Control;

/// <summary>
/// Turns the pair's state into the one response every panel page reads.
///
/// It lives apart from <see cref="PanelApi"/> because six endpoints return it and because the
/// warning is the interesting part: a pair is either working or degraded in one of a handful of
/// specific ways, and an operator should be told which without having to compare four fields.
/// </summary>
public sealed class PanelPairDescriber(
    PanelContext context,
    PanelRepository panels,
    PanelPairService pair,
    IPanelWitness witness,
    IDnsRecordUpdater updater,
    ControlOptions options)
{
    public PanelResponse[] Panels()
    {
        var state = context.State;
        var pairState = panels.FindPairState();
        var now = DateTimeOffset.UtcNow;

        return [..panels.List()
            .Select(panel => PanelResponse.From(panel, state.PanelId, pairState.ActivePanelId, state.SnapshotAt, options.Panel.Grace, now))
            .OrderByDescending(panel => panel.IsActive)
            .ThenBy(panel => panel.Revoked)
            .ThenBy(panel => panel.EnrolledAt)];
    }

    public PanelPairResponse Describe()
    {
        var state = context.State;
        var pairState = panels.FindPairState();
        var round = pair.LastRound;
        var list = Panels();
        var self = list.FirstOrDefault(panel => panel.IsSelf);

        return new PanelPairResponse(
            state.Role,
            state.PanelId,
            self?.Name ?? options.Panel.Name ?? Environment.MachineName,
            Math.Max(pairState.Term, state.PromotedTerm),
            pairState.ActivePanelId,
            pairState.PromotedAt,
            pairState.FailoverMode,
            state.Hold,
            state.IsEnrolled || state.IsActive,
            state.HasFleetState,
            state.SyncedAt,
            state.SnapshotAt,
            state.SyncError,
            (int)options.Panel.SyncInterval.TotalSeconds,
            (int)options.Panel.Grace.TotalSeconds,
            (int)options.Panel.Cooldown.TotalSeconds,
            witness.IsConfigured,
            pair.WitnessOnline,
            updater.ProviderName,
            updater.IsConfigured,
            options.Panel.DnsRecordName,
            options.Panel.Url,
            options.Panel.PublicIp,
            round?.At,
            round?.Action.ToString().ToLowerInvariant(),
            round?.Message,
            list,
            Warn(state, pairState, list));
    }

    /// <summary>
    /// The most important thing wrong with the pair, in one sentence, or null when nothing is.
    ///
    /// Ordered by what would hurt first. A standby that cannot take over at all beats a pair that
    /// merely cannot watch itself, which beats a record nobody will move - and only one is shown,
    /// because a card that lists four caveats is one an operator stops reading.
    /// </summary>
    private string? Warn(PanelLocalState state, PanelPairState pairState, PanelResponse[] list)
    {
        if (!state.IsActive && !state.IsEnrolled)
        {
            return "This panel is configured as a standby but has not been enrolled. Issue a standby token on the "
                + "panel in charge, then set AWG_PANEL_PEER_URL and AWG_PANEL_ENROLLMENT_TOKEN here and restart.";
        }

        if (!state.IsActive && !state.HasFleetState)
        {
            return "This panel has never applied a snapshot, so it holds no fleet identity, no peers and no nodes. "
                + "It cannot be promoted until it has synced at least once.";
        }

        if (pair.LastRound is { Action: PanelFailoverAction.Refused } refused
            && refused.Message.Contains("claim to be in charge", StringComparison.Ordinal))
        {
            return refused.Message;
        }

        if (list.Length <= 1)
        {
            return state.IsActive
                ? "No standby panel is enrolled, so there is nothing to fail over to. This panel is the only copy of "
                  + "the fleet identity and of every client key."
                : null;
        }

        if (list.FirstOrDefault(panel => !panel.Revoked && panel.Url is null) is { } unreachable)
        {
            return $"'{unreachable.Name}' has not reported a URL, so the pair cannot watch it. Set AWG_PANEL_URL there: "
                + "without it a superseded panel is never told, and both can end up accepting changes.";
        }

        if (list.FirstOrDefault(panel => panel.State == PanelStates.Silent && !panel.Revoked) is { } silent)
        {
            return $"'{silent.Name}' is out of touch: {silent.StateReason} A pair whose replication has stopped is "
                + "discovered at the worst moment, which is the moment it is needed.";
        }

        if (pairState.FailoverMode == PanelFailoverModes.Automatic && !witness.IsConfigured)
        {
            return "Automatic promotion is armed but no witness URLs are configured, so a standby cannot tell the panel "
                + "in charge being gone from its own uplink being gone.";
        }

        if (options.Panel.DnsRecordName is null)
        {
            return "No panel record is configured (AWG_PANEL_DNS_RECORD_NAME), so after a switch the agents keep "
                + "reaching the old panel until you point them at the new one. Nodes are fail-static, so they keep "
                + "serving traffic meanwhile.";
        }

        if (state.Hold)
        {
            return "This panel is held: it will not promote itself even if automatic promotion is armed.";
        }

        return null;
    }
}
