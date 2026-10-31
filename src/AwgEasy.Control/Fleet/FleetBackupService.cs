using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// The file an operator keeps. Everything but the snapshot is there so that the file can be read
/// by a person before it is applied to anything: which panel it came from, when, and which fleet
/// it is - the endpoint host and the generation are what distinguish two backups at a glance.
/// </summary>
/// <param name="FormatVersion">This envelope's own version, separate from the snapshot's schema
/// version inside it. The envelope changes when what surrounds the fleet changes; the schema
/// changes when the fleet itself does, and the two have no reason to move together.</param>
public sealed record FleetBackupFile(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    string PanelId,
    string PanelName,
    /// <summary>Which half of the pair it was taken on. An export from a standby is as fresh as
    /// that panel's last replication, which is the one thing a reader of the file cannot work out
    /// from the rest of it.</summary>
    string PanelRole,
    string PanelVersion,
    string EndpointHost,
    int Generation,
    long Revision,
    PanelSnapshot Snapshot);

/// <summary>
/// An export of the panel itself, and a way back from one.
///
/// It exists because of what the panel holds that nothing else does. A node can be rebuilt from
/// the install one-liner, a probe likewise, and the agents converge on whatever the panel
/// publishes - but the fleet key pair, the bundle signing key and every client's own key pair are
/// in one SQLite file, and losing it means every config in every person's hands stops working with
/// nothing to hand out in their place. <see href="panel-pair.md">A standby</see> covers losing the
/// host: it keeps a live second copy. What it cannot do is hand you a file to keep somewhere else,
/// and a replica follows a mistake as faithfully as it follows anything else - a rotation nobody
/// meant to run reaches it within one poll.
///
/// What is exported is the snapshot a standby replicates, and that is the whole point of reusing
/// it rather than defining a second thing: "what the fleet runs on" is a line this panel already
/// draws, in one place, and a backup that drew it again would be a second answer to drift from
/// the first. So the identity, the peers with their private keys, the groups and the arrangement,
/// each node's own settings, the probes, the operators, the live enrollment tokens and share
/// links and the counter baselines all travel; the evidence - what the agents reported, the
/// traffic history, the availability history, the event log - does not. A restored panel knows
/// what to publish and has observed nothing yet, which is exactly the state a panel is in when it
/// first comes up.
///
/// The file is plaintext JSON and carries the fleet private key. That is deliberate and not a
/// gap: a passphrase would make the one irrecoverable thing in the deployment recoverable only
/// while somebody remembers a second secret, which is a worse trade for a file whose whole job is
/// to still be readable years later in a bad week. Treat it exactly as you treat the panel's own
/// disk - it is the same material, in the same plaintext, and the panel says so where it is
/// downloaded.
/// </summary>
public sealed class FleetBackupService(
    PanelSnapshotRepository snapshots,
    PanelRepository panels,
    PanelLocalStore store,
    PanelContext panel,
    FleetService fleetService,
    ClientRepository clients,
    NodeRepository nodes,
    EventLog events,
    ControlOptions options,
    ILogger<FleetBackupService> logger)
{
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Builds the file. The snapshot is taken with its replication stamps left at default - this
    /// is not a payload addressed to a peer at a particular moment, and an expiry on a backup
    /// would be an expiry on the only copy of the fleet somebody has.
    /// </summary>
    public FleetBackupFile Export(string? actor)
    {
        var fleet = fleetService.Current;
        var state = panel.State;
        var now = DateTimeOffset.UtcNow;

        // Logged because of what left the panel, not because of what changed in it: this is the
        // fleet private key and every client's going out through a browser, and the one question
        // afterwards is who downloaded it and when.
        events.Record(
            "fleet.exported",
            $"Panel export downloaded: generation {fleet.Generation}, revision {fleet.Revision}.",
            actor: actor);

        logger.LogInformation("Exported the panel at generation {Generation}, revision {Revision}.", fleet.Generation, fleet.Revision);

        return new FleetBackupFile(
            CurrentFormatVersion,
            now,
            state.PanelId,
            options.Panel.Name ?? state.PanelId,
            state.Role,
            PanelVersion.Current,
            fleet.EndpointHost,
            fleet.Generation,
            fleet.Revision,
            snapshots.BuildSnapshot(state.PanelId));
    }

    /// <summary>
    /// Puts a file back. Applied in the one transaction a standby applies a snapshot in, for the
    /// same reason: a half-restored panel holds one fleet's identity and another fleet's peers,
    /// serves nobody, and cannot say why.
    /// </summary>
    /// <param name="replace">
    /// Required when this panel already has peers or nodes of its own. A restore is wholesale -
    /// nothing is merged - so running one on a panel that is already serving a fleet replaces that
    /// fleet, and every config it ever issued stops working. Asking for the flag is the difference
    /// between a recovery and an accident.
    /// </param>
    public (FleetRestoreResponse? Result, ApiError Error) Restore(FleetBackupFile? backup, bool replace, string? actor)
    {
        if (backup?.Snapshot is not { } snapshot)
        {
            return (null, new ApiError("backup_unreadable", "That file is not a panel export this build can read."));
        }

        if (backup.FormatVersion > CurrentFormatVersion)
        {
            return (null, new ApiError(
                "backup_too_new",
                $"That export was written by a newer panel (format {backup.FormatVersion}, this one reads "
                + $"{CurrentFormatVersion}). Upgrade this panel before restoring it."));
        }

        // The same check a standby makes on a snapshot arriving over the network, and the same
        // answer: a payload this build cannot apply is refused whole rather than applied in part.
        if (!PanelSchema.CanApply(snapshot.SchemaVersion))
        {
            return (null, new ApiError(
                "backup_schema_unsupported",
                $"That export carries schema version {snapshot.SchemaVersion}, and this panel applies "
                + $"{PanelSchema.MinimumVersion}-{PanelSchema.CurrentVersion}."));
        }

        if (snapshot.Clients.Length == 0 && snapshot.Nodes.Length == 0)
        {
            // Not an error in the file so much as an export of nothing, and restoring it over a
            // working fleet would be the single most destructive thing this endpoint could do by
            // accident - the same reason a panel with no fleet state is never promoted.
            return (null, new ApiError(
                "backup_empty",
                "That export contains no peers and no nodes, so there is nothing in it to restore."));
        }

        var existingClients = clients.ListAll().Count;
        var existingNodes = nodes.List().Count;
        if (!replace && (existingClients > 0 || existingNodes > 0))
        {
            return (null, new ApiError(
                "fleet_not_empty",
                $"This panel already holds {existingClients} peer(s) and {existingNodes} node(s), and a restore replaces "
                + "them rather than merging. Confirm the replacement if that is what you mean to do - every config this "
                + "panel issued stops working at once."));
        }

        var previous = fleetService.Current;
        snapshots.ApplySnapshot(snapshot);

        // The restored fleet names whichever panel was in charge when the export was taken, at
        // whatever term the pair had reached - and that panel is usually the one being replaced.
        // So this one takes charge the way a promotion does, at a term above the restored one: if
        // the old panel is in fact still running somewhere, it is told it has been superseded on
        // its next hello and steps down, which is the pair's own rule rather than a special case
        // here. Last promotion wins.
        var now = DateTimeOffset.UtcNow;
        var term = panels.Promote(panel.PanelId, now);

        // Pinned for the same reason a panel that created its fleet pins its own key: this panel
        // now holds an identity it did not issue, and without a pinned key it could never accept a
        // snapshot later - which would make stepping down a one-way door. Never overwrites a key
        // pinned at enrollment.
        var restored = fleetService.Current;
        store.PinOwnSigningKey(restored.SigningPublicKey, restored.SigningKeyId);
        panel.Reload();

        events.Record(
            "fleet.restored",
            $"Panel restored from an export taken {backup.CreatedAt:u} on '{backup.PanelName}': generation "
            + $"{restored.Generation}, revision {restored.Revision}, {snapshot.Clients.Length} peer(s), "
            + $"{snapshot.Nodes.Length} node(s). This panel is in charge at term {term}.",
            actor: actor);

        logger.LogWarning(
            "Restored the panel from an export taken {TakenAt} (generation {Generation}, revision {Revision}); "
            + "replaced {PreviousClients} peer(s) at generation {PreviousGeneration}.",
            backup.CreatedAt,
            restored.Generation,
            restored.Revision,
            existingClients,
            previous.Generation);

        return (
            new FleetRestoreResponse(
                restored.Generation,
                restored.Revision,
                snapshot.Clients.Length,
                snapshot.Nodes.Length,
                snapshot.Probes.Length,
                snapshot.Admins.Length,
                existingClients,
                term,
                Warning),
            ApiError.Empty);
    }

    /// <summary>
    /// What a restore does not do, in the panel's own words - carried on the response for the same
    /// reason <see cref="FleetRotationResponse.Warning"/> is, because an operator who has just got
    /// their fleet back has every reason to believe they are finished.
    /// </summary>
    private const string Warning =
        "The nodes are not restored, only what the panel knows about them: each agent keeps its own key and "
        + "reconnects on its next poll, and until then it goes on serving traffic from its cached bundle. An agent "
        + "whose node row is not in this export is not enrolled here and will be turned away - re-enroll it. "
        + "Operator passwords come from the export, not from AWG_ADMIN_PASSWORD, so sign in with whatever they were "
        + "when it was taken. The traffic and availability histories are not in an export and start again from here.";
}
