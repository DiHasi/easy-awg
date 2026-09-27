using AwgEasy.Contracts;

namespace AwgEasy.Tests;

public class TempProfileCheck
{
    [Fact]
    public void Proposed_profile_is_accepted()
    {
        var profile = new ServerObfuscationProfile
        {
            S1 = 79,
            S2 = 44,
            S3 = 34,
            S4 = 12,
            H1 = "296931085-296931340",
            H2 = "1620757348-1620757603",
            H3 = "2631639721-2631639976",
            H4 = "3041605112-3041605367",
            HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
            RandomTrailers = true,
            DefaultJc = 5,
            DefaultJmin = 48,
            DefaultJmax = 384,
            DefaultI1 = "<b 0x0001><b 0x0000><b 0x2112a442><r 12>",
            DefaultI2 = "<r 96>",
            DefaultContentPaddingAddition = "0-96",
            DefaultRekeyAfterTime = "100-140",
            DefaultRekeyTimeout = "4-9",
            DefaultRejectAfterTime = "200-260",
            DefaultKeepaliveTimeout = "7-15",
            DefaultMaxHandshakeAttempts = "12-24",
            DefaultDisableCookies = true,
            DefaultPersistentKeepalive = "18-32"
        };

        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error), error.Message);

        var bundle = new DesiredStateBundle(
            DesiredStateBundle.CurrentSchemaVersion, 1, "n", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            new FleetIdentity(1, "priv", "pub"),
            new NetworkProfile("10.8.0.0/24", 51820),
            profile.Normalize(),
            new NodeSettings("awg0", null, 1400),
            [new BundlePeer("k", "p", "10.8.0.2")]);

        Assert.True(BundleGuard.TryAccept(bundle, 0, "n", DateTimeOffset.UtcNow, out var guardError), guardError.Message);
        Assert.Equal("1", AwgRange.TryParse("0", 65535, out _, out _) ? "1" : "0");
    }
}
