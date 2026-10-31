using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>An account that can sign in to the panel.</summary>
public sealed record AdminAccount(string Username, bool IsDemo, DateTimeOffset CreatedAt);

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
    /// The shortest password the panel will accept through the API.
    ///
    /// Longer than a login form usually asks for, because what is behind it is not an account but
    /// a fleet: the database this guards holds the server private key, every peer's preshared key
    /// and every client's own key pair, and there is no second factor and no account recovery.
    /// <see cref="LoginThrottle"/> caps how fast guesses arrive; this is what makes the space they
    /// are guessing in worth the wait.
    ///
    /// Checked where a password is chosen, not where one is used: an existing account below it
    /// still signs in, because refusing would lock out the operator this is meant to protect.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// Whether a chosen password may be stored, as an <see cref="ApiError"/> or null. One method
    /// so every way in says the same thing - creating an account, changing your own password and
    /// resetting somebody else's are three endpoints and one rule.
    /// </summary>
    public static ApiError? ValidatePassword(string? password)
        => password is null || password.Length < MinimumPasswordLength
            ? new ApiError(
                "password_too_short",
                $"A password must be at least {MinimumPasswordLength} characters. This one unlocks the fleet's private "
                + "key, and the panel has no second factor to fall back on.")
            : null;

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
    public void Create(string username, string password, bool demo = false, string? actor = null)
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
                : $"Admin account '{username}' created.",
            actor: actor);

        logger.LogInformation("Created {Kind} account {Username}.", demo ? "demo" : "admin", username);
    }

    /// <summary>
    /// Replaces an account's password with a fresh salt and hash.
    ///
    /// Nothing else about the row changes, and no session is torn down: the cookie carries a name
    /// rather than anything derived from the password, so a sign-in that was already open stays
    /// open. That is the honest consequence of a session cookie and worth stating rather than
    /// hiding, because it is what an operator rotating a password after a compromise needs to
    /// know - the password stops a new sign-in, and a stolen cookie keeps working until it expires.
    /// </summary>
    /// <returns>False when there is no such account.</returns>
    public bool ChangePassword(string username, string password, string? actor = null)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE admins SET password_hash = $hash WHERE username = $username COLLATE NOCASE",
            ("$hash", HashPassword(password)),
            ("$username", username));

        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        events.Record(
            "admin.password_changed",
            string.Equals(username, actor, StringComparison.OrdinalIgnoreCase)
                ? $"Admin '{username}' changed their own password."
                : $"Password for admin '{username}' was reset.",
            actor: actor);

        logger.LogInformation("Password changed for account {Username}.", username);
        return true;
    }

    /// <summary>
    /// Removes an account. The caller is the one that refuses to remove the last operator and the
    /// signed-in one: both of those are decisions about who is asking, which this does not know.
    /// </summary>
    /// <returns>False when there is no such account.</returns>
    public bool Delete(string username, string? actor = null)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "DELETE FROM admins WHERE username = $username COLLATE NOCASE",
            ("$username", username));

        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        events.Record("admin.deleted", $"Admin account '{username}' deleted.", actor: actor);
        logger.LogInformation("Deleted account {Username}.", username);
        return true;
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
        using var command = connection.Sql("SELECT username, is_demo, created_at FROM admins ORDER BY id");

        using var reader = command.ExecuteReader();
        var accounts = new List<AdminAccount>();
        while (reader.Read())
        {
            accounts.Add(new AdminAccount(
                reader.GetString("username"),
                reader.GetBoolean("is_demo"),
                reader.GetTimestamp("created_at")));
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
