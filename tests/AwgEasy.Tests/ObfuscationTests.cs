using AwgEasy.Contracts;
using AwgEasy.Control;

namespace AwgEasy.Tests;

public class AwgRangeTests
{
    [Theory]
    [InlineData("0", 0u, 0u)]
    [InlineData("140", 140u, 140u)]
    [InlineData("120-160", 120u, 160u)]
    [InlineData(" 120-160 ", 120u, 160u)]
    public void Parses_a_number_or_an_inclusive_range(string value, uint expectedLow, uint expectedHigh)
    {
        Assert.True(AwgRange.TryParse(value, AwgRange.MaxUInt16, out var low, out var high));
        Assert.Equal(expectedLow, low);
        Assert.Equal(expectedHigh, high);
    }

    [Theory]
    [InlineData("160-120")]  // amneziawg refuses an inverted range rather than swapping it
    [InlineData("70000")]    // above uint16
    [InlineData("12-70000")]
    [InlineData("12 - 16")]  // the parser splits on '-' and accepts no whitespace
    [InlineData("0x10")]
    [InlineData("-16")]
    [InlineData("")]
    public void Rejects_what_amneziawg_would_reject(string value)
    {
        Assert.False(AwgRange.TryParse(value, AwgRange.MaxUInt16, out _, out _));
    }

    [Fact]
    public void Treats_a_zero_range_as_disabled_so_stock_wireguard_behaviour_is_expressible()
    {
        Assert.True(AwgRange.IsDisabled(null));
        Assert.True(AwgRange.IsDisabled("0"));
        Assert.True(AwgRange.IsDisabled("0-0"));
        Assert.False(AwgRange.IsDisabled("0-1"));
    }

    [Fact]
    public void Detects_overlapping_ranges()
    {
        Assert.True(AwgRange.Overlap("100-200", "200-300"));
        Assert.False(AwgRange.Overlap("100-200", "201-300"));
    }
}

public class ServerObfuscationValidationTests
{
    private static ServerObfuscationProfile Protected(ServerObfuscationProfile profile)
        => profile with
        {
            HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
            S1 = profile.S1 ?? 16,
            S2 = profile.S2 ?? 16,
            S3 = profile.S3 ?? 16,
            S4 = profile.S4 ?? 16
        };

    [Fact]
    public void Accepts_a_full_amneziawg_3_profile()
    {
        var profile = Protected(new ServerObfuscationProfile
        {
            H1 = "1000000-1000500",
            H2 = "2000000-2000500",
            H3 = "3000000-3000500",
            H4 = "4000000-4000500",
            RandomTrailers = true,
            DefaultContentPaddingAddition = "0-64",
            DefaultRekeyAfterTime = "110-130",
            DefaultRekeyTimeout = "4-7",
            DefaultRejectAfterTime = "170-190",
            DefaultKeepaliveTimeout = "8-14",
            DefaultMaxHandshakeAttempts = "16-20",
            DefaultDisableCookies = true,
            DefaultPersistentKeepalive = "20-30"
        });

        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error), error.Message);
    }

    // The header cipher nonce rides in the S1-S4 padding, so amneziawg rejects the whole device
    // configuration when any of them is shorter. Catching it here is the difference between a bad
    // form submission and every node in the fleet failing to bring its interface up.
    [Fact]
    public void Requires_padding_that_fits_the_header_cipher_nonce()
    {
        var profile = Protected(new ServerObfuscationProfile()) with { S3 = 8 };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Equal("invalid_obfuscation", error.Code);
        Assert.Contains("S3", error.Message);
    }

    [Fact]
    public void Accepts_short_padding_when_header_protection_is_off()
    {
        var profile = new ServerObfuscationProfile { S1 = 5, S2 = 5 };
        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error), error.Message);
    }

    [Fact]
    public void Rejects_a_header_protection_key_that_is_not_32_bytes()
    {
        var profile = new ServerObfuscationProfile
        {
            S1 = 16,
            S2 = 16,
            S3 = 16,
            S4 = 16,
            HeaderProtectionKey = Convert.ToBase64String(new byte[16])
        };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Contains("32-byte", error.Message);
    }

    // Overlapping magic headers leave the receiver unable to tell which message type it has.
    // Ranges made this reachable by accident, where distinct single values could not.
    [Fact]
    public void Rejects_overlapping_header_ranges()
    {
        var profile = new ServerObfuscationProfile { H1 = "1000-2000", H2 = "1500-2500" };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public void Rejects_a_header_inside_the_range_wireguard_reserves()
    {
        var profile = new ServerObfuscationProfile { H1 = "3" };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Contains("above 4", error.Message);
    }

    [Fact]
    public void Allows_a_header_left_at_its_own_wireguard_default()
    {
        var profile = new ServerObfuscationProfile { H1 = "1", H2 = "2", H3 = "3", H4 = "4" };
        Assert.True(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error), error.Message);
    }

    [Fact]
    public void Rejects_a_malformed_range()
    {
        var profile = new ServerObfuscationProfile { DefaultRekeyTimeout = "7-4" };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Contains("RekeyTimeout", error.Message);
    }

    // amneziawg accepts this pair and the node comes up looking healthy, but a session is dropped
    // before it is ever renewed. Refusing it in the panel is the only place it is cheap to find.
    [Fact]
    public void Rejects_a_reject_window_that_can_close_before_the_rekey_window_opens()
    {
        var profile = new ServerObfuscationProfile
        {
            DefaultRekeyAfterTime = "110-130",
            DefaultRejectAfterTime = "120-190"
        };

        Assert.False(AwgObfuscationValidator.TryValidateServerProfile(profile, out var error));
        Assert.Contains("RejectAfterTime", error.Message);
    }

    [Fact]
    public void Validates_the_same_ranges_on_a_per_client_override()
    {
        var overrides = new ClientObfuscationOverrides { PersistentKeepalive = "30-20" };

        Assert.False(AwgObfuscationValidator.TryValidateClientOverrides(overrides, out var error));
        Assert.Contains("PersistentKeepalive", error.Message);
    }
}

public class SchemaDowngradeTests
{
    private static readonly ServerObfuscationProfile Full = new()
    {
        S1 = 16,
        S2 = 16,
        S3 = 16,
        S4 = 16,
        H1 = "1000000-1000500",
        HeaderProtectionKey = Convert.ToBase64String(new byte[32]),
        RandomTrailers = true,
        DefaultJc = 4,
        DefaultRekeyAfterTime = "110-130",
        DefaultDisableCookies = true
    };

    [Fact]
    public void A_profile_using_3x_settings_is_reported_as_such()
    {
        Assert.True(Full.UsesSchema3Features);
        Assert.False(new ServerObfuscationProfile { S1 = 15, H1 = "1234567891", DefaultJc = 4 }.UsesSchema3Features);
    }

    // A setting turned off asks for stock WireGuard behaviour, which an older node already does.
    // Counting that as a 3.x feature would warn about nodes that are in fact serving the profile.
    [Fact]
    public void A_profile_that_only_disables_3x_settings_is_not_treated_as_3x()
    {
        var profile = new ServerObfuscationProfile
        {
            RandomTrailers = false,
            DefaultDisableCookies = false,
            DefaultContentPaddingAddition = "0",
            DefaultRekeyAfterTime = "0-0"
        };

        Assert.False(profile.UsesSchema3Features);
    }

    // A pre-3.0 node handed these keys refuses the whole config and drops its tunnel, so the
    // downgrade has to remove them rather than let the node decide what it understands.
    [Fact]
    public void Downgrading_strips_every_3x_setting()
    {
        var downgraded = Full.ToSchemaV1();

        Assert.Null(downgraded.HeaderProtectionKey);
        Assert.Null(downgraded.RandomTrailers);
        Assert.Null(downgraded.DefaultRekeyAfterTime);
        Assert.Null(downgraded.DefaultDisableCookies);
    }

    // The low bound is the value such a node was already running before the range was widened,
    // so collapsing to it keeps the fleet's magic headers unchanged for that node.
    [Fact]
    public void Downgrading_collapses_a_header_range_to_its_low_bound()
    {
        Assert.Equal("1000000", Full.ToSchemaV1().H1);
    }

    [Fact]
    public void Downgrading_keeps_everything_a_2x_node_can_still_apply()
    {
        var downgraded = Full.ToSchemaV1();

        Assert.Equal(16, downgraded.S1);
        Assert.Equal(4, downgraded.DefaultJc);
    }

    [Theory]
    [InlineData(0, DesiredStateBundle.MinimumSupportedSchemaVersion)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(99, DesiredStateBundle.CurrentSchemaVersion)]
    public void An_unreported_schema_reads_as_the_oldest_rather_than_as_zero(int reported, int expected)
    {
        Assert.Equal(expected, BundleSchema.Normalize(reported));
    }
}

public class ClientConfigRendererTests
{
    private static FleetRecord Fleet(ServerObfuscationProfile? obfuscation, int tunnelMtu = FleetService.DefaultTunnelMtu)
        => new(
            Generation: 1,
            ServerPrivateKey: "SERVER_PRIVATE",
            ServerPublicKey: "SERVER_PUBLIC",
            SigningPrivateKey: "SIGN_PRIVATE",
            SigningPublicKey: "SIGN_PUBLIC",
            SigningKeyId: "key-id",
            Subnet: "10.8.0.0/24",
            ListenPort: 51820,
            ClientAllowedIps: "0.0.0.0/0",
            ClientDns: "1.1.1.1",
            TunnelMtu: tunnelMtu,
            EndpointHost: "vpn.example.com",
            Obfuscation: obfuscation,
            Revision: 3);

    private static ClientRecord Client(ClientObfuscationOverrides? obfuscation = null)
        => new(
            Id: "client-1",
            Name: "laptop",
            Address: "10.8.0.2",
            PrivateKey: "CLIENT_PRIVATE",
            PublicKey: "CLIENT_PUBLIC",
            PresharedKey: "CLIENT_PSK",
            Enabled: true,
            Obfuscation: obfuscation,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Carries_the_shared_wire_format_and_the_fleet_tunables()
    {
        var profile = new ServerObfuscationProfile
        {
            S1 = 16,
            H1 = "1000000-1000500",
            HeaderProtectionKey = "aGVhZGVyLXByb3RlY3Rpb24ta2V5LTMyLWJ5dGVzLg==",
            RandomTrailers = true,
            DefaultJc = 4,
            DefaultRekeyAfterTime = "110-130",
            DefaultDisableCookies = true
        };

        var config = ClientConfigRenderer.Render(Fleet(profile), Client(), "vpn.example.com");

        Assert.Contains("HeaderProtectionKey = aGVhZGVyLXByb3RlY3Rpb24ta2V5LTMyLWJ5dGVzLg==", config);
        Assert.Contains("RandomTrailers = on", config);
        Assert.Contains("RekeyAfterTime = 110-130", config);
        Assert.Contains("DisableCookies = on", config);
        Assert.Contains("Jc = 4", config);
    }

    [Fact]
    public void A_per_client_override_wins_over_the_fleet_default()
    {
        var profile = new ServerObfuscationProfile { DefaultRekeyAfterTime = "110-130" };
        var client = Client(new ClientObfuscationOverrides { RekeyAfterTime = "300-360" });

        var config = ClientConfigRenderer.Render(Fleet(profile), client, "vpn.example.com");

        Assert.Contains("RekeyAfterTime = 300-360", config);
        Assert.DoesNotContain("110-130", config);
    }

    [Fact]
    public void Keeps_the_stock_keepalive_until_a_range_is_configured()
    {
        Assert.Contains(
            "PersistentKeepalive = 25",
            ClientConfigRenderer.Render(Fleet(null), Client(), "vpn.example.com"));

        Assert.Contains(
            "PersistentKeepalive = 20-30",
            ClientConfigRenderer.Render(
                Fleet(new ServerObfuscationProfile { DefaultPersistentKeepalive = "20-30" }),
                Client(),
                "vpn.example.com"));
    }

    [Fact]
    public void Carries_the_fleet_tunnel_mtu()
    {
        Assert.Contains("MTU = 1280", ClientConfigRenderer.Render(Fleet(null), Client(), "vpn.example.com"));

        Assert.Contains(
            "MTU = 1380",
            ClientConfigRenderer.Render(Fleet(null, tunnelMtu: 1380), Client(), "vpn.example.com"));
    }

    /// <summary>
    /// A probe has to speak exactly as a client does. On a wider MTU it could pass a path that
    /// fragments every real client's traffic, and the panel would call that node healthy.
    /// </summary>
    [Fact]
    public void A_probe_config_carries_the_same_mtu_a_client_gets()
    {
        var fleet = Fleet(null, tunnelMtu: 1340);

        Assert.Contains("MTU = 1340", ClientConfigRenderer.RenderProbe(fleet, Client(), "203.0.113.10"));
    }
}
