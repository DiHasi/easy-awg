using System.Globalization;
using System.Security.Cryptography;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Draws a fresh fleet-wide wire format: the half of the obfuscation profile that both ends of a
/// tunnel must carry identically.
///
/// This exists for rotation. A node read off its hypervisor gives up the fleet private key and
/// every preshared key, and it gives up the wire format too - S1-S4, H1-H4 and the header
/// protection key are in the config on its disk, and together they are what makes this fleet's
/// traffic recognizable as this fleet's. Replacing the keys and leaving the format behind would
/// rotate the secrets and keep the fingerprint.
///
/// Only that half is drawn. The Default* seeds and <see cref="ServerObfuscationProfile.RandomTrailers"/>
/// are carried over untouched: junk counts, timings and padding ranges are the operator's own
/// tuning rather than key material, a boolean has no secrecy to refresh, and silently rewriting
/// decisions somebody made for a reason is not what an operator pressing "rotate" is asking for.
/// </summary>
public static class ServerObfuscationGenerator
{
    /// <summary>
    /// Junk padding for each message type. At least
    /// <see cref="AwgObfuscationValidator.HeaderProtectionNonceSize"/>, because the header cipher
    /// nonce rides in this padding.
    /// </summary>
    private const int MinPadding = 16;

    private const int MaxPadding = 64;

    /// <summary>How many message types each header picks from per packet. A range rather than a
    /// single value is the 3.x point: the type changes packet to packet.</summary>
    private const uint HeaderWidth = 512;

    /// <summary>Distance between two headers' windows. Far wider than
    /// <see cref="HeaderWidth"/>, so the four windows cannot overlap however the draw falls -
    /// which is the one thing amneziawg will not accept about H1-H4.</summary>
    private const uint HeaderStride = 1u << 16;

    /// <summary>WireGuard owns message types 1-4; anything else must clear them.</summary>
    private const int FirstUsableHeader = 5;

    public static ServerObfuscationProfile Rotate(ServerObfuscationProfile? current, IAwgKeyGenerator keys)
    {
        var headers = DrawHeaders();
        var s1 = RandomNumberGenerator.GetInt32(MinPadding, MaxPadding);

        return (current ?? new ServerObfuscationProfile()) with
        {
            S1 = s1,
            S2 = DrawResponsePadding(s1),
            S3 = RandomNumberGenerator.GetInt32(MinPadding, MaxPadding),
            S4 = RandomNumberGenerator.GetInt32(MinPadding, MaxPadding),
            H1 = headers[0],
            H2 = headers[1],
            H3 = headers[2],
            H4 = headers[3],
            // Through the same `awg genpsk` the fleet's other 32-byte secrets come from, rather
            // than a second source of key material in the panel.
            HeaderProtectionKey = keys.GenerateHeaderProtectionKey()
        };
    }

    /// <summary>
    /// The handshake response padding, never the one value that would make the padded response the
    /// same length as the padded initiation - which amneziawg refuses, taking the whole device
    /// configuration with it. The band these are drawn from is narrower than the gap, so as it
    /// stands the draw cannot land there; stated as a condition anyway, because the alternative is
    /// a correct generator resting on an arithmetic coincidence that widening the band would
    /// quietly undo. <see cref="AwgObfuscationValidator"/> enforces the same rule on the way in.
    /// </summary>
    private static int DrawResponsePadding(int initiationPadding)
    {
        int padding;
        do
        {
            padding = RandomNumberGenerator.GetInt32(MinPadding, MaxPadding);
        }
        while (!AwgObfuscationValidator.HandshakeSizesDiffer(initiationPadding, padding));

        return padding;
    }

    /// <summary>
    /// Four non-overlapping windows of the message-type space, laid out in order from one random
    /// start. Only the lower half of the 32-bit space is drawn from: that is still 2^31 places to
    /// put a fleet, and it keeps the arithmetic visibly inside <c>uint</c>.
    /// </summary>
    private static string[] DrawHeaders()
    {
        var span = (int)((3 * HeaderStride) + HeaderWidth);
        var start = (uint)RandomNumberGenerator.GetInt32(FirstUsableHeader, int.MaxValue - span);

        return
        [
            Window(start),
            Window(start + HeaderStride),
            Window(start + (2 * HeaderStride)),
            Window(start + (3 * HeaderStride))
        ];
    }

    private static string Window(uint low)
        => low.ToString(CultureInfo.InvariantCulture) + "-" + (low + HeaderWidth - 1).ToString(CultureInfo.InvariantCulture);
}
