using System.Text.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Everything a rotation replaces, generated before the write so no `awg` process is forked while
/// a write transaction is held open.
/// </summary>
/// <param name="Obfuscation">The whole profile as it should end up, not a patch: the must-match
/// half freshly drawn, the per-client seeds carried over.</param>
public sealed record FleetRotation(
    string ServerPrivateKey,
    string ServerPublicKey,
    ServerObfuscationProfile? Obfuscation,
    IReadOnlyList<RotatedPeerMaterial> Peers);

/// <param name="PrivateKey">Null leaves the peer's own key pair alone, which is the default: a
/// node never held it, so the fleet identity being read does not expose it. A panel whose own
/// database may have been read is the case for reissuing it, and then both halves are given.</param>
public sealed record RotatedPeerMaterial(
    string ClientId,
    string PresharedKey,
    string? PrivateKey = null,
    string? PublicKey = null);

/// <param name="ShareLinksRevoked">Links swept because they name a generation that no longer
/// exists. They could never resolve again; this is what they were.</param>
public sealed record FleetRotationOutcome(int Generation, long Revision, int ShareLinksRevoked);

public sealed class FleetRepository(Database database)
{
    public FleetRecord? Find()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM fleet WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new FleetRecord(
            reader.GetInt32("generation"),
            reader.GetString("server_private_key"),
            reader.GetString("server_public_key"),
            reader.GetString("signing_private_key"),
            reader.GetString("signing_public_key"),
            reader.GetString("signing_key_id"),
            reader.GetString("subnet"),
            reader.GetInt32("listen_port"),
            reader.GetString("client_allowed_ips"),
            reader.GetStringOrNull("client_dns"),
            reader.GetInt32("tunnel_mtu"),
            reader.GetString("endpoint_host"),
            Deserialize(reader.GetStringOrNull("obfuscation_json")),
            reader.GetInt64("revision"),
            reader.GetInt64("identity_revision"),
            reader.GetTimestampOrNull("identity_rotated_at"));
    }

    public void Insert(FleetRecord fleet, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO fleet (id, generation, server_private_key, server_public_key,
                               signing_private_key, signing_public_key, signing_key_id,
                               subnet, listen_port, client_allowed_ips, client_dns, tunnel_mtu,
                               endpoint_host, obfuscation_json, revision, created_at, updated_at)
            VALUES (1, $generation, $serverPrivate, $serverPublic,
                    $signingPrivate, $signingPublic, $signingKeyId,
                    $subnet, $listenPort, $allowedIps, $dns, $tunnelMtu,
                    $endpointHost, $obfuscation, $revision, $now, $now)
            """,
            ("$generation", fleet.Generation),
            ("$serverPrivate", fleet.ServerPrivateKey),
            ("$serverPublic", fleet.ServerPublicKey),
            ("$signingPrivate", fleet.SigningPrivateKey),
            ("$signingPublic", fleet.SigningPublicKey),
            ("$signingKeyId", fleet.SigningKeyId),
            ("$subnet", fleet.Subnet),
            ("$listenPort", fleet.ListenPort),
            ("$allowedIps", fleet.ClientAllowedIps),
            ("$dns", fleet.ClientDns),
            ("$tunnelMtu", fleet.TunnelMtu),
            ("$endpointHost", fleet.EndpointHost),
            ("$obfuscation", Serialize(fleet.Obfuscation)),
            ("$revision", fleet.Revision),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Which node the failover DNS record currently points at, and when it was pointed there.
    ///
    /// Kept out of <see cref="FleetRecord"/> on purpose: it is not part of what a node runs, it
    /// never reaches a bundle, and nothing in the config-building path should be able to read it
    /// by accident.
    /// </summary>
    public (string? NodeId, DateTimeOffset? SetAt) FindActiveNode()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT active_node_id, active_node_set_at FROM fleet WHERE id = 1");
        using var reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetStringOrNull("active_node_id"), reader.GetTimestampOrNull("active_node_set_at"))
            : (null, null);
    }

    /// <summary>Null clears it, which is what removing the active node from the fleet has to do.</summary>
    public void SetActiveNode(string? nodeId, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet
               SET active_node_id = $nodeId,
                   active_node_set_at = $setAt,
                   updated_at = $now
             WHERE id = 1
            """,
            ("$nodeId", nodeId),
            ("$setAt", nodeId is null ? null : now.ToStorage()),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public string FindFailoverMode()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT failover_mode FROM fleet WHERE id = 1");
        return command.ExecuteScalar() as string ?? FailoverModes.Manual;
    }

    public void SetFailoverMode(string mode, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET failover_mode = $mode, updated_at = $now WHERE id = 1",
            ("$mode", mode),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void UpdateObfuscation(ServerObfuscationProfile? obfuscation, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET obfuscation_json = $obfuscation, updated_at = $now WHERE id = 1",
            ("$obfuscation", Serialize(obfuscation)),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void UpdateTunnelMtu(int mtu, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET tunnel_mtu = $mtu, updated_at = $now WHERE id = 1",
            ("$mtu", mtu),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adopts an identity that already exists, for the legacy import and nothing else: the
    /// generation does not move, because the key pair being written is the one the configs in the
    /// field were already issued against. Replacing an identity with a new one is
    /// <see cref="Rotate"/>, which is a different act with different consequences.
    /// </summary>
    public void ReplaceIdentity(string serverPrivateKey, string serverPublicKey, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet SET server_private_key = $private, server_public_key = $public, updated_at = $now
            WHERE id = 1
            """,
            ("$private", serverPrivateKey),
            ("$public", serverPublicKey),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the fleet identity: a new AmneziaWG key pair, the next generation, a freshly
    /// drawn wire format, a new preshared key for every peer, and one revision bump - all of it
    /// in a single transaction.
    ///
    /// One transaction because a rotation that half-applied is the worst state this panel can be
    /// in. A new server key pair with the old preshared keys, or a bumped generation with the old
    /// identity, is a fleet where nothing handshakes and the panel cannot say why. That is also
    /// why the client rows are written here rather than through
    /// <see cref="ClientRepository"/> - the same reason <c>UsageAccounting.Accrue</c> runs inside
    /// <see cref="NodeRepository.ReplacePeerStats"/>' transaction instead of beside it.
    ///
    /// The revision moves once, in the same statement as the identity, so every node picks the
    /// whole change up on its next poll. <c>config_generation</c> on the client rows is
    /// deliberately left where it was: making those rows stale is the point, and it is what lets
    /// the panel say afterwards who has been handed a working config and who has not.
    ///
    /// The signing key pair is not touched. Rotating it would re-pin every agent, and an agent
    /// whose pinned key no longer matches refuses bundles rather than fetching new ones - the
    /// fleet would stop converging exactly when it must converge fastest.
    /// </summary>
    public FleetRotationOutcome Rotate(FleetRotation rotation, DateTimeOffset now)
    {
        using var connection = database.Open();
        // BEGIN IMMEDIATE: this writes the fleet row and every client row, and a deferred
        // transaction that started as a reader would have to upgrade mid-way.
        using var transaction = connection.BeginTransaction(deferred: false);

        int generation;
        long revision;

        using (var identity = connection.Sql(
            """
            UPDATE fleet
               SET generation          = generation + 1,
                   server_private_key  = $private,
                   server_public_key   = $public,
                   obfuscation_json    = $obfuscation,
                   revision            = revision + 1,
                   -- The revision this identity starts at. SQLite reads the right-hand side from
                   -- the row as it was, so this is the same new value `revision` gets, written in
                   -- the same statement: the two can never disagree about which revision it was.
                   identity_revision   = revision + 1,
                   identity_rotated_at = $now,
                   updated_at          = $now
             WHERE id = 1
            RETURNING generation, revision
            """,
            ("$private", rotation.ServerPrivateKey),
            ("$public", rotation.ServerPublicKey),
            ("$obfuscation", Serialize(rotation.Obfuscation)),
            ("$now", now.ToStorage())))
        {
            identity.Transaction = transaction;
            using var reader = identity.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Fleet has not been initialized.");
            }

            generation = reader.GetInt32("generation");
            revision = reader.GetInt64("revision");
        }

        foreach (var peer in rotation.Peers)
        {
            // COALESCE so one statement covers both kinds of rotation: the key pair is replaced
            // when one was generated for this peer and left exactly as it was when none was.
            using var update = connection.Sql(
                """
                UPDATE clients
                   SET preshared_key = $psk,
                       private_key   = COALESCE($privateKey, private_key),
                       public_key    = COALESCE($publicKey, public_key),
                       updated_at    = $now
                 WHERE id = $id
                """,
                ("$id", peer.ClientId),
                ("$psk", peer.PresharedKey),
                ("$privateKey", peer.PrivateKey),
                ("$publicKey", peer.PublicKey),
                ("$now", now.ToStorage()));

            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }

        // Every outstanding link names the generation that has just been replaced, so none of them
        // can resolve again. Swept here rather than left to expire, so the panel is not holding
        // tokens for configs that no longer exist.
        int revoked;
        using (var shares = connection.Sql("DELETE FROM client_shares"))
        {
            shares.Transaction = transaction;
            revoked = shares.ExecuteNonQuery();
        }

        transaction.Commit();
        return new FleetRotationOutcome(generation, revision, revoked);
    }

    /// <summary>
    /// Bumps the fleet revision and returns the new value. Every change that alters what a node
    /// should be running goes through here, so a node can decide whether it is in sync by
    /// comparing one number.
    /// </summary>
    public long BumpRevision(DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET revision = revision + 1, updated_at = $now WHERE id = 1 RETURNING revision",
            ("$now", now.ToStorage()));

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? Serialize(ServerObfuscationProfile? value)
        => value is null ? null : JsonSerializer.Serialize(value, ControlJsonContext.Default.ServerObfuscationProfile);

    private static ServerObfuscationProfile? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, ControlJsonContext.Default.ServerObfuscationProfile);
}
