namespace AwgEasy.Contracts;

/// <summary>
/// Rejects obfuscation profiles the node would refuse, before they ever reach a node.
///
/// This matters more than it looks. The node applies a bundle by handing the rendered config to
/// `awg`, and a single unacceptable value makes the whole apply fail - the tunnel does not come
/// up with the rest of the settings honoured, it does not come up at all. Every rule below
/// mirrors one that amneziawg enforces on its side.
/// </summary>
public static class AwgObfuscationValidator
{
    /// <summary>ChaCha20 nonce length. Header protection carries the nonce inside the S1-S4
    /// padding, so those paddings must leave room for it.</summary>
    public const int HeaderProtectionNonceSize = 12;

    private const int HeaderProtectionKeySize = 32;

    /// <summary>WireGuard uses message types 1-4. A magic header set to its own stock type is
    /// how amneziawg asks for that message to be left alone; anything else must clear the range.</summary>
    private const uint ReservedHeaderMax = 4;

    public static bool TryValidateServerProfile(ServerObfuscationProfile? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation is null)
        {
            return true;
        }

        if (!ValidateHeaders(obfuscation, out error))
        {
            return false;
        }

        if (!ValidateHeaderProtection(obfuscation, out error))
        {
            return false;
        }

        return ValidateTunables(obfuscation.GetDefaults(), out error);
    }

    public static bool TryValidateClientOverrides(ClientObfuscationOverrides? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        return obfuscation is null || ValidateTunables(obfuscation, out error);
    }

    private static bool ValidateTunables(ClientObfuscationOverrides obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation.Jc is < 1 or > 128)
        {
            error = new ApiError("invalid_obfuscation", "Jc must be between 1 and 128.");
            return false;
        }

        if (obfuscation.Jmin.HasValue != obfuscation.Jmax.HasValue)
        {
            error = new ApiError("invalid_obfuscation", "Jmin and Jmax must be set together.");
            return false;
        }

        if (obfuscation.Jmin.HasValue && !(obfuscation.Jmin.Value >= 0 && obfuscation.Jmin.Value < obfuscation.Jmax!.Value && obfuscation.Jmax.Value <= 1280))
        {
            error = new ApiError("invalid_obfuscation", "Jmin/Jmax must satisfy 0 <= Jmin < Jmax <= 1280.");
            return false;
        }

        // 3.x tunables: each is a single uint16 or an inclusive lo-hi range over uint16.
        (string Name, string? Value)[] ranges =
        [
            ("ContentPaddingAddition", obfuscation.ContentPaddingAddition),
            ("RekeyAfterTime", obfuscation.RekeyAfterTime),
            ("RekeyTimeout", obfuscation.RekeyTimeout),
            ("RejectAfterTime", obfuscation.RejectAfterTime),
            ("KeepaliveTimeout", obfuscation.KeepaliveTimeout),
            ("MaxHandshakeAttempts", obfuscation.MaxHandshakeAttempts),
            ("PersistentKeepalive", obfuscation.PersistentKeepalive)
        ];

        foreach (var (name, value) in ranges)
        {
            if (value is null)
            {
                continue;
            }

            if (!AwgRange.TryParse(value, AwgRange.MaxUInt16, out _, out _))
            {
                error = new ApiError(
                    "invalid_obfuscation",
                    $"{name} must be a number 0-65535 or a range like 100-140 with the low bound first.");
                return false;
            }
        }

        return ValidateTimerOrdering(obfuscation, out error);
    }

    /// <summary>
    /// A keypair is abandoned after RejectAfterTime but only renewed after RekeyAfterTime, so a
    /// reject window that can close before the rekey window opens means the tunnel drops on every
    /// key rotation. amneziawg accepts the pair and the node comes up looking healthy, which is
    /// exactly the kind of failure that is expensive to find from the panel.
    /// </summary>
    private static bool ValidateTimerOrdering(ClientObfuscationOverrides obfuscation, out ApiError error)
    {
        error = ApiError.Empty;

        if (AwgRange.IsDisabled(obfuscation.RekeyAfterTime) || AwgRange.IsDisabled(obfuscation.RejectAfterTime))
        {
            return true;
        }

        if (!AwgRange.TryParse(obfuscation.RekeyAfterTime, AwgRange.MaxUInt16, out _, out var rekeyHigh)
            || !AwgRange.TryParse(obfuscation.RejectAfterTime, AwgRange.MaxUInt16, out var rejectLow, out _))
        {
            return true;
        }

        if (rejectLow <= rekeyHigh)
        {
            error = new ApiError(
                "invalid_obfuscation",
                "RejectAfterTime must stay above RekeyAfterTime, or a session is dropped before it is renewed.");
            return false;
        }

        return true;
    }

    private static bool ValidateHeaders(ServerObfuscationProfile obfuscation, out ApiError error)
    {
        error = ApiError.Empty;

        (string Name, string? Value, int Stock)[] headers =
        [
            ("H1", obfuscation.H1, 1),
            ("H2", obfuscation.H2, 2),
            ("H3", obfuscation.H3, 3),
            ("H4", obfuscation.H4, 4)
        ];

        foreach (var (name, value, stock) in headers)
        {
            if (value is null)
            {
                continue;
            }

            if (!AwgRange.TryParse(value, AwgRange.MaxUInt32, out var low, out var high))
            {
                error = new ApiError(
                    "invalid_obfuscation",
                    $"{name} must be a number or a range like 1000000-1000500 with the low bound first.");
                return false;
            }

            // Leaving a header at its stock type is how you disable it for that message. Any
            // other value must clear the reserved range, or the node treats real WireGuard
            // packets as this message type.
            var disabled = low == high && low == (uint)stock;
            if (!disabled && low <= ReservedHeaderMax)
            {
                error = new ApiError(
                    "invalid_obfuscation",
                    $"{name} must be above 4, or exactly {stock} to leave it at the WireGuard default.");
                return false;
            }
        }

        for (var i = 0; i < headers.Length; i++)
        {
            for (var j = i + 1; j < headers.Length; j++)
            {
                if (AwgRange.Overlap(headers[i].Value, headers[j].Value))
                {
                    error = new ApiError(
                        "invalid_obfuscation",
                        $"{headers[i].Name} and {headers[j].Name} must not overlap.");
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateHeaderProtection(ServerObfuscationProfile obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation.HeaderProtectionKey is null)
        {
            return true;
        }

        Span<byte> key = stackalloc byte[HeaderProtectionKeySize + 1];
        if (!Convert.TryFromBase64String(obfuscation.HeaderProtectionKey, key, out var written) || written != HeaderProtectionKeySize)
        {
            error = new ApiError("invalid_obfuscation", "HeaderProtectionKey must be a base64-encoded 32-byte key.");
            return false;
        }

        // The header cipher nonce travels in the junk padding, so every padded message type needs
        // room for it. amneziawg rejects the whole device configuration otherwise.
        (string Name, int? Value)[] paddings =
        [
            ("S1", obfuscation.S1),
            ("S2", obfuscation.S2),
            ("S3", obfuscation.S3),
            ("S4", obfuscation.S4)
        ];

        foreach (var (name, value) in paddings)
        {
            if ((value ?? 0) < HeaderProtectionNonceSize)
            {
                error = new ApiError(
                    "invalid_obfuscation",
                    $"{name} must be at least {HeaderProtectionNonceSize} when HeaderProtectionKey is set.");
                return false;
            }
        }

        return true;
    }
}
