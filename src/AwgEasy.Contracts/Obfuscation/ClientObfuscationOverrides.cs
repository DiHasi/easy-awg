using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// Per-client AmneziaWG obfuscation values. Any unset field falls back to the fleet default
/// carried by <see cref="ServerObfuscationProfile"/>.
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
            I5 = NormalizeString(I5)
        };

    public bool IsEmpty
        => Jc is null && Jmin is null && Jmax is null
            && I1 is null && I2 is null && I3 is null && I4 is null && I5 is null;

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
