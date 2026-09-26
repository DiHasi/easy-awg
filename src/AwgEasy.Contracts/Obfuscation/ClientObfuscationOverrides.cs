using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// The AmneziaWG settings that may legally differ between the two ends of a tunnel: junk
/// packets, the pre-handshake obfuscation packets, and the 3.x timing and padding knobs. Any
/// unset field falls back to the fleet default carried by <see cref="ServerObfuscationProfile"/>.
///
/// Everything here is safe to vary per client, which is the point - two clients of the same
/// fleet that rekey on the same schedule and pad to the same length are a correlatable pair.
/// The settings that must match instead live on the fleet profile.
/// </summary>
public sealed record ClientObfuscationOverrides
{
    public int? Jc { get; init; }
    public int? Jmin { get; init; }
    public int? Jmax { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I1 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I2 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I3 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I4 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? I5 { get; init; }

    // AmneziaWG 3.x. Each is a single number or an inclusive `lo-hi` range re-rolled on every
    // use; 0 or 0-0 means "behave like stock WireGuard".

    /// <summary>Random bytes appended to the transport payload, on top of the 16-byte multiple
    /// WireGuard already pads to.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? ContentPaddingAddition { get; init; }

    /// <summary>Seconds before a live session re-handshakes. Stock WireGuard is a fixed 120.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? RekeyAfterTime { get; init; }

    /// <summary>Seconds between handshake retransmits. Stock WireGuard is a fixed 5.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? RekeyTimeout { get; init; }

    /// <summary>Seconds after which a keypair is abandoned. Stock WireGuard is a fixed 180.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? RejectAfterTime { get; init; }

    /// <summary>Seconds of silence before a keepalive is sent. Stock WireGuard is a fixed 10.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? KeepaliveTimeout { get; init; }

    /// <summary>Handshake attempts before the peer gives up. Stock WireGuard is a fixed 18.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? MaxHandshakeAttempts { get; init; }

    /// <summary>Stops the peer answering with a cookie reply under load. The reply is a
    /// recognizable message of its own, and not sending it removes that signal.</summary>
    public bool? DisableCookies { get; init; }

    /// <summary>Goes in the client's [Peer] section rather than its interface. A range here is
    /// what stops every client of the fleet emitting keepalives on the same cadence.</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? PersistentKeepalive { get; init; }

    public ClientObfuscationOverrides Normalize()
        => new()
        {
            Jc = Jc,
            Jmin = Jmin,
            Jmax = Jmax,
            I1 = NormalizeString(I1),
            I2 = NormalizeString(I2),
            I3 = NormalizeString(I3),
            I4 = NormalizeString(I4),
            I5 = NormalizeString(I5),
            ContentPaddingAddition = NormalizeString(ContentPaddingAddition),
            RekeyAfterTime = NormalizeString(RekeyAfterTime),
            RekeyTimeout = NormalizeString(RekeyTimeout),
            RejectAfterTime = NormalizeString(RejectAfterTime),
            KeepaliveTimeout = NormalizeString(KeepaliveTimeout),
            MaxHandshakeAttempts = NormalizeString(MaxHandshakeAttempts),
            DisableCookies = DisableCookies,
            PersistentKeepalive = NormalizeString(PersistentKeepalive)
        };

    public bool IsEmpty
        => Jc is null && Jmin is null && Jmax is null
            && I1 is null && I2 is null && I3 is null && I4 is null && I5 is null
            && ContentPaddingAddition is null && RekeyAfterTime is null && RekeyTimeout is null
            && RejectAfterTime is null && KeepaliveTimeout is null && MaxHandshakeAttempts is null
            && DisableCookies is null && PersistentKeepalive is null;

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
