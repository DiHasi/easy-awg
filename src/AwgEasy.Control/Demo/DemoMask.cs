using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// What each real value is shown as, for the fleet as it currently stands.
/// </summary>
/// <param name="Exact">
/// Every value the panel knows about, mapped to its stand-in. Built from the tables rather than
/// derived per field, which is what makes one peer read as one person wherever it appears -
/// including inside a sentence the panel wrote about it.
/// </param>
/// <param name="Substrings">
/// The subset of those values worth looking for inside free text, longest first so a name that
/// contains another is replaced whole. Short values are left out: a peer called "a" would
/// otherwise be substituted inside every other word in the log.
/// </param>
/// <param name="FleetSubnet">The real subnet, so an address inside it can keep its host bits.</param>
public sealed record DemoFleetMask(
    FrozenDictionary<string, string> Exact,
    string[] Substrings,
    Ipv4Network? FleetSubnet,
    Ipv4Network DemoSubnet,
    string DemoSubnetCidr);

/// <summary>
/// Rewrites what a demo session is served so that nothing in it belongs to the real fleet, while
/// everything about the fleet's shape, state and history survives: the same number of peers, on
/// the same nodes, with the same traffic, the same health and the same arrangement.
///
/// Two layers, and the order matters.
///
/// The first is a map built from the panel's own tables - every client, group, node, probe, panel
/// and operator name, every address, every public key, the subnet and the endpoint host. It is
/// applied to every string in a response regardless of which field it sits in, so a field added
/// to the API later is masked without anybody having to remember this file existed. That is the
/// property the panel's other one-place rules have, and it is the only reason a redaction layer
/// over forty endpoints is defensible at all.
///
/// The second is a table of field names, for the values the map cannot know: whatever a resolver
/// happened to answer, a key the panel was handed and does not store, an error string from a
/// node. A field in that table is replaced whether or not its value was recognized.
///
/// What it deliberately does not touch: counts, bytes, timestamps, states, revisions, generations
/// and ids. Those are what a demo is for. Ids in particular stay real - they name nothing and the
/// pages need them to work - which is also why a demo session must not be able to write: an id is
/// a handle onto a real peer even when its name is somebody else's.
/// </summary>
public sealed class DemoMask(
    DemoPseudonyms pseudonyms,
    ControlOptions options,
    FleetRepository fleet,
    ClientRepository clients,
    ClientGroupRepository groups,
    NodeRepository nodes,
    ProbeRepository probes,
    PanelRepository panels,
    AdminAccounts accounts)
{
    /// <summary>
    /// How long a built map is reused. The map is a handful of small queries, but it is wanted on
    /// every request of a page that polls, so it is held briefly. Staleness is harmless in the one
    /// direction that matters: a peer created in the last few seconds is not in the map, and the
    /// field table masks its name anyway - it just gets a stand-in drawn from the name rather than
    /// the one the map would have agreed on, until the next build.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private DemoFleetMask? _mask;
    private DateTimeOffset _builtAt;

    /// <summary>What each field name is replaced with when the map does not recognize its value.</summary>
    private static readonly FrozenDictionary<string, DemoFieldKind> Fields =
        new Dictionary<string, DemoFieldKind>(StringComparer.OrdinalIgnoreCase)
        {
            // Names. Every one of these is in the map already; the kind is what an unrecognized
            // value falls back to, which is a peer or a node that was created seconds ago.
            ["name"] = DemoFieldKind.Person,
            ["clientName"] = DemoFieldKind.Person,
            ["nodeName"] = DemoFieldKind.NodeName,
            ["activeNodeName"] = DemoFieldKind.NodeName,
            ["actor"] = DemoFieldKind.Username,

            // Where things are.
            ["hostname"] = DemoFieldKind.Host,
            ["endpointHost"] = DemoFieldKind.Host,
            ["recordName"] = DemoFieldKind.Host,
            ["url"] = DemoFieldKind.Url,
            ["address"] = DemoFieldKind.Address,
            ["publicIp"] = DemoFieldKind.Address,
            ["targetAddress"] = DemoFieldKind.Address,
            ["resolvedAddresses"] = DemoFieldKind.Address,
            ["defaultAddress"] = DemoFieldKind.Address,
            // What an echo service answered for one of a node's own addresses. Not in the map:
            // the panel does not hold it, the outside does.
            ["observed"] = DemoFieldKind.Address,
            // In the map for every label some node offers. The fallback is for a peer pinned to
            // a label no node offers yet, which the panel accepts on purpose.
            ["label"] = DemoFieldKind.EgressLabel,
            ["egressLabel"] = DemoFieldKind.EgressLabel,
            ["subnet"] = DemoFieldKind.Subnet,

            // Key material. A demo never sees a private key - none is in any admin response - but
            // a public key identifies a peer across a fleet just as well as a name does.
            ["publicKey"] = DemoFieldKind.Key,
            ["serverPublicKey"] = DemoFieldKind.Key,
            ["headerProtectionKey"] = DemoFieldKind.Key,
            ["key"] = DemoFieldKind.Key,
            // Only ever on the answer to a write, which a demo session is refused. Here so that
            // stays true of a write somebody adds later.
            ["token"] = DemoFieldKind.Key,
            ["installCommand"] = DemoFieldKind.Text,

            // Sentences the panel wrote, which name peers, nodes and addresses in prose. The
            // scrub is the same one every other string gets; listing them is documentation.
            ["message"] = DemoFieldKind.Text,
            ["detail"] = DemoFieldKind.Text,
            ["reason"] = DemoFieldKind.Text,
            ["healthReason"] = DemoFieldKind.Text,
            ["stateReason"] = DemoFieldKind.Text,
            ["warning"] = DemoFieldKind.Text,
            ["lastError"] = DemoFieldKind.Text,
            ["syncError"] = DemoFieldKind.Text,
            // A list of resolvers, which may carry several addresses in one string.
            ["clientDns"] = DemoFieldKind.Text
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fields that are never touched, whatever is in them.
    ///
    /// Two kinds, and both would be broken rather than masked by a substitution. The ids, because
    /// they name nobody and every page is built on them - which is the other half of why a demo
    /// session must not be able to write, since an id is a handle onto a real peer even when its
    /// name is somebody else's. And the stable vocabularies: a status, a role, a health word, an
    /// event kind. Those are part of the API, and an operator unlucky enough to be called "admin"
    /// would otherwise have "admin.login" rewritten into a code that means nothing to the page
    /// reading it.
    /// </summary>
    private static readonly FrozenSet<string> Untouched = new[]
    {
        "id", "clientId", "nodeId", "probeId", "panelId", "groupId",
        "activeNodeId", "targetNodeId", "activePanelId", "signingKeyId",
        "kind", "code", "status", "state", "role", "mode", "action", "provider", "outcome",
        "health", "healthSource", "window", "bucket", "backend", "recordType",
        "agentVersion", "version",
        // The name the visitor signed in with and reads in the header. Renaming it there would
        // read as a bug rather than as discretion.
        "username"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The must-match half of the obfuscation profile, by field. Kept apart from
    /// <see cref="Fields"/> because these are the one group whose stand-in has to stay a valid
    /// profile rather than merely an unrecognizable one.</summary>
    private static readonly FrozenDictionary<string, int> WireFormatFields =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["s1"] = 1, ["s2"] = 2, ["s3"] = 3, ["s4"] = 4,
            ["h1"] = 1, ["h2"] = 2, ["h3"] = 3, ["h4"] = 4
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Ipv4Pattern = new(
        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Four colon groups at least, so a clock time is never mistaken for an address;
    /// whatever matches is then parsed before it is replaced.</summary>
    private static readonly Regex Ipv6Pattern = new(
        @"\b[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){3,7}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A 32-byte base64 key, which is what every key in this system is.</summary>
    private static readonly Regex KeyPattern = new(
        @"\b[A-Za-z0-9+/]{43}=",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public DemoFleetMask Current
    {
        get
        {
            lock (_gate)
            {
                if (_mask is { } cached && DateTimeOffset.UtcNow - _builtAt < CacheLifetime)
                {
                    return cached;
                }

                _mask = Build();
                _builtAt = DateTimeOffset.UtcNow;
                return _mask;
            }
        }
    }

    /// <summary>
    /// Rewrites a JSON response in place. Objects and arrays are walked; a leaf is replaced only
    /// when there is something to replace, so a response that mentions nobody comes back byte for
    /// byte as it was.
    /// </summary>
    public string Json(string body)
    {
        var root = JsonNode.Parse(body);
        if (root is null)
        {
            return body;
        }

        var mask = Current;
        Walk(property: null, root, mask);
        return root.ToJsonString();
    }

    /// <summary>
    /// The config a demo session downloads, rendered from stand-in material rather than scrubbed
    /// afterwards.
    ///
    /// The one response that is not JSON, and the one place the map is applied to the inputs
    /// instead of the output. That is not a shortcut: a config is the file a visitor came to look
    /// at, so it has to be a real one in every respect except whose it is, and the way to be sure
    /// of that is to hand the renderer a peer and a fleet that are already nobody's. Nothing is
    /// stamped - rendering normally records which generation a peer was handed, and a demo
    /// visitor was handed nothing.
    /// </summary>
    public (string Config, string FileName) RenderConfig(FleetRecord current, ClientRecord client)
    {
        var mask = Current;

        var peer = client with
        {
            Name = Value(client.Name, DemoFieldKind.Person, mask),
            Address = Value(client.Address, DemoFieldKind.Address, mask),
            PrivateKey = Value(client.PrivateKey, DemoFieldKind.Key, mask),
            PublicKey = Value(client.PublicKey, DemoFieldKind.Key, mask),
            PresharedKey = Value(client.PresharedKey, DemoFieldKind.Key, mask)
        };

        var endpointHost = Value(current.EndpointHost, DemoFieldKind.Host, mask);
        var demoFleet = current with
        {
            // Every secret the record carries, whether or not this renderer writes it out. A
            // renderer that grows a line has to leak nothing by doing so.
            ServerPrivateKey = pseudonyms.Key("demo-server-private"),
            ServerPublicKey = Value(current.ServerPublicKey, DemoFieldKind.Key, mask),
            SigningPrivateKey = pseudonyms.Key("demo-signing-private"),
            SigningPublicKey = pseudonyms.Key("demo-signing-public"),
            Subnet = mask.DemoSubnetCidr,
            ClientDns = current.ClientDns is null ? null : Text(current.ClientDns, mask),
            EndpointHost = endpointHost,
            Obfuscation = Disguise(current.Obfuscation)
        };

        return (ClientConfigRenderer.Render(demoFleet, peer, endpointHost), ClientConfigRenderer.FileName(peer.Name));
    }

    /// <summary>
    /// The fleet's wire format, replaced where it is set and left unset where it is not.
    ///
    /// Only the must-match half: S1-S4, H1-H4 and the header protection key are what makes this
    /// fleet's packets recognizable as this fleet's, which is why a rotation draws them afresh and
    /// why publishing them to whoever is looking at the demo would hand a censor the one thing
    /// they are short of. The may-differ half is left alone - junk counts, padding and timing
    /// ranges are per-peer by design, so they describe nobody - and leaving it alone is what keeps
    /// the profile a visitor reads recognizably the operator's own tuning.
    /// </summary>
    public ServerObfuscationProfile? Disguise(ServerObfuscationProfile? profile)
        => profile is null
            ? null
            : profile with
            {
                S1 = profile.S1 is null ? null : pseudonyms.Padding(1),
                S2 = profile.S2 is null ? null : pseudonyms.Padding(2),
                S3 = profile.S3 is null ? null : pseudonyms.Padding(3),
                S4 = profile.S4 is null ? null : pseudonyms.Padding(4),
                H1 = Header(profile.H1, 1),
                H2 = Header(profile.H2, 2),
                H3 = Header(profile.H3, 3),
                H4 = Header(profile.H4, 4),
                HeaderProtectionKey = profile.HeaderProtectionKey is null ? null : pseudonyms.HeaderProtectionKey
            };

    private string? Header(string? value, int index)
        => value is null ? null : pseudonyms.Header(index, value.Contains('-', StringComparison.Ordinal));

    private void Walk(string? property, JsonNode node, DemoFleetMask mask)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(pair => pair.Key).ToArray())
                {
                    Replace(obj, name, name, mask);
                }

                break;

            // An array's elements are the property the array itself sits under - a list of
            // addresses is a list of addresses, not a list of nameless strings.
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    Replace(array, index, property, mask);
                }

                break;
        }
    }

    private void Replace(JsonObject obj, string slot, string? property, DemoFleetMask mask)
    {
        var child = obj[slot];
        if (child is JsonObject or JsonArray)
        {
            Walk(property, child, mask);
            return;
        }

        if (Leaf(property, child, mask) is { } replacement)
        {
            obj[slot] = replacement;
        }
    }

    private void Replace(JsonArray array, int slot, string? property, DemoFleetMask mask)
    {
        var child = array[slot];
        if (child is JsonObject or JsonArray)
        {
            Walk(property, child, mask);
            return;
        }

        if (Leaf(property, child, mask) is { } replacement)
        {
            array[slot] = replacement;
        }
    }

    /// <summary>What one leaf becomes, or null when it is to be left exactly as it was.</summary>
    private JsonNode? Leaf(string? property, JsonNode? node, DemoFleetMask mask)
    {
        if (node is null || (property is not null && Untouched.Contains(property)))
        {
            return null;
        }

        // The wire format first: these are the only fields whose stand-in is a number as often as
        // a string, and the JSON type they arrive in has to survive - a single message type where
        // the fleet carries one, a range where it carries a range.
        if (property is not null && WireFormatFields.TryGetValue(property, out var index))
        {
            return WireFormat(property, index, node);
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var value = node.GetValue<string>();
        if (value.Length == 0)
        {
            return null;
        }

        // The map, which is the layer that keeps a peer the same person everywhere.
        if (mask.Exact.TryGetValue(value, out var known))
        {
            return JsonValue.Create(known);
        }

        // Then the field table, for a value the panel does not hold - or holds but created in the
        // last few seconds, before this map was built.
        if (property is not null && Fields.TryGetValue(property, out var kind) && kind != DemoFieldKind.Text)
        {
            return JsonValue.Create(Value(value, kind, mask));
        }

        // Everything else, including every field nobody has listed here. Text that mentions
        // nothing comes back unchanged and is left alone.
        var scrubbed = Text(value, mask);
        return string.Equals(scrubbed, value, StringComparison.Ordinal) ? null : JsonValue.Create(scrubbed);
    }

    private JsonNode? WireFormat(string property, int index, JsonNode node)
    {
        var kind = node.GetValueKind();
        if (property.StartsWith('s') || property.StartsWith('S'))
        {
            return kind == JsonValueKind.Number ? JsonValue.Create(pseudonyms.Padding(index)) : null;
        }

        return kind switch
        {
            JsonValueKind.Number => JsonValue.Create(int.Parse(
                pseudonyms.Header(index, asRange: false),
                CultureInfo.InvariantCulture)),
            JsonValueKind.String => JsonValue.Create(
                pseudonyms.Header(index, node.GetValue<string>().Contains('-', StringComparison.Ordinal))),
            _ => null
        };
    }

    /// <summary>One value, as the given kind of thing.</summary>
    private string Value(string value, DemoFieldKind kind, DemoFleetMask mask)
        => mask.Exact.TryGetValue(value, out var known)
            ? known
            : kind switch
            {
                DemoFieldKind.Person => pseudonyms.Person(value),
                DemoFieldKind.NodeName => pseudonyms.NodeName(value),
                DemoFieldKind.EgressLabel => pseudonyms.EgressLabel(value),
                DemoFieldKind.Username => pseudonyms.Username(value),
                DemoFieldKind.Host => pseudonyms.Host(value),
                DemoFieldKind.Url => pseudonyms.Url(value),
                DemoFieldKind.Key => pseudonyms.Key(value),
                DemoFieldKind.Subnet => mask.DemoSubnetCidr,
                DemoFieldKind.Address => pseudonyms.Address(value, mask.FleetSubnet, mask.DemoSubnet),
                _ => Text(value, mask)
            };

    /// <summary>
    /// A sentence the panel wrote, with everything it names taken out of it.
    ///
    /// Addresses and keys are swept first, by the same derivation the map uses, so what comes out
    /// agrees with the peer list. Names go last for exactly that reason: a stand-in address looks
    /// like an address, and sweeping after substituting would map the stand-ins a second time and
    /// have the log disagree with the page it describes.
    /// </summary>
    private string Text(string value, DemoFleetMask mask)
    {
        value = Ipv4Pattern.Replace(value, match => Swept(match.Value, AddressFamily.InterNetwork, mask));
        value = Ipv6Pattern.Replace(value, match => Swept(match.Value, AddressFamily.InterNetworkV6, mask));

        value = KeyPattern.Replace(value, match => pseudonyms.Key(match.Value));

        return Mentions(value, mask) ? Substitute(value, mask) : value;
    }

    private static bool Mentions(string value, DemoFleetMask mask)
        => mask.Substrings.Any(real => value.Contains(real, StringComparison.Ordinal));

    /// <summary>
    /// Replaces every name mentioned, in one pass.
    ///
    /// One pass rather than a replacement per name, because a stand-in is a plausible name and a
    /// second replacement could land inside one this method had just written - a peer called
    /// "Willow" would rewrite the "Willow" in the stand-in of the peer substituted before it. At
    /// each position the longest name that fits wins, which is also what keeps a name that
    /// contains another from being replaced in halves.
    /// </summary>
    private static string Substitute(string value, DemoFleetMask mask)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var match = mask.Substrings.FirstOrDefault(real =>
                index + real.Length <= value.Length
                && string.CompareOrdinal(value, index, real, 0, real.Length) == 0);

            if (match is null)
            {
                builder.Append(value[index]);
                index++;
                continue;
            }

            builder.Append(mask.Exact[match]);
            index += match.Length;
        }

        return builder.ToString();
    }

    /// <summary>What one address found inside a sentence becomes. Anything that does not parse as
    /// the family the pattern was looking for is left exactly as it was: the patterns are
    /// deliberately loose, and a version number is not an address.</summary>
    private string Swept(string candidate, AddressFamily family, DemoFleetMask mask)
        => IPAddress.TryParse(candidate, out var address) && address.AddressFamily == family
            ? pseudonyms.Address(candidate, mask.FleetSubnet, mask.DemoSubnet)
            : candidate;

    private DemoFleetMask Build()
    {
        var current = fleet.Find();
        var fleetSubnet = current is not null && Ipv4Network.TryParse(current.Subnet, out var parsed, out _)
            ? parsed
            : null;

        var demoSubnet = pseudonyms.Subnet(fleetSubnet ?? Ipv4Network.Parse("10.0.0.0/24"));
        var demoSubnetCidr = demoSubnet.GetAddress(0)
            + "/"
            + demoSubnet.PrefixLength.ToString(CultureInfo.InvariantCulture);

        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? real, Func<int, string> draw)
        {
            if (string.IsNullOrWhiteSpace(real) || exact.ContainsKey(real))
            {
                return;
            }

            // Two real values can derive onto one stand-in, and two peers showing as one person
            // is the one way this could mislead rather than merely conceal. The draw is salted
            // until it lands somewhere free; the ceiling is there so a pathological fleet cannot
            // spin here, and a repeat at that point is cosmetic.
            for (var attempt = 0; ; attempt++)
            {
                var candidate = draw(attempt);
                if (taken.Add(candidate) || attempt >= 64)
                {
                    exact[real] = candidate;
                    return;
                }
            }
        }

        if (current is not null)
        {
            Add(current.ServerPublicKey, _ => pseudonyms.Key(current.ServerPublicKey));
            Add(current.Subnet, _ => demoSubnetCidr);
            Add(current.EndpointHost, attempt => pseudonyms.Host(current.EndpointHost, attempt));
            // Not a key, but it names one: the log says which signing key a fleet was created
            // with, and that is a fingerprint of this fleet like any other.
            Add(current.SigningKeyId, _ => pseudonyms.Identifier(current.SigningKeyId, current.SigningKeyId.Length));
        }

        // Every peer, probes included: a probe tunnels with a client row like anybody's, and its
        // address and key reach the panel's own pages through the probe list.
        foreach (var client in clients.ListAll())
        {
            Add(client.Name, attempt => pseudonyms.Person(client.Name, attempt));
            Add(client.Address, _ => pseudonyms.Address(client.Address, fleetSubnet, demoSubnet));
            Add(client.PublicKey, _ => pseudonyms.Key(client.PublicKey));
            // Neither reaches an admin response today. They are here as the backstop for one that
            // starts to: a key in the map is masked wherever it turns up.
            Add(client.PrivateKey, _ => pseudonyms.Key(client.PrivateKey));
            Add(client.PresharedKey, _ => pseudonyms.Key(client.PresharedKey));
            // A peer may name a label no node offers, which the panel accepts on purpose, so the
            // node loop below would not have it.
            Add(client.EgressLabel, attempt => pseudonyms.EgressLabel(client.EgressLabel!, attempt));
        }

        foreach (var group in groups.List())
        {
            Add(group.Name, attempt => pseudonyms.Person(group.Name, attempt));
        }

        foreach (var node in nodes.List())
        {
            Add(node.Name, attempt => pseudonyms.NodeName(node.Name, attempt));
            Add(node.Hostname, attempt => pseudonyms.Host(node.Hostname!, attempt));
            Add(node.EndpointHost, attempt => pseudonyms.Host(node.EndpointHost!, attempt));
            Add(node.PublicIp, _ => pseudonyms.Address(node.PublicIp!, fleetSubnet, demoSubnet));
            Add(node.AgentPublicKey, _ => pseudonyms.Key(node.AgentPublicKey));

            // Both halves of a node's egress. The addresses are a fleet's addresses like any
            // other - publishing the spare ones is publishing where else this fleet can be
            // reached - and the labels are the operator's own words.
            var plan = NodeEgressPlan.Read(node.EgressPlan);
            Add(plan.DefaultAddress, _ => pseudonyms.Address(plan.DefaultAddress!, fleetSubnet, demoSubnet));

            foreach (var entry in plan.Labels)
            {
                Add(entry.Address, _ => pseudonyms.Address(entry.Address, fleetSubnet, demoSubnet));
                Add(entry.Label, attempt => pseudonyms.EgressLabel(entry.Label, attempt));
            }

            foreach (var reported in ReportedEgress.Read(node.EgressReport))
            {
                Add(reported.Address, _ => pseudonyms.Address(reported.Address, fleetSubnet, demoSubnet));
                Add(reported.Observed, _ => pseudonyms.Address(reported.Observed!, fleetSubnet, demoSubnet));
            }
        }

        foreach (var probe in probes.List())
        {
            Add(probe.Name, attempt => pseudonyms.ProbeName(probe.Name, attempt));
            Add(probe.Hostname, attempt => pseudonyms.Host(probe.Hostname!, attempt));
            Add(probe.AgentPublicKey, _ => pseudonyms.Key(probe.AgentPublicKey));
        }

        foreach (var panel in panels.List())
        {
            Add(panel.Name, attempt => pseudonyms.PanelName(panel.Name, attempt));
            Add(panel.Hostname, attempt => pseudonyms.Host(panel.Hostname!, attempt));
            Add(panel.Url, attempt => pseudonyms.Url(panel.Url!, attempt));
            Add(panel.PublicIp, _ => pseudonyms.Address(panel.PublicIp!, fleetSubnet, demoSubnet));
            Add(panel.AgentPublicKey, _ => pseudonyms.Key(panel.AgentPublicKey));
        }

        // Operators, who appear beside everything they did in the event log. A demo account maps
        // to itself: it is the name the visitor signed in with and the one they read in the
        // header, so masking it would read as a bug - and mapping it rather than skipping it is
        // what keeps the log's actor column agreeing with the sentence beside it.
        foreach (var account in accounts.List())
        {
            Add(account.Username, attempt => account.IsDemo
                ? account.Username
                : pseudonyms.Username(account.Username, attempt));
        }

        // Named in the environment rather than the database, so they are not in any table above:
        // the records this panel moves, and where the pair reaches this half of itself.
        Add(options.DefaultEndpointHost, attempt => pseudonyms.Host(options.DefaultEndpointHost, attempt));
        Add(options.Dns.RecordName, attempt => pseudonyms.Host(options.Dns.RecordName!, attempt));
        Add(options.Panel.DnsRecordName, attempt => pseudonyms.Host(options.Panel.DnsRecordName!, attempt));
        Add(options.Panel.Url, attempt => pseudonyms.Url(options.Panel.Url!, attempt));
        Add(options.Panel.PublicIp, _ => pseudonyms.Address(options.Panel.PublicIp!, fleetSubnet, demoSubnet));

        var substrings = exact.Keys
            .Where(key => key.Length >= 3)
            .OrderByDescending(key => key.Length)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();

        return new DemoFleetMask(exact.ToFrozenDictionary(StringComparer.Ordinal), substrings, fleetSubnet, demoSubnet, demoSubnetCidr);
    }
}

/// <summary>What sort of thing a field holds, which is what an unrecognized value is replaced
/// with. Only ever reached for a value the map does not already have an answer for.</summary>
internal enum DemoFieldKind
{
    Person,
    NodeName,
    EgressLabel,
    Username,
    Host,
    Url,
    Address,
    Subnet,
    Key,
    Text
}
