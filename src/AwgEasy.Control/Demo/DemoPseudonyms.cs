using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// What a demo session is handed instead of the fleet's own names, addresses and keys.
///
/// Every stand-in is derived from the real value through an HMAC over a key drawn once per
/// process, which buys the two things a demo needs. The same peer is the same person on every
/// page, in every graph and in the event log, so what a visitor reads is one fleet rather than a
/// reshuffle of one - and the derivation cannot be run backwards by whoever is looking at it,
/// which a lookup table shipped with the panel or a plain hash of the value could be. The key is
/// drawn per process rather than stored: nothing needs a pseudonym to outlive a restart, and a
/// secret that is never written down cannot be read off a disk.
///
/// Addresses and hostnames land in the ranges reserved for documentation - RFC 5737 for IPv4,
/// RFC 3849 for IPv6, <c>example.net</c> for names. A plausible-looking address drawn at random
/// belongs to somebody, and a demo that invites a visitor to scan it is a demo that points them
/// at a stranger.
/// </summary>
public sealed class DemoPseudonyms
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Stand-in people. Deliberately not names anybody has: a demo that reads as a list
    /// of plausible customers is one a visitor may take for the real list.</summary>
    private static readonly string[] GivenNames =
    [
        "Aurora", "Basil", "Cedar", "Dahlia", "Ember", "Flint", "Garnet", "Hazel", "Indigo",
        "Juniper", "Kestrel", "Linden", "Marlow", "Nimbus", "Onyx", "Poppy", "Quill", "Rowan",
        "Sable", "Tamsin", "Umber", "Verity", "Willow", "Yarrow", "Zephyr"
    ];

    private static readonly string[] FamilyNames =
    [
        "Ashford", "Bellamy", "Calloway", "Draper", "Ellsworth", "Fairbairn", "Glenmore",
        "Hollis", "Ironside", "Jessop", "Kingsley", "Lockwood", "Merritt", "Nightingale",
        "Osgood", "Pembroke", "Quimby", "Ravenhill", "Sterling", "Thorne", "Underhill",
        "Vexley", "Whitlock", "Yardley", "Zelenko"
    ];

    /// <summary>Where a node, a probe or a panel pretends to be. Airport-style codes because that
    /// is what an operator actually calls them, so the demo's node list reads like one.</summary>
    private static readonly string[] Places =
    [
        "ams", "fra", "waw", "hel", "sto", "lhr", "cdg", "mxp", "vie", "prg", "otp", "tll",
        "rix", "vno", "zrh", "mad", "lis", "dub", "osl", "cph", "kef", "sof", "beg", "zag", "lju"
    ];

    private static readonly string[] Labels =
    [
        "alder", "birch", "cedar", "dogwood", "elm", "fir", "gingko", "hawthorn", "ironwood",
        "juniper", "kapok", "larch", "maple", "nutmeg", "oak", "pine", "quince", "redwood",
        "spruce", "teak", "umbra", "vine", "walnut", "yew", "zelkova"
    ];

    /// <summary>The documentation ranges every unmapped address lands in.</summary>
    private const string Ipv4DocumentationPrefix = "203.0.113.";

    private const string Ipv6DocumentationPrefix = "2001:db8:";

    private const string DocumentationDomain = "example.net";

    // The demo's own wire format, drawn once so the obfuscation profile a visitor reads is a
    // single coherent one rather than a fresh draw per request. Deliberately not the fleet's: S1-S4,
    // H1-H4 and the header protection key are what make this fleet's traffic recognizable as this
    // fleet's, which is exactly what a rotation replaces and exactly what a demo must not publish.
    private readonly int[] _paddings;
    private readonly uint[] _headerLows;

    /// <summary>Padding bounds matching <see cref="ServerObfuscationGenerator"/>'s, for the same
    /// reason: at least the header-protection nonce size, and small enough to be a plausible
    /// profile rather than an obviously invented one.</summary>
    private const int MinPadding = 16;

    private const int MaxPadding = 64;

    /// <summary>How many message types one header picks from, and how far apart the four windows
    /// sit. The stride is far wider than the width so the windows cannot overlap however the
    /// derivation falls, which is the one thing amneziawg will not accept about H1-H4.</summary>
    private const uint HeaderWidth = 512;

    private const uint HeaderStride = 1u << 16;

    /// <summary>WireGuard owns message types 1-4; anything else has to clear them.</summary>
    private const uint FirstUsableHeader = 5;

    public DemoPseudonyms()
    {
        _paddings =
        [
            Padding("s1"),
            DrawResponsePadding(Padding("s1")),
            Padding("s3"),
            Padding("s4")
        ];

        var ceiling = (ulong)int.MaxValue - (3 * HeaderStride) - HeaderWidth - FirstUsableHeader;
        var start = FirstUsableHeader + (uint)(Draw("headers", string.Empty) % ceiling);
        _headerLows = [start, start + HeaderStride, start + (2 * HeaderStride), start + (3 * HeaderStride)];

        HeaderProtectionKey = Key("demo-header-protection");
        return;

        int Padding(string scope) => MinPadding + (int)(Draw(scope, string.Empty) % (MaxPadding - MinPadding));
    }

    /// <summary>A person, holding however many devices. Also what a group - which is a person -
    /// and an unrecognized name become.</summary>
    public string Person(string value, int attempt = 0)
    {
        var draw = Draw("person", value, attempt);
        return GivenNames[(int)(draw % (ulong)GivenNames.Length)]
            + " "
            + FamilyNames[(int)(draw / (ulong)GivenNames.Length % (ulong)FamilyNames.Length)];
    }

    public string NodeName(string value, int attempt = 0)
    {
        var draw = Draw("node", value, attempt);
        return Places[(int)(draw % (ulong)Places.Length)]
            + "-"
            + (1 + draw / (ulong)Places.Length % 4).ToString(CultureInfo.InvariantCulture);
    }

    public string ProbeName(string value, int attempt = 0)
        => Places[(int)(Draw("probe", value, attempt) % (ulong)Places.Length)] + "-probe";

    /// <summary>
    /// An operator's own word for a group of egress addresses. Masked like a name rather than
    /// passed through: an operator picks these words, and they are as likely to name the person
    /// who needs that address, or the provider it came from, as anything else.
    /// </summary>
    public string EgressLabel(string value, int attempt = 0)
        => "exit-" + (1 + Draw("egress", value, attempt) % 9).ToString(CultureInfo.InvariantCulture);

    public string PanelName(string value, int attempt = 0)
        => Places[(int)(Draw("panel", value, attempt) % (ulong)Places.Length)] + "-panel";

    /// <summary>An operator's sign-in name, as it appears beside an entry in the event log.</summary>
    public string Username(string value, int attempt = 0)
        => "operator" + (1 + Draw("username", value, attempt) % 9).ToString(CultureInfo.InvariantCulture);

    /// <summary>A hostname, an endpoint host or a DNS record name, under the domain reserved for
    /// documentation.</summary>
    public string Host(string value, int attempt = 0)
    {
        var draw = Draw("host", value, attempt);
        return Labels[(int)(draw % (ulong)Labels.Length)]
            + (1 + draw / (ulong)Labels.Length % 9).ToString(CultureInfo.InvariantCulture)
            + "."
            + DocumentationDomain;
    }

    /// <summary>A URL one panel reaches another at. Only the host is invented - the scheme and
    /// the port say something about how the pair is deployed, and neither names anybody.</summary>
    public string Url(string value, int attempt = 0)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "https://" + Host(value, attempt);
        }

        var builder = new UriBuilder(uri) { Host = Host(value, attempt) };
        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>
    /// A structurally valid 32-byte key. Structurally valid rather than a row of asterisks because
    /// the config a demo visitor downloads has to be the shape of a real one - it is the thing
    /// they came to look at - and a key that parses is the only way that config reads true.
    /// </summary>
    public string Key(string value) => Convert.ToBase64String(Mac("key", value, 0));

    /// <summary>
    /// A stand-in for an identifier that is not a key but names one - the signing key id the log
    /// records a fleet's creation with. Hex and the same length as the original, because it is
    /// read as an opaque handle and a shorter or stranger-looking one would read as a fault.
    /// </summary>
    public string Identifier(string value, int length)
        => Convert.ToHexStringLower(Mac("identifier", value, 0))[..Math.Min(length, 64)];

    /// <summary>Where the demo's fleet pretends to live: the operator's own prefix length, over a
    /// private network that is not theirs.</summary>
    public Ipv4Network Subnet(Ipv4Network real)
    {
        var draw = Draw("subnet", real.PrefixLength.ToString(CultureInfo.InvariantCulture));
        var second = (byte)(16 + (draw % 200));
        var third = (byte)(draw / 200 % 256);
        return Ipv4Network.Parse($"10.{second}.{third}.0/{real.PrefixLength.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// An address, mapped rather than merely replaced.
    ///
    /// One inside the fleet's own subnet keeps its host bits and moves to the demo's subnet: the
    /// panel shows the peer list anyway, so which host a peer holds reveals nothing, and keeping
    /// it is what makes the address in a peer's row, in its config and in a probe result the same
    /// address. Anything else - a node's public address, whatever a resolver answered - lands in
    /// the documentation range, deterministically, so two mentions of one host agree.
    /// </summary>
    public string Address(string value, Ipv4Network? fleetSubnet, Ipv4Network demoSubnet)
    {
        // Not an address at all. The field said it was one, so something else is in it - a
        // hostname the operator typed where an address was expected, most likely - and the honest
        // answer is a stand-in for that rather than a made-up address.
        if (!IPAddress.TryParse(value, out var parsed))
        {
            return Host(value);
        }

        // Addresses that name no host: the unspecified address a default route is written with,
        // loopback, broadcast. Replacing them is not masking, it is corruption - "0.0.0.0/0" is
        // what AllowedIPs says on every config in the world, and a demo that showed a documentation
        // address there would be showing a config that routes nothing.
        if (IsAnonymous(parsed))
        {
            return value;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var draw = Draw("ipv6", value);
            return Ipv6DocumentationPrefix
                + (draw % 0xFFFF).ToString("x", CultureInfo.InvariantCulture)
                + ":"
                + (draw / 0xFFFF % 0xFFFF).ToString("x", CultureInfo.InvariantCulture)
                + "::1";
        }

        if (fleetSubnet is { } subnet && subnet.Contains(parsed))
        {
            var host = ToUInt(parsed) - subnet.NetworkAddress;
            return demoSubnet.GetAddress(host);
        }

        return Ipv4DocumentationPrefix + (1 + Draw("ipv4", value) % 254).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The demo's header protection key. One per process, like the rest of its wire
    /// format.</summary>
    public string HeaderProtectionKey { get; }

    public int Padding(int index) => _paddings[index - 1];

    /// <summary>
    /// The demo's own value for one of H1-H4, keeping the fleet's shape: a single message type
    /// stays single and a range stays a range. The shape is the one thing about these values that
    /// is visible elsewhere in the panel - it is what decides whether a node needs AmneziaWG 3.x -
    /// so changing it would have the demo contradict the schema badge beside it.
    /// </summary>
    public string Header(int index, bool asRange)
    {
        var low = _headerLows[index - 1];
        return asRange
            ? low.ToString(CultureInfo.InvariantCulture) + "-" + (low + HeaderWidth - 1).ToString(CultureInfo.InvariantCulture)
            : low.ToString(CultureInfo.InvariantCulture);
    }

    private int DrawResponsePadding(int initiationPadding)
    {
        for (var attempt = 0; ; attempt++)
        {
            var padding = MinPadding + (int)(Draw("s2", string.Empty, attempt) % (MaxPadding - MinPadding));
            // Checked against the initiation padding and against no padding at all, because the
            // fleet's own profile decides which of the two pairings the demo ends up showing: a
            // fleet that sets S2 and leaves S1 unset is served this value beside nothing.
            if (AwgObfuscationValidator.HandshakeSizesDiffer(initiationPadding, padding)
                && AwgObfuscationValidator.HandshakeSizesDiffer(null, padding))
            {
                return padding;
            }
        }
    }

    private ulong Draw(string scope, string value, int attempt = 0)
        => BinaryPrimitives.ReadUInt64LittleEndian(Mac(scope, value, attempt));

    /// <summary>
    /// The one place a stand-in comes from. The scope keeps the spaces apart, so the client named
    /// "fra-1" and the node named "fra-1" are not handed the same pseudonym; the attempt is what
    /// lets a caller draw again when two real values collided onto one stand-in.
    /// </summary>
    private byte[] Mac(string scope, string value, int attempt)
        => HMACSHA256.HashData(
            _key,
            Encoding.UTF8.GetBytes($"{scope}\u0000{value}\u0000{attempt.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>Whether this address is a constant of the protocol rather than somebody's.</summary>
    public static bool IsAnonymous(IPAddress address)
        => address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || address.Equals(IPAddress.IPv6Any)
            || IPAddress.IsLoopback(address);

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
