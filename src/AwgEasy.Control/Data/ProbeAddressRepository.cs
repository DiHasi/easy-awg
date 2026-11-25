using AwgEasy.Contracts;
using Microsoft.Data.Sqlite;

namespace AwgEasy.Control;

/// <summary>The latest check of one entry or exit from one probe's network.</summary>
public sealed record ProbeAddressObservation(
    string ProbeId, string NodeId, string Direction, string Address, string IngressAddress,
    string Outcome, DateTimeOffset CheckedAt, bool? Handshake, int? LatencyMs, string? Detail, string? ObservedAddress = null);

public sealed class ProbeAddressRepository(Database database, NodeRepository nodes, ProbePathRepository paths)
{
    public void Record(string probeId, IEnumerable<ProbeResult> results, DateTimeOffset now)
    {
        var currentNodes = nodes.List().Where(node => !node.Revoked).ToDictionary(node => node.Id);
        var assigned = paths.List().Where(path => path.ProbeId == probeId).ToArray();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var result in results)
        {
            if (!currentNodes.TryGetValue(result.NodeId, out var node)
                || !NodeIngress.Addresses(node).Contains(result.Address, StringComparer.Ordinal)) continue;
            var direction = result.EgressAddress is null ? "ingress" : "egress";
            if (result.EgressAddress is { } exit
                && (!ReportedEgress.Read(node.EgressReport).Any(address => address.Address == exit)
                    || !assigned.Any(path => path.NodeId == node.Id && path.Address == exit && path.Revision <= node.AppliedRevision))) continue;
            // Without a handshake the entry path failed; no claim about an exit was possible.
            var outcome = direction == "egress" && result.Handshake != true ? ProbeOutcomes.Error : result.Outcome;
            using var command = connection.Sql(
                """
                INSERT INTO probe_address_results
                    (probe_id, node_id, direction, address, ingress_address, outcome, checked_at, handshake, latency_ms, detail, observed_address)
                VALUES ($probe, $node, $direction, $address, $ingress, $outcome, $at, $handshake, $latency, $detail, $observed)
                ON CONFLICT (probe_id, node_id, direction, address) DO UPDATE SET
                    ingress_address = excluded.ingress_address, outcome = excluded.outcome,
                    checked_at = excluded.checked_at, handshake = excluded.handshake,
                    latency_ms = excluded.latency_ms, detail = excluded.detail, observed_address = excluded.observed_address
                """,
                ("$probe", probeId), ("$node", node.Id), ("$direction", direction),
                ("$address", result.EgressAddress ?? result.Address), ("$ingress", result.Address),
                ("$outcome", outcome), ("$at", now.ToStorage()),
                ("$handshake", result.Handshake is { } handshake ? handshake ? 1 : 0 : null),
                ("$latency", result.LatencyMs), ("$detail", result.Detail), ("$observed", result.ObservedAddress));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ProbeAddressObservation[] List()
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT r.* FROM probe_address_results r JOIN probes p ON p.id = r.probe_id WHERE p.revoked = 0");
        using var reader = command.ExecuteReader();
        var rows = new List<ProbeAddressObservation>();
        while (reader.Read()) rows.Add(Read(reader));
        return [.. rows];
    }

    internal static ProbeAddressObservation Read(SqliteDataReader reader) => new(
        reader.GetString("probe_id"), reader.GetString("node_id"), reader.GetString("direction"),
        reader.GetString("address"), reader.GetString("ingress_address"), reader.GetString("outcome"),
        reader.GetTimestamp("checked_at"), reader.GetInt32OrNull("handshake") is { } handshake ? handshake != 0 : null,
        reader.GetInt32OrNull("latency_ms"), reader.GetStringOrNull("detail"), reader.GetStringOrNull("observed_address"));
}
