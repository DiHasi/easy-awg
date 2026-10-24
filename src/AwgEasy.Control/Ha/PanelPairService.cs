using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>One turn of the pair's loop: which role it ran as, and what it concluded.</summary>
public sealed record PanelRound(
    DateTimeOffset At,
    string Role,
    PanelFailoverAction Action,
    string Message,
    bool? WitnessOnline);

/// <summary>
/// The pair's single loop, branching on the role rather than running two services that would have
/// to agree about which of them is allowed to act.
///
/// As a standby it pulls: the configuration when its ETag has moved, the live half and the next
/// slice of history every time, then it reports what it applied and asks the policy whether it
/// should take over. As the active panel it watches: one cheap signed call to each peer, and if
/// one answers at a higher term this panel has been superseded and steps down.
///
/// Both halves are in one loop because a promotion or a demotion has to change behaviour without
/// a restart. A panel that was demoted while serving has to start replicating from its successor
/// immediately - that is the whole value of the fencing - and a panel that has just taken over has
/// to stop replicating from the corpse of the one it replaced.
/// </summary>
public sealed class PanelPairService(
    IServiceScopeFactory scopes,
    PanelContext context,
    PanelLocalStore store,
    PanelRepository panels,
    PanelSnapshotRepository snapshots,
    IPanelWitness witness,
    ControlOptions options,
    EventLog events,
    Notifier notifier,
    ILogger<PanelPairService> logger) : BackgroundService
{
    public const string Actor = "panel replication";

    private readonly SemaphoreSlim _gate = new(1, 1);

    // What was announced last, so a pair that has been out of touch for a day is one notification
    // rather than one every thirty seconds for a day.
    private string? _lastAlert;

    public PanelRound? LastRound { get; private set; }

    public bool? WitnessOnline { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Panel.SyncInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A round that throws is a round skipped, never a loop stopped: the next time the
                // other panel dies is exactly when this has to still be running.
                logger.LogError(exception, "Panel replication round failed. Retrying at the next interval.");
            }
        }
    }

    public async Task<PanelRound> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<PanelReplicationClient>();
            var promotion = scope.ServiceProvider.GetRequiredService<PanelPromotionService>();

            var now = DateTimeOffset.UtcNow;
            var state = context.State;

            if (!state.IsActive && !state.IsEnrolled)
            {
                var enrollment = await EnrollAsync(client, state, now, cancellationToken);
                state = context.State;

                if (!state.IsEnrolled)
                {
                    LastRound = enrollment;
                    return LastRound;
                }

                // Falls through into the first pull rather than waiting a whole interval: a panel
                // that has just been enrolled holds nothing, and holding nothing is the one state
                // it must not stay in.
            }

            LastRound = state.IsActive
                ? await WatchPeersAsync(client, promotion, state, now, cancellationToken)
                : await ReplicateAsync(client, promotion, state, now, cancellationToken);

            return LastRound;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------------------- enrollment

    private async Task<PanelRound> EnrollAsync(
        PanelReplicationClient client,
        PanelLocalState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var peerUrl = state.PeerUrl ?? options.Panel.PeerUrl;
        if (peerUrl is null || options.Panel.EnrollmentToken is null)
        {
            return Idle(now, state,
                "This panel is configured as a standby but has not been enrolled. Issue a standby token on the "
                + "panel in charge, then set AWG_PANEL_PEER_URL and AWG_PANEL_ENROLLMENT_TOKEN here.");
        }

        var (response, error) = await client.EnrollAsync(peerUrl, options.Panel.EnrollmentToken, state.AgentPublicKey, cancellationToken);
        if (response is null)
        {
            store.RecordSyncFailure(error!.Message);
            context.Reload();
            logger.LogWarning("Panel enrollment at {PeerUrl} failed: {Code} - {Message}", peerUrl, error.Code, error.Message);
            return Idle(now, state, $"Enrollment at {peerUrl} failed: {error.Message}");
        }

        store.RecordEnrollment(response.PanelId, response.ControlSigningPublicKey, response.ControlSigningKeyId, response.PeerUrl ?? peerUrl);
        context.Reload();

        var message = $"Enrolled into the pair behind '{response.ActivePanelName}' as panel {response.PanelId} at term {response.Term}.";
        events.Record("panel.enrolled", message, actor: Actor);
        logger.LogInformation("{Message}", message);

        return new PanelRound(now, PanelRoles.Standby, PanelFailoverAction.None, message, null);
    }

    // ----------------------------------------------------------------------------- active: watch

    /// <summary>
    /// One signed hello to each peer that has told us where it lives. The only thing being looked
    /// for is a higher term, which means this panel has been superseded - and because the term is
    /// a number rather than a judgement, it steps down without asking anybody.
    /// </summary>
    private async Task<PanelRound> WatchPeersAsync(
        PanelReplicationClient client,
        PanelPromotionService promotion,
        PanelLocalState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pair = panels.FindPairState();

        // If this is the first the fleet hears of a pair, the panel already serving it is the one
        // in charge. Adopting rather than promoting keeps the first enrollment out of the log as
        // a failover that never happened.
        if (pair.ActivePanelId is null && panels.Find(state.PanelId) is not null)
        {
            pair = pair with { Term = panels.AdoptAsActive(state.PanelId, now), ActivePanelId = state.PanelId };
        }

        panels.RecordSelf(state.PanelId, PanelRoles.Active, pair.Term, options.Panel.Url, options.Panel.PublicIp, state.SnapshotAt, now);

        var peers = panels.List()
            .Where(panel => panel.Id != state.PanelId && !panel.Revoked)
            .ToArray();

        if (peers.Length == 0)
        {
            return new PanelRound(now, PanelRoles.Active, PanelFailoverAction.None, "No standby panel is enrolled.", null);
        }

        var unreachable = new List<string>();
        foreach (var peer in peers)
        {
            if (peer.Url is not { Length: > 0 } url)
            {
                // A peer with no address cannot be watched, and a pair that cannot watch itself
                // has no fencing. Worth saying once per round rather than silently degrading.
                unreachable.Add($"{peer.Name} has not reported a URL");
                continue;
            }

            var (hello, error) = await client.HelloAsync(url, state, cancellationToken);
            if (hello is null)
            {
                unreachable.Add($"{peer.Name}: {error?.Message ?? "no answer"}");
                continue;
            }

            // What the peer just said about itself. Only a standby reports in of its own accord,
            // so without this the row for a peer that believes it is in charge would stay at
            // whatever it last claimed - which is exactly the row an operator reads during a
            // split brain.
            panels.RecordState(
                peer.Id,
                new PanelStatusReport(hello.Role, hello.Term, hello.Version, peer.Url, null, hello.SnapshotAt, null),
                now);

            if (hello.Role == PanelRoles.Active && hello.Term > pair.Term)
            {
                var reason = $"'{hello.Name}' answered as the panel in charge at term {hello.Term}, above this panel's {pair.Term}";
                await promotion.DemoteAsync(Actor, reason, hello.Term, hello.PanelId, cancellationToken);
                await AlertOnceAsync("panel.fenced", "Panel superseded", $"This panel stepped down: {reason}.", cancellationToken);
                return new PanelRound(now, PanelRoles.Standby, PanelFailoverAction.None, $"Stepped down: {reason}.", null);
            }

            if (hello.Role == PanelRoles.Active && hello.Term == pair.Term && hello.PanelId != state.PanelId)
            {
                // Two panels at the same term is the one thing the term cannot resolve, and it
                // takes a promotion on each side in the same instant to produce. Nothing is done
                // automatically - picking a winner by coin toss is worse than telling the operator
                // which two panels to look at.
                var message = $"Both this panel and '{hello.Name}' claim to be in charge at term {pair.Term}. "
                    + "Demote one of them by hand; until you do, each is accepting changes the other will not see.";
                await AlertOnceAsync("panel.split_brain", "Two panels claim to be in charge", message, cancellationToken);
                return new PanelRound(now, PanelRoles.Active, PanelFailoverAction.Refused, message, null);
            }
        }

        if (unreachable.Count > 0)
        {
            var message = $"Cannot see {string.Join("; ", unreachable)}. A pair that cannot watch itself cannot tell "
                + "which panel is in charge, so fix this before relying on a switch.";
            await AlertOnceAsync("panel.peer_silent", "Standby panel out of touch", message, cancellationToken);
            return new PanelRound(now, PanelRoles.Active, PanelFailoverAction.Refused, message, null);
        }

        _lastAlert = null;
        return new PanelRound(
            now,
            PanelRoles.Active,
            PanelFailoverAction.None,
            $"In charge at term {pair.Term}; {peers.Length} standby {(peers.Length == 1 ? "panel" : "panels")} in touch.",
            null);
    }

    // -------------------------------------------------------------------------- standby: replicate

    private async Task<PanelRound> ReplicateAsync(
        PanelReplicationClient client,
        PanelPromotionService promotion,
        PanelLocalState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var peerUrl = ResolvePeerUrl(state);
        if (peerUrl is null)
        {
            store.RecordSyncFailure("No address for the panel in charge.");
        }
        else
        {
            await PullAsync(client, state, peerUrl, now, cancellationToken);
        }

        // Unconditionally, because the pull writes its outcome - success or the reason it failed -
        // to the local row, and the cached copy is what both the policy below and the panel's own
        // pages read. Reloading only on success is how a refusal becomes invisible.
        context.Reload();
        state = context.State;

        var pair = panels.FindPairState();
        var revoked = panels.Find(state.PanelId) is { Revoked: true };

        // The witness is only asked when it could change the answer. It is outbound traffic from a
        // panel that is already having trouble, and while contact is healthy the decision is None
        // whatever it would have said.
        var outOfTouch = state.SyncedAt is null || now - state.SyncedAt > options.Panel.Grace;
        WitnessOnline = outOfTouch && witness.IsConfigured
            ? await witness.IsOnlineAsync(cancellationToken)
            : null;

        var decision = PanelFailoverPolicy.Decide(
            now,
            pair.FailoverMode,
            state.Hold,
            revoked,
            state,
            witness.IsConfigured,
            WitnessOnline,
            options.Panel);

        await ReportAsync(decision, state, cancellationToken);

        if (decision.Action == PanelFailoverAction.Promote)
        {
            var (outcome, error) = await promotion.PromoteAsync(Actor, decision.Message.TrimEnd('.'), cancellationToken);
            if (outcome is null)
            {
                var refused = $"Tried to take over and could not: {error!.Message}";
                logger.LogError("Automatic promotion refused: {Code} - {Message}", error.Code, error.Message);
                events.Record("panel.promotion_failed", refused, actor: Actor);
                await AlertOnceAsync("panel.promotion_failed", "Panel could not take over", refused, cancellationToken);
                return new PanelRound(now, PanelRoles.Standby, PanelFailoverAction.Refused, refused, WitnessOnline);
            }

            _lastAlert = null;
            return new PanelRound(now, PanelRoles.Active, PanelFailoverAction.Promote, decision.Message, WitnessOnline);
        }

        return new PanelRound(now, PanelRoles.Standby, decision.Action, decision.Message, WitnessOnline);
    }

    /// <summary>
    /// Pulls the configuration and then the live half, and reports what was applied.
    ///
    /// The snapshot is tried first on purpose: the stream updates rows the snapshot creates, so a
    /// stream applied against a configuration that has not caught up would update nothing for the
    /// nodes that are new. Doing it the other way round is not wrong, only a poll slower.
    /// </summary>
    private async Task PullAsync(
        PanelReplicationClient client,
        PanelLocalState state,
        string peerUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var localTerm = panels.FindPairState().Term;

        var snapshotFetch = await client.FetchSnapshotAsync(peerUrl, state.PanelId, state.SnapshotEtag, state, cancellationToken);
        if (!snapshotFetch.Succeeded)
        {
            store.RecordSyncFailure(snapshotFetch.Error!.Message);
            return;
        }

        DateTimeOffset? appliedAt = null;
        if (!snapshotFetch.NotModified)
        {
            if (!TryAccept(snapshotFetch.Value, state, localTerm, ControlJsonContext.Default.PanelSnapshot,
                    snapshot => (snapshot.SchemaVersion, snapshot.PanelId, snapshot.Term, snapshot.CreatedAt, snapshot.ExpiresAt),
                    out var snapshot))
            {
                return;
            }

            snapshots.ApplySnapshot(snapshot);
            appliedAt = snapshot.CreatedAt;
            localTerm = snapshot.Term;

            logger.LogInformation(
                "Applied fleet configuration at revision {Revision}: {Clients} peers, {Nodes} nodes, {Probes} probes.",
                snapshot.Fleet.Revision,
                snapshot.Clients.Length,
                snapshot.Nodes.Length,
                snapshot.Probes.Length);
        }

        var streamFetch = await client.FetchStreamAsync(peerUrl, state.PanelId, PanelStreamCursors.From(state), state, cancellationToken);
        if (!streamFetch.Succeeded)
        {
            store.RecordSyncFailure(streamFetch.Error!.Message);
            return;
        }

        if (!TryAccept(streamFetch.Value, state, localTerm, ControlJsonContext.Default.PanelStream,
                stream => (stream.SchemaVersion, stream.PanelId, stream.Term, stream.CreatedAt, stream.ExpiresAt),
                out var live))
        {
            return;
        }

        snapshots.ApplyStream(live);
        store.SetCursors(live.Usage.Cursor, live.NodeAvailability.Cursor, live.ProbeAvailability.Cursor);
        store.RecordSync(now, appliedAt, snapshotFetch.NotModified ? null : snapshotFetch.Etag);
        context.Reload();

        // The standby's own row, so its pages name the right role and term before the next
        // snapshot replaces the table with the active's copy of it.
        var applied = context.State;
        panels.RecordSelf(state.PanelId, PanelRoles.Standby, live.Term, options.Panel.Url, options.Panel.PublicIp, applied.SnapshotAt, now);

        await client.ReportStatusAsync(
            peerUrl,
            state.PanelId,
            new PanelStatusReport(
                PanelRoles.Standby,
                live.Term,
                PanelVersion.Current,
                options.Panel.Url,
                options.Panel.PublicIp,
                applied.SnapshotAt,
                applied.SyncError),
            state,
            cancellationToken);
    }

    /// <summary>
    /// Verifies a payload against the pinned signing key and runs it past the guard, then refuses
    /// anything from a panel at a lower term than this one holds.
    ///
    /// The term check is the one that matters for a pair. A panel that lost a promotion still has
    /// the fleet signing key, so it can still produce payloads that verify perfectly - and the
    /// state it would hand over is the fleet as it was before it lost.
    /// </summary>
    private bool TryAccept<T>(
        SignedPanelPayload? envelope,
        PanelLocalState state,
        long localTerm,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Func<T, (int Schema, string PanelId, long Term, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)> describe,
        out T value)
    {
        value = default!;

        using var pinned = BundleSigning.ImportPublicKey(state.PinnedSigningKey!);
        if (!PanelSigning.TryVerify(envelope, pinned, state.PinnedSigningKeyId!, typeInfo, out value, out var error))
        {
            Refuse(error);
            return false;
        }

        var (schema, panelId, term, createdAt, expiresAt) = describe(value);
        if (PanelGuard.Check(schema, panelId, createdAt, expiresAt, state.PanelId, state.SnapshotAt, DateTimeOffset.UtcNow) is { } refusal)
        {
            Refuse(refusal);
            return false;
        }

        if (term < localTerm)
        {
            Refuse(new ApiError(
                "panel_term_behind",
                $"The panel that answered is at term {term} and this one already holds {localTerm}. "
                + "It has been superseded, so what it is offering is the fleet as it was before that."));

            return false;
        }

        return true;
    }

    private void Refuse(ApiError error)
    {
        store.RecordSyncFailure(error.Message);
        context.Reload();
        logger.LogWarning("Refused a payload from the panel in charge: {Code} - {Message}", error.Code, error.Message);
    }

    /// <summary>
    /// Where to pull from. The active panel's own reported URL first, because it is better
    /// information than what this container was started with; the configured one as the bootstrap
    /// and the fallback for a fleet whose row for it is not filled in yet.
    /// </summary>
    private string? ResolvePeerUrl(PanelLocalState state)
    {
        var activeId = panels.FindPairState().ActivePanelId;
        if (activeId is not null && activeId != state.PanelId && panels.Find(activeId)?.Url is { Length: > 0 } reported)
        {
            return reported;
        }

        return state.PeerUrl ?? options.Panel.PeerUrl;
    }

    /// <summary>
    /// Announces losing and regaining contact. In manual mode too, and that is the point: a pair
    /// whose replication quietly stopped three weeks ago is discovered at the worst possible
    /// moment, which is the moment it is needed.
    /// </summary>
    private async Task ReportAsync(PanelFailoverDecision decision, PanelLocalState state, CancellationToken cancellationToken)
    {
        if (decision.Action == PanelFailoverAction.None)
        {
            if (_lastAlert is not null)
            {
                _lastAlert = null;
                var message = "Replication from the panel in charge is working again.";
                events.Record("panel.synced", message, actor: Actor);
                await NotifyAsync("panel.synced", "Panel replication restored", message, cancellationToken);
            }

            return;
        }

        if (decision.Action is PanelFailoverAction.Promote)
        {
            return;
        }

        await AlertOnceAsync(
            $"panel.{decision.Action.ToString().ToLowerInvariant()}",
            "Panel replication is behind",
            decision.Message + (state.SyncError is { } error ? $" Last error: {error}" : string.Empty),
            cancellationToken);
    }

    private async Task AlertOnceAsync(string kind, string title, string message, CancellationToken cancellationToken)
    {
        if (kind == _lastAlert)
        {
            return;
        }

        _lastAlert = kind;
        events.Record(kind, message, actor: Actor);
        await NotifyAsync(kind, title, message, cancellationToken);
    }

    private Task NotifyAsync(string kind, string title, string message, CancellationToken cancellationToken)
        => notifier.IsConfigured
            ? notifier.SendAsync(new Notification(kind, title, message, null, DateTimeOffset.UtcNow), cancellationToken)
            : Task.CompletedTask;

    private static PanelRound Idle(DateTimeOffset now, PanelLocalState state, string message)
        => new(now, state.Role, PanelFailoverAction.Refused, message, null);
}
