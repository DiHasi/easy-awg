namespace AwgEasy.Contracts;

public static class AwgObfuscationValidator
{
    public static bool TryValidateServerProfile(ServerObfuscationProfile? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        if (obfuscation is null)
        {
            return true;
        }

        if (!ValidateHValues([obfuscation.H1, obfuscation.H2, obfuscation.H3, obfuscation.H4], out error))
        {
            return false;
        }

        return ValidateClientOverrides(obfuscation.GetDefaults(), out error);
    }

    public static bool TryValidateClientOverrides(ClientObfuscationOverrides? obfuscation, out ApiError error)
    {
        error = ApiError.Empty;
        return obfuscation is null || ValidateClientOverrides(obfuscation, out error);
    }

    private static bool ValidateClientOverrides(ClientObfuscationOverrides obfuscation, out ApiError error)
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

        return true;
    }

    private static bool ValidateHValues(string?[] values, out ApiError error)
    {
        error = ApiError.Empty;
        var headers = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToArray();
        if (headers.Length != headers.Distinct().Count())
        {
            error = new ApiError("invalid_obfuscation", "H1-H4 values must be unique.");
            return false;
        }

        return true;
    }
}
