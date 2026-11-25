using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>A distinct client per probe and source address prevents concurrent probes from roaming
/// one another's peer. The mapping stays in the panel; bundles carry only peers and source addresses.</summary>
public sealed record ProbeEgressPeer(string ProbeId, string NodeId, string Address, string ClientId, long Revision);

public sealed class ProbePathRepository(Database database, IAwgKeyGenerator keys)
{
    public ProbeEgressPeer[] List()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM probe_egress_peers ORDER BY probe_id, node_id, address");
        using var reader = command.ExecuteReader();
        var rows = new List<ProbeEgressPeer>();
        while (reader.Read()) rows.Add(Read(reader));
        return [.. rows];
    }

    /// <summary>Allocate only on the panel, and commit peer, mapping and revision together. The node
    /// must acknowledge this revision before a failed handshake becomes evidence about an exit.</summary>
    public ProbeEgressPeer[] Ensure(ProbeRecord probe, FleetRecord fleet, IReadOnlyList<NodeRecord> nodes)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var wanted = nodes.Where(node => !node.Revoked && NodeIngress.Addresses(node).Length > 0
                && node.BundleSchemaVersion >= BundleSchema.EgressVersion)
            .SelectMany(node => ReportedEgress.Read(node.EgressReport)
                .Select(address => (NodeId: node.Id, Address: address.Address)))
            .Where(path => EgressAddress.TryParse(path.Address, out _))
            .Distinct().ToHashSet();
        var existing = new List<ProbeEgressPeer>();
        using (var command = connection.Sql("SELECT * FROM probe_egress_peers WHERE probe_id = $id", ("$id", probe.Id)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) existing.Add(Read(reader));
        }

        var changed = false;
        foreach (var stale in existing.Where(row => !wanted.Contains((row.NodeId, row.Address))).ToArray())
        {
            Execute(connection, transaction, "DELETE FROM probe_egress_peers WHERE client_id = $id", ("$id", stale.ClientId));
            Execute(connection, transaction, "DELETE FROM clients WHERE id = $id AND kind = 'probe'", ("$id", stale.ClientId));
            existing.Remove(stale);
            changed = true;
        }

        long revision;
        using (var command = connection.Sql("SELECT revision + 1 FROM fleet WHERE id = 1"))
        {
            command.Transaction = transaction;
            revision = Convert.ToInt64(command.ExecuteScalar());
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.Sql("SELECT address FROM clients"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) used.Add(reader.GetString(0));
        }
        var now = DateTimeOffset.UtcNow.ToStorage();
        foreach (var path in wanted.OrderBy(path => path.NodeId, StringComparer.Ordinal).ThenBy(path => path.Address, StringComparer.Ordinal))
        {
            if (existing.Any(row => row.NodeId == path.NodeId && row.Address == path.Address)) continue;
            // A full subnet must not break the ordinary ingress checks or anybody's existing peer.
            if (!AddressAllocator.TryAllocate(fleet.Subnet, used, out var address, out _)) break;
            var id = Guid.NewGuid().ToString("N");
            var privateKey = keys.GeneratePrivateKey();
            Execute(connection, transaction,
                """
                INSERT INTO clients (id, name, address, private_key, public_key, preshared_key,
                                     enabled, kind, created_at, updated_at)
                VALUES ($id, $name, $address, $private, $public, $psk, 1, 'probe', $now, $now)
                """,
                ("$id", id), ("$name", $"probe-egress:{id}"), ("$address", address),
                ("$private", privateKey), ("$public", keys.GeneratePublicKey(privateKey)),
                ("$psk", keys.GeneratePresharedKey()), ("$now", now));
            Execute(connection, transaction,
                "INSERT INTO probe_egress_peers VALUES ($probe, $node, $address, $client, $revision)",
                ("$probe", probe.Id), ("$node", path.NodeId), ("$address", path.Address), ("$client", id), ("$revision", revision));
            existing.Add(new(probe.Id, path.NodeId, path.Address, id, revision));
            used.Add(address);
            changed = true;
        }
        if (changed) Execute(connection, transaction, "UPDATE fleet SET revision = $revision, updated_at = $now WHERE id = 1",
            ("$revision", revision), ("$now", now));
        transaction.Commit();
        return [.. existing];
    }

    public bool Remove(string? probeId = null, string? nodeId = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var parameters = new (string, object?)[] { ("$probe", probeId), ("$node", nodeId) };
        const string selection = "SELECT client_id FROM probe_egress_peers WHERE ($probe IS NULL OR probe_id = $probe) AND ($node IS NULL OR node_id = $node)";
        using var command = connection.Sql($"DELETE FROM clients WHERE kind = 'probe' AND id IN ({selection})", parameters);
        command.Transaction = transaction;
        var changed = command.ExecuteNonQuery() > 0;
        Execute(connection, transaction,
            "DELETE FROM probe_egress_peers WHERE ($probe IS NULL OR probe_id = $probe) AND ($node IS NULL OR node_id = $node)", parameters);
        Execute(connection, transaction,
            "DELETE FROM probe_address_results WHERE ($probe IS NULL OR probe_id = $probe) AND ($node IS NULL OR node_id = $node)", parameters);
        transaction.Commit();
        return changed;
    }

    internal static ProbeEgressPeer Read(SqliteDataReader reader) => new(
        reader.GetString("probe_id"), reader.GetString("node_id"), reader.GetString("address"),
        reader.GetString("client_id"), reader.GetInt64("revision"));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Sql(sql, parameters);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
