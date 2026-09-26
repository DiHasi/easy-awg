using System.Globalization;
using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// Fleet-wide AmneziaWG obfuscation profile.
///
/// Split by what the protocol requires rather than by where the UI puts it. S1-S4, H1-H4,
/// <see cref="HeaderProtectionKey"/> and <see cref="RandomTrailers"/> describe the wire format
/// itself: both ends must agree or the handshake is never recognized. The Default* values are
/// the fleet starting point for everything that may legally differ, and a client may override
/// those individually.
/// </summary>
public sealed record ServerObfuscationProfile
{
    public int? S1 { get; init; }
    public int? S2 { get; init; }
    public int? S3 { get; init; }
    public int? S4 { get; init; }

    // 3.x turned H1-H4 into ranges: a single number still works, `lo-hi` makes the sender pick a
    // fresh message type per packet. The four ranges may not overlap.

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H1 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H2 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H3 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H4 { get; init; }

    /// <summary>
    /// AmneziaWG 3.x. Base64 32-byte ChaCha20 key that encrypts the packet header, so the
    /// message type is unreadable rather than merely renamed by H1-H4. Requires S1-S4 of at
    /// least <see cref="AwgObfuscationValidator.HeaderProtectionNonceSize"/> bytes: the cipher
    /// nonce is carried in that padding.
    /// </summary>
    public string? HeaderProtectionKey { get; init; }

    /// <summary>AmneziaWG 3.x. Appends random trailing bytes to packets. Must match on both ends.</summary>
    public bool? RandomTrailers { get; init; }

    public int? DefaultJc { get; init; }
    public int? DefaultJmin { get; init; }
    public int? DefaultJmax { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI1 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI2 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI3 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI4 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultI5 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultContentPaddingAddition { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultRekeyAfterTime { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultRekeyTimeout { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultRejectAfterTime { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultKeepaliveTimeout { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultMaxHandshakeAttempts { get; init; }

    public bool? DefaultDisableCookies { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? DefaultPersistentKeepalive { get; init; }

    /// <summary>
    /// True when the profile asks for anything a pre-3.0 AmneziaWG cannot apply. Such a node
    /// would bring up a tunnel its own clients can no longer talk to, so it is served a
    /// downgraded bundle and reported as behind instead.
    /// </summary>
    public bool UsesSchema3Features
        => HeaderProtectionKey is not null
            || RandomTrailers is true
            || DefaultDisableCookies is true
            // A setting explicitly turned off asks for stock WireGuard behaviour, which an older
            // node already does. Only a value that actually changes something counts as 3.x.
            || !AwgRange.IsDisabled(DefaultContentPaddingAddition)
            || !AwgRange.IsDisabled(DefaultRekeyAfterTime)
            || !AwgRange.IsDisabled(DefaultRekeyTimeout)
            || !AwgRange.IsDisabled(DefaultRejectAfterTime)
            || !AwgRange.IsDisabled(DefaultKeepaliveTimeout)
            || !AwgRange.IsDisabled(DefaultMaxHandshakeAttempts)
            || IsRange(H1) || IsRange(H2) || IsRange(H3) || IsRange(H4);

    public ServerObfuscationProfile Normalize()
        => new()
        {
            S1 = S1,
            S2 = S2,
            S3 = S3,
            S4 = S4,
            H1 = NormalizeString(H1),
            H2 = NormalizeString(H2),
            H3 = NormalizeString(H3),
            H4 = NormalizeString(H4),
            HeaderProtectionKey = NormalizeString(HeaderProtectionKey),
            RandomTrailers = RandomTrailers,
            DefaultJc = DefaultJc,
            DefaultJmin = DefaultJmin,
            DefaultJmax = DefaultJmax,
            DefaultI1 = NormalizeString(DefaultI1),
            DefaultI2 = NormalizeString(DefaultI2),
            DefaultI3 = NormalizeString(DefaultI3),
            DefaultI4 = NormalizeString(DefaultI4),
            DefaultI5 = NormalizeString(DefaultI5),
            DefaultContentPaddingAddition = NormalizeString(DefaultContentPaddingAddition),
            DefaultRekeyAfterTime = NormalizeString(DefaultRekeyAfterTime),
            DefaultRekeyTimeout = NormalizeString(DefaultRekeyTimeout),
            DefaultRejectAfterTime = NormalizeString(DefaultRejectAfterTime),
            DefaultKeepaliveTimeout = NormalizeString(DefaultKeepaliveTimeout),
            DefaultMaxHandshakeAttempts = NormalizeString(DefaultMaxHandshakeAttempts),
            DefaultDisableCookies = DefaultDisableCookies,
            DefaultPersistentKeepalive = NormalizeString(DefaultPersistentKeepalive)
        };

    /// <summary>
    /// Strips everything a pre-3.0 AmneziaWG cannot parse, so a node one bundle schema behind
    /// still gets a config it can apply. H1-H4 ranges collapse to their low bound, which is the
    /// single value such a node was already running before the range was widened.
    /// </summary>
    public ServerObfuscationProfile ToSchemaV1()
        => new()
        {
            S1 = S1,
            S2 = S2,
            S3 = S3,
            S4 = S4,
            H1 = LowBound(H1),
            H2 = LowBound(H2),
            H3 = LowBound(H3),
            H4 = LowBound(H4),
            DefaultJc = DefaultJc,
            DefaultJmin = DefaultJmin,
            DefaultJmax = DefaultJmax,
            DefaultI1 = DefaultI1,
            DefaultI2 = DefaultI2,
            DefaultI3 = DefaultI3,
            DefaultI4 = DefaultI4,
            DefaultI5 = DefaultI5
        };

    public ClientObfuscationOverrides GetDefaults()
        => new()
        {
            Jc = DefaultJc,
            Jmin = DefaultJmin,
            Jmax = DefaultJmax,
            I1 = DefaultI1,
            I2 = DefaultI2,
            I3 = DefaultI3,
            I4 = DefaultI4,
            I5 = DefaultI5,
            ContentPaddingAddition = DefaultContentPaddingAddition,
            RekeyAfterTime = DefaultRekeyAfterTime,
            RekeyTimeout = DefaultRekeyTimeout,
            RejectAfterTime = DefaultRejectAfterTime,
            KeepaliveTimeout = DefaultKeepaliveTimeout,
            MaxHandshakeAttempts = DefaultMaxHandshakeAttempts,
            DisableCookies = DefaultDisableCookies,
            PersistentKeepalive = DefaultPersistentKeepalive
        };

    /// <summary>Merges per-client overrides over the fleet defaults.</summary>
    public ClientObfuscationOverrides Merge(ClientObfuscationOverrides? overrides)
        => new()
        {
            Jc = overrides?.Jc ?? DefaultJc,
            Jmin = overrides?.Jmin ?? DefaultJmin,
            Jmax = overrides?.Jmax ?? DefaultJmax,
            I1 = overrides?.I1 ?? DefaultI1,
            I2 = overrides?.I2 ?? DefaultI2,
            I3 = overrides?.I3 ?? DefaultI3,
            I4 = overrides?.I4 ?? DefaultI4,
            I5 = overrides?.I5 ?? DefaultI5,
            ContentPaddingAddition = overrides?.ContentPaddingAddition ?? DefaultContentPaddingAddition,
            RekeyAfterTime = overrides?.RekeyAfterTime ?? DefaultRekeyAfterTime,
            RekeyTimeout = overrides?.RekeyTimeout ?? DefaultRekeyTimeout,
            RejectAfterTime = overrides?.RejectAfterTime ?? DefaultRejectAfterTime,
            KeepaliveTimeout = overrides?.KeepaliveTimeout ?? DefaultKeepaliveTimeout,
            MaxHandshakeAttempts = overrides?.MaxHandshakeAttempts ?? DefaultMaxHandshakeAttempts,
            DisableCookies = overrides?.DisableCookies ?? DefaultDisableCookies,
            PersistentKeepalive = overrides?.PersistentKeepalive ?? DefaultPersistentKeepalive
        };

    private static bool IsRange(string? value) => value is not null && value.Contains('-');

    private static string? LowBound(string? value)
        => AwgRange.TryParse(value, AwgRange.MaxUInt32, out var low, out _)
            ? low.ToString(CultureInfo.InvariantCulture)
            : value;

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
