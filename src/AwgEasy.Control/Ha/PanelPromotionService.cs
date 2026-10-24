using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <param name="RecordWarning">What the panel record could not be made to say, if anything. A
/// promotion is reported as done even when this is set - see <see cref="PanelPromotionService"/>
/// for why that is not the same decision the node switch makes.</param>
public sealed record PanelPromotionOutcome(long Term, string? RecordWarning);

/// <summary>
/// Taking charge, and stepping down.
///
/// Promotion is one write and one consequence. The write is the term: incremented in the same
/// statement that names the panel holding it, so the two can never disagree, and monotonic, so
/// the pair can always say which of them promoted last without anybody arbitrating. The
/// consequence is the record the agents follow, moved through the same
/// <see cref="IDnsRecordUpdater"/> the node record uses.
///
/// Those two are deliberately not atomic, and this is the one place the panel pair behaves
/// differently from node failover. <see cref="DnsFailoverService"/> refuses to mark a node active
/// when the record did not move, because "active" there means "this is where client traffic
/// goes", and a panel that claims otherwise is lying about the one fact an operator checks under
/// pressure. Here "active" means "this panel is in charge of the fleet", which is true the moment
/// the term moves. A panel that refused to take over because a DNS API was unreachable would be
/// useless in precisely the situation it exists for - so it takes over, and says loudly if the
/// record still has to be moved by hand.
///
/// That is safe because of what the agents do when they reach the wrong panel. A superseded panel
/// refuses every agent call, and a node that cannot reach its panel is fail-static: it re-applies
/// its cached bundle and keeps serving traffic. So a record that has not caught up costs the
/// fleet its management for a while, never its data plane.
/// </summary>
public sealed class PanelPromotionService(
    PanelContext context,
    PanelLocalStore store,
    PanelRepository panels,
    IDnsRecordUpdater updater,
    ControlOptions options,
    EventLog events,
    Notifier notifier,
    ILogger<PanelPromotionService> logger)
{
    /// <summary>The record agents follow to reach the panel. Null when there is none to move,
    /// which is a supported deployment: the operator points the agents by hand.</summary>
    public string? RecordName => options.Panel.DnsRecordName;

    /// <param name="reason">Why, in the operator's log. "promoted by hand" reads differently from
    /// "the panel in charge has been unreachable for 6 min", and in an incident the difference is
    /// the first thing anybody wants.</param>
    public async Task<(PanelPromotionOutcome? Outcome, ApiError? Error)> PromoteAsync(
        string? actor,
        string reason,
        CancellationToken cancellationToken)
    {
        var state = context.State;
        if (state.IsActive)
        {
            return (null, new ApiError("panel_already_active", "This panel is already the one in charge."));
        }

        // The hard one. A panel promoted without fleet state has an empty clients table, and the
        // first thing it would do is serve every node a bundle with no peers in it - which every
        // node would apply, because that is what a node is for. The whole fleet would stop serving
        // traffic within one poll interval, and the panel would report it as a successful switch.
        if (!state.HasFleetState)
        {
            return (null, new ApiError(
                "panel_never_synced",
                "This panel has never completed a sync, so it holds no clients, no nodes and no fleet identity. "
                + "Promoting it would publish an empty fleet and every node would drop every peer it serves. "
                + "Get it syncing from the panel in charge first."));
        }

        if (panels.Find(state.PanelId) is { Revoked: true })
        {
            return (null, new ApiError(
                "panel_revoked",
                "This panel is revoked: the panel in charge stopped replicating to it on purpose, so what it holds "
                + "is as old as the revocation. Restore it there before promoting it."));
        }

        var now = DateTimeOffset.UtcNow;
        var term = panels.Promote(state.PanelId, now);

        // The role before the record: whatever the provider does next, this panel is the one in
        // charge from here, and it must start accepting writes and stop replicating.
        store.SetRole(PanelRoles.Active, term, now);
        store.ClearSyncState();
        context.Reload();
        panels.RecordSelf(state.PanelId, PanelRoles.Active, term, options.Panel.Url, options.Panel.PublicIp, state.SnapshotAt, now);

        var warning = await PointRecordAsync(cancellationToken);
        var message = $"This panel took charge at term {term}: {reason}."
            + (warning is null ? string.Empty : $" {warning}");

        events.Record("panel.promoted", message, actor: actor);
        logger.LogWarning("Panel promoted to active at term {Term}: {Reason}", term, reason);
        await NotifyAsync("panel.promoted", "Panel took charge", message, cancellationToken);

        return (new PanelPromotionOutcome(term, warning), null);
    }

    /// <summary>
    /// Steps down. Called by an operator handing the fleet back, and by the pair watch when it
    /// finds a peer at a higher term - which is not a choice but arithmetic: a panel at a lower
    /// term serves a fleet nobody is listening to, and the longer it believes otherwise the more
    /// of the operator's work it quietly swallows.
    /// </summary>
    /// <param name="observedTerm">The term that superseded this panel, and the panel holding it.
    /// Written down so this panel's own pages stop naming itself as the one in charge before the
    /// first snapshot from the new one arrives.</param>
    public async Task<(bool Demoted, ApiError? Error)> DemoteAsync(
        string? actor,
        string reason,
        long? observedTerm,
        string? observedPanelId,
        CancellationToken cancellationToken)
    {
        var state = context.State;
        if (!state.IsActive)
        {
            return (false, new ApiError("panel_not_active", "This panel is already a standby."));
        }

        // Demoting the only panel there is would leave the fleet with nothing that can write to
        // it: no new clients, no revocations, and no failover. Refused rather than warned about,
        // because the operator who asked for it is almost certainly looking at the wrong panel.
        if (panels.List().Count(panel => !panel.Revoked) <= 1)
        {
            return (false, new ApiError(
                "panel_no_peer",
                "This is the only panel in the pair, so stepping down would leave the fleet with no panel that can "
                + "change anything. Enroll a standby first, or promote the other panel instead of demoting this one."));
        }

        // Stepping down with nobody else in charge leaves the fleet with two standbys and no
        // panel that can change anything - the same dead end as demoting the only panel, reached
        // by a different route. The fencing path is exempt: it has just spoken to the panel that
        // superseded this one, which is better evidence than any row here.
        if (observedPanelId is null
            && !panels.List().Any(panel => panel.Id != state.PanelId && !panel.Revoked && panel.Role == PanelRoles.Active))
        {
            return (false, new ApiError(
                "panel_no_active_peer",
                "No other panel reports being in charge, so stepping down would leave the fleet with two standbys "
                + "and nothing that can change it. Promote the other panel first - this one then steps down on its "
                + "own the moment the two can see each other."));
        }

        var now = DateTimeOffset.UtcNow;
        var term = observedTerm ?? panels.FindPairState().Term;

        if (observedTerm is { } superseding && observedPanelId is { } winner)
        {
            panels.AcknowledgeActive(winner, superseding, now);
        }

        store.SetRole(PanelRoles.Standby, term, now);
        context.Reload();
        panels.RecordSelf(state.PanelId, PanelRoles.Standby, term, options.Panel.Url, options.Panel.PublicIp, state.SnapshotAt, now);

        var message = $"This panel stepped down to standby at term {term}: {reason}.";
        events.Record("panel.demoted", message, actor: actor);
        logger.LogWarning("Panel demoted to standby at term {Term}: {Reason}", term, reason);
        await NotifyAsync("panel.demoted", "Panel stepped down", message, cancellationToken);

        return (true, null);
    }

    /// <summary>
    /// Points the record agents follow at this panel. Returns what the operator still has to do,
    /// or null when there is nothing: unlike the node record this never fails the call around it.
    /// </summary>
    private async Task<string?> PointRecordAsync(CancellationToken cancellationToken)
    {
        if (RecordName is not { Length: > 0 } recordName)
        {
            return "No panel record is configured (AWG_PANEL_DNS_RECORD_NAME), so point the agents at this panel yourself.";
        }

        if (options.Panel.PublicIp is not { Length: > 0 } address)
        {
            return $"AWG_PANEL_PUBLIC_IP is not set on this panel, so {recordName} was left alone. "
                + "Point it at this panel yourself, or set the variable so the next switch can.";
        }

        var result = await updater.PointAsync(new DnsRecordTarget(recordName, address, options.Dns.Ttl), cancellationToken);
        return result.Outcome switch
        {
            DnsUpdateOutcome.Applied => null,
            DnsUpdateOutcome.Manual => $"No DNS provider is configured: {result.Detail}",
            _ => $"{recordName} could not be moved to {address}: {result.Error?.Message ?? "the provider refused"}. "
                + "The agents keep reaching the other panel until it is moved."
        };
    }

    private Task NotifyAsync(string kind, string title, string message, CancellationToken cancellationToken)
        => notifier.IsConfigured
            ? notifier.SendAsync(new Notification(kind, title, message, null, DateTimeOffset.UtcNow), cancellationToken)
            : Task.CompletedTask;
}
