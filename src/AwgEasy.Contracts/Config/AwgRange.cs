using System.Globalization;

namespace AwgEasy.Contracts;

/// <summary>
/// AmneziaWG 3.x accepts several settings as either a single number or an inclusive
/// <c>lo-hi</c> range that the implementation re-rolls on every use. Randomizing timings and
/// padding is the whole point of the 3.x line: a fixed rekey interval or a fixed padding length
/// is itself a fingerprint, which is what the 2.x profiles were finally classified on.
///
/// Values are carried as strings end to end and written into the config verbatim, so the panel
/// never has to agree with <c>awg</c> on how to format them. This only parses them for
/// validation - a range the node would reject must never reach a node.
/// </summary>
public static class AwgRange
{
    public const uint MaxUInt16 = 65535;
    public const uint MaxUInt32 = uint.MaxValue;

    /// <summary>
    /// Parses <c>"n"</c> or <c>"lo-hi"</c>. Matches the parser in amneziawg-tools: decimal only,
    /// no whitespace inside, and <c>hi</c> may not be below <c>lo</c>.
    /// </summary>
    public static bool TryParse(string? value, uint max, out uint low, out uint high)
    {
        low = 0;
        high = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var separator = text.IndexOf('-');
        if (separator < 0)
        {
            if (!TryParseNumber(text, max, out low))
            {
                return false;
            }

            high = low;
            return true;
        }

        return TryParseNumber(text[..separator], max, out low)
            && TryParseNumber(text[(separator + 1)..], max, out high)
            && high >= low;
    }

    /// <summary>True when the setting is absent or explicitly disabled (<c>0</c> or <c>0-0</c>),
    /// which is how AmneziaWG asks for stock WireGuard behaviour.</summary>
    public static bool IsDisabled(string? value)
        => string.IsNullOrWhiteSpace(value)
            || (TryParse(value, MaxUInt32, out var low, out var high) && low == 0 && high == 0);

    /// <summary>Two ranges overlap, which H1-H4 may never do: the receiver would not know which
    /// message type it is looking at.</summary>
    public static bool Overlap(string? left, string? right)
        => TryParse(left, MaxUInt32, out var leftLow, out var leftHigh)
            && TryParse(right, MaxUInt32, out var rightLow, out var rightHigh)
            && leftLow <= rightHigh
            && rightLow <= leftHigh;

    private static bool TryParseNumber(string text, uint max, out uint result)
    {
        result = 0;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > max)
        {
            return false;
        }

        result = (uint)parsed;
        return true;
    }
}
