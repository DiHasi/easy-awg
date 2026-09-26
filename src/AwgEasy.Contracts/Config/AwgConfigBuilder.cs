using System.Globalization;
using System.Text;

namespace AwgEasy.Contracts;

/// <summary>
/// Shared primitives for writing AmneziaWG config files.
///
/// The control plane renders client configs and the node renders its interface config. Both write
/// the same obfuscation keys in the same format, and both must agree: S1-S4, H1-H4,
/// HeaderProtectionKey and RandomTrailers have to match between a client and the server it talks
/// to, or the handshake never completes. Keeping the writer in one place means the two ends
/// cannot drift apart.
/// </summary>
public static class AwgConfigBuilder
{
    /// <summary>Writes a key only when it has a value, so unset settings stay out of the file.</summary>
    public static StringBuilder AppendSetting(this StringBuilder builder, string key, int? value)
        => value.HasValue
            ? builder.Append(key).Append(" = ").AppendLine(value.Value.ToString(CultureInfo.InvariantCulture))
            : builder;

    public static StringBuilder AppendSetting(this StringBuilder builder, string key, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? builder
            : builder.Append(key).Append(" = ").AppendLine(value);

    /// <summary>awg parses on/off as well as 1/0; the words are what its own documentation uses.</summary>
    public static StringBuilder AppendSetting(this StringBuilder builder, string key, bool? value)
        => value.HasValue
            ? builder.Append(key).Append(" = ").AppendLine(value.Value ? "on" : "off")
            : builder;

    /// <summary>
    /// The settings that describe the wire format. These belong in both the node interface config
    /// and every client config, and must be identical in the two: they are what the two ends use
    /// to recognize each other's packets at all.
    /// </summary>
    public static StringBuilder AppendInterfaceObfuscation(this StringBuilder builder, ServerObfuscationProfile? obfuscation)
    {
        if (obfuscation is null)
        {
            return builder;
        }

        return builder
            .AppendSetting("S1", obfuscation.S1)
            .AppendSetting("S2", obfuscation.S2)
            .AppendSetting("S3", obfuscation.S3)
            .AppendSetting("S4", obfuscation.S4)
            .AppendSetting("H1", obfuscation.H1)
            .AppendSetting("H2", obfuscation.H2)
            .AppendSetting("H3", obfuscation.H3)
            .AppendSetting("H4", obfuscation.H4)
            .AppendSetting("HeaderProtectionKey", obfuscation.HeaderProtectionKey)
            .AppendSetting("RandomTrailers", obfuscation.RandomTrailers);
    }

    /// <summary>
    /// AmneziaWG 3.x timing and padding knobs. Both ends apply these, but unlike the settings
    /// above they need not agree: each peer only randomizes its own behaviour. The node gets the
    /// fleet defaults, a client gets its own merged values.
    /// </summary>
    public static StringBuilder AppendTuning(this StringBuilder builder, ClientObfuscationOverrides? obfuscation)
    {
        if (obfuscation is null)
        {
            return builder;
        }

        return builder
            .AppendSetting("ContentPaddingAddition", obfuscation.ContentPaddingAddition)
            .AppendSetting("RekeyAfterTime", obfuscation.RekeyAfterTime)
            .AppendSetting("RekeyTimeout", obfuscation.RekeyTimeout)
            .AppendSetting("RejectAfterTime", obfuscation.RejectAfterTime)
            .AppendSetting("KeepaliveTimeout", obfuscation.KeepaliveTimeout)
            .AppendSetting("MaxHandshakeAttempts", obfuscation.MaxHandshakeAttempts)
            .AppendSetting("DisableCookies", obfuscation.DisableCookies);
    }

    /// <summary>
    /// Junk packets and the pre-handshake obfuscation packets. These go only into client configs:
    /// they describe what the initiator sends, and the node tolerates them without needing its
    /// own copy. Leaving them out keeps the node config to what it actually needs.
    /// </summary>
    public static StringBuilder AppendClientObfuscation(this StringBuilder builder, ClientObfuscationOverrides? obfuscation)
    {
        if (obfuscation is null)
        {
            return builder;
        }

        return builder
            .AppendSetting("Jc", obfuscation.Jc)
            .AppendSetting("Jmin", obfuscation.Jmin)
            .AppendSetting("Jmax", obfuscation.Jmax)
            .AppendSetting("I1", obfuscation.I1)
            .AppendSetting("I2", obfuscation.I2)
            .AppendSetting("I3", obfuscation.I3)
            .AppendSetting("I4", obfuscation.I4)
            .AppendSetting("I5", obfuscation.I5);
    }
}
