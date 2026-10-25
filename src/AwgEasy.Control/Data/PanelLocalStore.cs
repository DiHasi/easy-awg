using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// What is true of this panel and of no other: the role it is in, the key pair it signs peer
/// requests with, the fleet signing key it pinned, and how far its replication has got.
/// </summary>
/// <param name="PromotedTerm">The term this panel last became active at. Zero if it never has.</param>
/// <param name="SyncedAt">The last pull that succeeded, on this panel's own clock - which is what
/// the grace period is measured against, so a peer with a wandering clock cannot hold it back.</param>
/// <param name="SnapshotAt">The active's stamp on the newest snapshot applied here, and the
/// monotonicity anchor that refuses an older one.</param>
/// <param name="Hold">The operator's brake. An armed standby that is held never promotes itself.</param>
public sealed record PanelLocalState(
    string Role,
    string PanelId,
    string AgentPrivateKey,
    string AgentPublicKey,
    string? PinnedSigningKey,
    string? PinnedSigningKeyId,
    string? PeerUrl,
    long PromotedTerm,
    DateTimeOffset? PromotedAt,
    DateTimeOffset? SyncedAt,
    DateTimeOffset? SnapshotAt,
    string? SnapshotEtag,
    string? SyncError,
    bool Hold,
    string? UsageCursor,
    string? NodeAvailabilityCursor,
    string? ProbeAvailabilityCursor)
{
    public bool IsActive => Role == PanelRoles.Active;

    /// <summary>True once this panel has been given an identity by the panel it replicates.</summary>
    public bool IsEnrolled => PinnedSigningKey is not null && PinnedSigningKeyId is not null;

    /// <summary>
    /// True once a snapshot has been applied here. The hard precondition for promotion: a panel
    /// promoted without one would publish an empty fleet, and every node would dutifully converge
    /// onto it and drop every peer it serves.
    /// </summary>
    public bool HasFleetState => SnapshotAt is not null;
}

/// <summary>
/// Owner of <c>panel_local</c>, the one table a snapshot never replaces.
///
/// It has to be outside the replicated set in both directions. A standby that replicated the
/// active's role would read itself as active the moment it first synced; a promotion that lived in
/// the replicated set would be undone by the next snapshot from a panel that has not yet heard
/// about it. Which is also why the role is here rather than in the environment: an operator who
/// promotes a standby in an emergency must not have a container restart put it back.
/// </summary>
public sealed class PanelLocalStore(Database database)
{
    /// <summary>
    /// Reads the local row, creating it on first run with a freshly generated key pair.
    ///
    /// The key pair is this panel's own, generated here and never sent anywhere but as a public
    /// half: the same arrangement as an agent's, and for the same reason - what authenticates this
    /// panel to its peer should never cross the wire.
    /// </summary>
    public PanelLocalState Ensure(string configuredRole, string? peerUrl)
    {
        if (Find() is { } existing)
        {
            // The configured peer URL is a bootstrap, so it fills a gap and never overrides what
            // replication has since learned: the active panel's own reported URL is better
            // information than the one this container was started with months ago.
            if (existing.PeerUrl is null && !string.IsNullOrWhiteSpace(peerUrl))
            {
                SetPeerUrl(peerUrl);
                return Find()!;
            }

            return existing;
        }

        using var key = BundleSigning.CreateKey();
        var now = DateTimeOffset.UtcNow;

        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO panel_local (id, role, panel_id, agent_private_key, agent_public_key, peer_url, created_at, updated_at)
            VALUES (1, $role, $panelId, $private, $public, $peerUrl, $now, $now)
            """,
            ("$role", configuredRole),
            ("$panelId", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            ("$private", BundleSigning.ExportPrivateKey(key)),
            ("$public", BundleSigning.ExportPublicKey(key)),
            ("$peerUrl", peerUrl),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
        return Find()!;
    }

    public PanelLocalState? Find()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM panel_local WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PanelLocalState(
            reader.GetString("role"),
            reader.GetString("panel_id"),
            reader.GetString("agent_private_key"),
            reader.GetString("agent_public_key"),
            reader.GetStringOrNull("pinned_signing_key"),
            reader.GetStringOrNull("pinned_signing_key_id"),
            reader.GetStringOrNull("peer_url"),
            reader.GetInt64("promoted_term"),
            reader.GetTimestampOrNull("promoted_at"),
            reader.GetTimestampOrNull("synced_at"),
            reader.GetTimestampOrNull("snapshot_at"),
            reader.GetStringOrNull("snapshot_etag"),
            reader.GetStringOrNull("sync_error"),
            reader.GetBoolean("hold"),
            reader.GetStringOrNull("usage_cursor"),
            reader.GetStringOrNull("node_availability_cursor"),
            reader.GetStringOrNull("probe_availability_cursor"));
    }

    /// <summary>
    /// Takes the identity the active panel assigned and pins its signing key, in one statement
    /// with the id: a panel holding one and not the other could neither be addressed nor verify
    /// anything it was handed.
    /// </summary>
    public void RecordEnrollment(string panelId, string signingPublicKey, string signingKeyId, string? peerUrl)
        => Update(
            """
            UPDATE panel_local
               SET panel_id              = $panelId,
                   pinned_signing_key    = $key,
                   pinned_signing_key_id = $keyId,
                   peer_url              = COALESCE($peerUrl, peer_url),
                   updated_at            = $now
             WHERE id = 1
            """,
            ("$panelId", panelId),
            ("$key", signingPublicKey),
            ("$keyId", signingKeyId),
            ("$peerUrl", peerUrl));

    /// <summary>
    /// Pins the fleet signing key this panel already holds, if nothing is pinned yet.
    ///
    /// The panel that created the fleet never enrolls into anything, so it has no pinned key - and
    /// without one it can never accept a snapshot, which means it can never become a standby. It
    /// does not need to be told the key by a peer: the key is in its own fleet row, it is the same
    /// key the other panel signs with, and the signing key pair is the one thing a rotation
    /// deliberately leaves alone. COALESCE so this can never overwrite a key pinned at enrollment,
    /// which is the only one that proves anything about where the state came from.
    /// </summary>
    public void PinOwnSigningKey(string signingPublicKey, string signingKeyId)
        => Update(
            """
            UPDATE panel_local
               SET pinned_signing_key    = COALESCE(pinned_signing_key, $key),
                   pinned_signing_key_id = COALESCE(pinned_signing_key_id, $keyId),
                   updated_at            = $now
             WHERE id = 1
            """,
            ("$key", signingPublicKey),
            ("$keyId", signingKeyId));

    /// <param name="term">The term this panel is active at, or the term it last saw when stepping
    /// down. Recorded either way: it is what a returning panel compares against.</param>
    public void SetRole(string role, long term, DateTimeOffset at)
        => Update(
            """
            UPDATE panel_local
               SET role          = $role,
                   promoted_term = $term,
                   promoted_at   = $at,
                   updated_at    = $now
             WHERE id = 1
            """,
            ("$role", role),
            ("$term", term),
            ("$at", at.ToStorage()));

    /// <summary>A pull that worked. The error is cleared here rather than left to age out: what
    /// the panel shows has to be the state now, not the last thing that went wrong.</summary>
    public void RecordSync(DateTimeOffset syncedAt, DateTimeOffset? snapshotAt, string? etag)
        => Update(
            """
            UPDATE panel_local
               SET synced_at     = $syncedAt,
                   snapshot_at   = COALESCE($snapshotAt, snapshot_at),
                   snapshot_etag = COALESCE($etag, snapshot_etag),
                   sync_error    = NULL,
                   updated_at    = $now
             WHERE id = 1
            """,
            ("$syncedAt", syncedAt.ToStorage()),
            ("$snapshotAt", snapshotAt.ToStorage()),
            ("$etag", etag));

    /// <summary>
    /// A pull that did not work. <c>synced_at</c> is deliberately left where it was: it is the
    /// clock the grace period runs against, and moving it on a failure would mean a standby could
    /// never notice that the active had gone.
    /// </summary>
    public void RecordSyncFailure(string error)
        => Update(
            "UPDATE panel_local SET sync_error = $error, updated_at = $now WHERE id = 1",
            ("$error", error));

    public void SetCursors(string? usage, string? nodeAvailability, string? probeAvailability)
        => Update(
            """
            UPDATE panel_local
               SET usage_cursor              = COALESCE($usage, usage_cursor),
                   node_availability_cursor  = COALESCE($nodes, node_availability_cursor),
                   probe_availability_cursor = COALESCE($probes, probe_availability_cursor),
                   updated_at                = $now
             WHERE id = 1
            """,
            ("$usage", usage),
            ("$nodes", nodeAvailability),
            ("$probes", probeAvailability));

    public void SetHold(bool held)
        => Update("UPDATE panel_local SET hold = $hold, updated_at = $now WHERE id = 1", ("$hold", held ? 1 : 0));

    public void SetPeerUrl(string? url)
        => Update("UPDATE panel_local SET peer_url = $url, updated_at = $now WHERE id = 1", ("$url", url));

    /// <summary>
    /// Forgets what a promoted panel's replication state said, so a panel that has taken over does
    /// not look like one that is five minutes behind itself. The cursors stay: the history rows
    /// they point into are still there, and a panel that is later demoted resumes rather than
    /// re-pulling ninety days.
    /// </summary>
    public void ClearSyncState()
        => Update("UPDATE panel_local SET sync_error = NULL, updated_at = $now WHERE id = 1");

    private void Update(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.Sql(sql, [..parameters, ("$now", DateTimeOffset.UtcNow.ToStorage())]);
        command.ExecuteNonQuery();
    }
}
