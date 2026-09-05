using System.Security.Cryptography;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public sealed record ClientShare(string ClientId, DateTimeOffset ExpiresAt);

/// <summary>
/// Time-limited links for handing a config to someone without emailing the file around.
///
/// Stored in the database rather than in memory, unlike the single-server version: a share that
/// silently dies on the next container restart, while the UI promised 24 hours, is worse than no
/// share at all. Only the token hash is kept, so a database dump does not yield working links.
/// </summary>
public sealed class ShareRepository(Database database)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public string Create(string clientId, DateTimeOffset now)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));

        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO client_shares (token_hash, client_id, created_at, expires_at) VALUES ($hash, $clientId, $created, $expires)",
            ("$hash", Hash(token)),
            ("$clientId", clientId),
            ("$created", now.ToStorage()),
            ("$expires", now.Add(Lifetime).ToStorage()));

        command.ExecuteNonQuery();
        return token;
    }

    public ClientShare? Resolve(string token, DateTimeOffset now)
    {
        using var connection = database.Open();

        // Expired rows are cleared on access rather than by a background job: shares are few and
        // this keeps the table from growing without adding a scheduler.
        using (var prune = connection.Sql("DELETE FROM client_shares WHERE expires_at <= $now", ("$now", now.ToStorage())))
        {
            prune.ExecuteNonQuery();
        }

        using var command = connection.Sql(
            "SELECT client_id, expires_at FROM client_shares WHERE token_hash = $hash",
            ("$hash", Hash(token)));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var expiresAt = reader.GetTimestamp("expires_at");
        return expiresAt <= now ? null : new ClientShare(reader.GetString("client_id"), expiresAt);
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
