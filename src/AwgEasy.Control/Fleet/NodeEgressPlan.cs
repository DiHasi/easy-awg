using System.Text.Json;
using System.Text.RegularExpressions;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>One of a node's addresses and what the operator calls it.</summary>
public sealed record EgressLabel(string Address, string Label);

/// <summary>
/// Which source addresses one node hands out, and under what names.
///
/// A peer picks a label, never an address. Addresses belong to a node, and the point of the fleet
/// is that a client works on any node without being reissued - so a peer pinned to one node's
/// address would quietly lose it the moment failover moved traffic elsewhere, while a peer that
/// asked for "clean" gets whatever the node it lands on calls "clean". A node with no address
/// under that name falls back to its default, which is one line here rather than a special case
/// anywhere else.
///
/// The labels never reach a node: the panel resolves them while it builds each bundle, for the
/// same reason address allocation belongs to the panel. A node stays a thing that applies what it
/// is handed.
/// </summary>
/// <param name="DefaultAddress">
/// What everyone who did not ask for something else leaves with. Null leaves the choice to the
/// kernel, which is what every node did before this existed - and on a host carrying several
/// addresses on one interface that choice is invisible and decided by the order they sit in,
/// which is exactly why an operator can now state it.
/// </param>
public sealed record NodeEgressPlan(string? DefaultAddress, EgressLabel[] Labels)
{
    /// <summary>No plan: the kernel picks, and nobody leaves through anything of their own.</summary>
    public static readonly NodeEgressPlan None = new(null, []);

    /// <summary>
    /// An operator's own word for a group of addresses, so it has to be readable rather than
    /// safe for a shell: it never reaches a node and never enters a rule. Capped because it is
    /// shown in a dropdown beside every peer.
    /// </summary>
    private static readonly Regex Name = new(@"^[A-Za-z0-9 _.-]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>What a refused label has to be told to an operator, wherever it was typed.</summary>
    public const string LabelRule =
        "An egress label must be up to 32 characters: letters, digits, spaces, dot, dash or underscore.";

    public bool IsEmpty => DefaultAddress is null && Labels.Length == 0;

    /// <summary>
    /// One label as an operator may have written it: trimmed, or null for none. Shared by the
    /// node's plan and the peer's choice, because a peer can only ever be sent through a label
    /// some node offers and the two have to agree on what one looks like.
    /// </summary>
    public static bool TryReadLabel(string? value, out string? label)
    {
        label = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (!Name.IsMatch(trimmed))
        {
            return false;
        }

        label = trimmed;
        return true;
    }

    /// <summary>The address this node offers under that label, or null when it offers none.</summary>
    public string? Resolve(string? label)
        => string.IsNullOrWhiteSpace(label)
            ? null
            : Labels.FirstOrDefault(entry => string.Equals(entry.Label, label.Trim(), StringComparison.OrdinalIgnoreCase))?.Address;

    /// <summary>
    /// Every part of a plan decides what some node runs - the default travels in the bundle, and
    /// a label decides which address a peer's rule names - so any difference has to move the
    /// revision. A bundle is served against an ETag of the revision, and an unchanged one is
    /// answered with a 304: the node this is about would never hear.
    /// </summary>
    public bool ChangesBundle(NodeEgressPlan other)
        => !string.Equals(DefaultAddress, other.DefaultAddress, StringComparison.Ordinal)
            || !Labels.Select(Describe).SequenceEqual(other.Labels.Select(Describe), StringComparer.Ordinal);

    public static NodeEgressPlan Read(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? None
            : JsonSerializer.Deserialize(json, ControlJsonContext.Default.NodeEgressPlan) ?? None;

    /// <summary>Null for an empty plan, so a node that has none has none in the database either.</summary>
    public string? Serialize()
        => IsEmpty ? null : JsonSerializer.Serialize(this, ControlJsonContext.Default.NodeEgressPlan);

    /// <summary>
    /// Reads what a request carried. Blank is not a value: clearing the label on a row means that
    /// address has no label, and clearing the default means the kernel picks again.
    ///
    /// What cannot be checked here is whether the node holds the address - only the node knows,
    /// which is why the panel offers what was reported. An address that is merely not in the last
    /// report is still accepted: a node that has not reported yet must not make a plan
    /// unexpressible, and the agent skips a rule it cannot honour rather than installing it.
    /// </summary>
    public static bool TryRead(string? defaultAddress, EgressLabel[]? labels, out NodeEgressPlan plan, out ApiError error)
    {
        plan = None;
        error = default!;

        string? fallback = null;
        if (!string.IsNullOrWhiteSpace(defaultAddress) && !EgressAddress.TryParse(defaultAddress, out fallback))
        {
            error = Invalid(defaultAddress);
            return false;
        }

        var entries = new List<EgressLabel>();
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in labels ?? [])
        {
            // A row with no name is an address the operator cleared, and nobody can ask for it.
            if (entry is null || string.IsNullOrWhiteSpace(entry.Label))
            {
                continue;
            }

            if (!EgressAddress.TryParse(entry.Address, out var address))
            {
                error = Invalid(entry.Address);
                return false;
            }

            if (!TryReadLabel(entry.Label, out var name) || name is null)
            {
                error = new ApiError("node_egress_label_invalid", LabelRule);
                return false;
            }

            if (!addresses.Add(address) || !names.Add(name))
            {
                error = new ApiError(
                    "node_egress_duplicate",
                    "Each address and each label can appear only once on a node: a label has to name one address for a peer to be sent through it.");

                return false;
            }

            entries.Add(new EgressLabel(address, name));
        }

        plan = new NodeEgressPlan(fallback, [.. entries]);
        return true;
    }

    private static ApiError Invalid(string? address)
        => new(
            "node_egress_address_invalid",
            $"{address} is not usable as a source address. It has to be an IPv4 address the node carries.");

    private static string Describe(EgressLabel entry) => $"{entry.Address}={entry.Label}";
}

/// <summary>
/// The addresses a node reported, as its column holds them.
///
/// Read rather than re-derived: these are the node's own observation, and the panel has no other
/// source for them. An empty list means the panel has not heard a report that carried any, which
/// is why the egress dialog has nothing to offer until an agent new enough to send them has
/// polled at least once.
/// </summary>
public static class ReportedEgress
{
    public static ReportedEgressAddress[] Read(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize(json, ControlJsonContext.Default.ReportedEgressAddressArray) ?? [];
}
