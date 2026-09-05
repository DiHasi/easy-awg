using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AwgEasy.Control;

/// <summary>
/// Admin credentials.
///
/// The single-server version had no authentication at all, which in a fleet setup would mean
/// anyone reaching the panel could pull the identity of every node. PBKDF2-SHA256 keeps the
/// hashing in the BCL; the iteration count follows current OWASP guidance.
/// </summary>
public sealed class AdminAccounts(Database database, EventLog events, ILogger<AdminAccounts> logger)
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public bool AnyExists()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT COUNT(*) FROM admins");
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Create(string username, string password)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO admins (username, password_hash, created_at) VALUES ($username, $hash, $now)",
            ("$username", username),
            ("$hash", HashPassword(password)),
            ("$now", DateTimeOffset.UtcNow.ToStorage()));

        command.ExecuteNonQuery();
        events.Record("admin.created", $"Admin account '{username}' created.");
        logger.LogInformation("Created admin account {Username}.", username);
    }

    public bool Verify(string username, string password)
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT password_hash FROM admins WHERE username = $username COLLATE NOCASE", ("$username", username));
        var stored = command.ExecuteScalar() as string;

        if (stored is null)
        {
            // Still spend the work factor so a missing user is not distinguishable by timing.
            _ = HashPassword(password);
            return false;
        }

        return VerifyPassword(password, stored);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256"
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
