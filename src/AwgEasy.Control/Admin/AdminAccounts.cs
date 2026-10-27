using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AwgEasy.Control;

/// <summary>An account that can sign in to the panel.</summary>
public sealed record AdminAccount(string Username, bool IsDemo);

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

    /// <summary>
    /// Whether anybody can actually administer this panel.
    ///
    /// Demo accounts are not counted, and that is the whole reason this is not a plain row count.
    /// A panel brought up with only AWG_DEMO_USER set has an account that can sign in and change
    /// nothing; counting it would mean that adding AWG_ADMIN_USER afterwards never created the
    /// real one, leaving a panel nobody can administer and no obvious reason why.
    /// </summary>
    public bool AnyOperatorExists()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT COUNT(*) FROM admins WHERE is_demo = 0");
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public bool Exists(string username)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT 1 FROM admins WHERE username = $username COLLATE NOCASE",
            ("$username", username));

        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Creates an account. <paramref name="demo"/> makes it a read-only one: it signs in exactly
    /// as an operator does and is refused every write, with every name and address it is served
    /// replaced on the way out.
    /// </summary>
    public void Create(string username, string password, bool demo = false)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO admins (username, password_hash, is_demo, created_at) VALUES ($username, $hash, $demo, $now)",
            ("$username", username),
            ("$hash", HashPassword(password)),
            ("$demo", demo),
            ("$now", DateTimeOffset.UtcNow.ToStorage()));

        command.ExecuteNonQuery();
        events.Record(
            demo ? "admin.demo_created" : "admin.created",
            demo
                ? $"Read-only demo account '{username}' created."
                : $"Admin account '{username}' created.");

        logger.LogInformation("Created {Kind} account {Username}.", demo ? "demo" : "admin", username);
    }

    /// <summary>
    /// Whether this account is the demo one, read from the row rather than from the session.
    ///
    /// Read per request on purpose. The alternative is a claim written into the cookie at sign-in,
    /// which is cheaper and wrong in the one direction that matters: a session minted before the
    /// flag was set would keep full access for as long as the cookie lives. A demoted account has
    /// to be read-only on its very next request, and this is one indexed read of a table with a
    /// handful of rows.
    /// </summary>
    public bool IsDemo(string username)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "SELECT is_demo FROM admins WHERE username = $username COLLATE NOCASE",
            ("$username", username));

        // An account that is not there at all reads as a demo one: a session whose row has been
        // deleted is about to be turned away anyway, and the safe answer while it is not is the
        // one that changes nothing.
        return command.ExecuteScalar() is not { } value || Convert.ToInt64(value) != 0;
    }

    /// <summary>
    /// Who can sign in, and which of them is the demo account.
    ///
    /// Read by the demo mask, which has to recognize an operator's name wherever the event log
    /// mentions it - and has to recognize the demo account's own name in order to leave it alone,
    /// because that is the name the visitor signed in with and reads in the header.
    /// </summary>
    public IReadOnlyList<AdminAccount> List()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT username, is_demo FROM admins ORDER BY id");

        using var reader = command.ExecuteReader();
        var accounts = new List<AdminAccount>();
        while (reader.Read())
        {
            accounts.Add(new AdminAccount(reader.GetString("username"), reader.GetBoolean("is_demo")));
        }

        return accounts;
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
