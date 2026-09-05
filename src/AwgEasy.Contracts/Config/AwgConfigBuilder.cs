using System.Globalization;
using System.Text;

namespace AwgEasy.Contracts;

/// <summary>
/// Shared primitives for writing AmneziaWG config files.
///
/// The control plane renders client configs and the node renders its interface config. Both write
/// the same obfuscation keys in the same format, and both must agree: S1-S4 and H1-H4 have to
/// match between a client and the server it talks to, or the handshake never completes. Keeping
/// the writer in one place means the two ends cannot drift apart.
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

    /// <summary>
    /// Interface-side obfuscation. These belong in both the node interface config and every
    /// client config: they describe the wire format the two ends share.
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
            .AppendSetting("H4", obfuscation.H4);
    }

    /// <summary>
    /// Client-side jitter and packet settings. These go only into client configs - the node has
    /// no use for them, and leaving them out keeps its config to what it actually needs.
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
