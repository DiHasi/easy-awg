using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// Fleet-wide AmneziaWG obfuscation profile. S1-S4/H1-H4 are applied to the node interface;
/// the Default* values seed client configs that do not override them.
/// </summary>
public sealed record ServerObfuscationProfile
{
    public int? S1 { get; init; }
    public int? S2 { get; init; }
    public int? S3 { get; init; }
    public int? S4 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H1 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H2 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H3 { get; init; }

    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? H4 { get; init; }

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
            DefaultJc = DefaultJc,
            DefaultJmin = DefaultJmin,
            DefaultJmax = DefaultJmax,
            DefaultI1 = NormalizeString(DefaultI1),
            DefaultI2 = NormalizeString(DefaultI2),
            DefaultI3 = NormalizeString(DefaultI3),
            DefaultI4 = NormalizeString(DefaultI4),
            DefaultI5 = NormalizeString(DefaultI5)
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
            I5 = DefaultI5
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
            I5 = overrides?.I5 ?? DefaultI5
        };

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
