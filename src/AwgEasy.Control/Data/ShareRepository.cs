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

    /// <param name="generation">The fleet generation the config behind this link belongs to. A
    /// link is a promise about one identity, so it is stamped with the one it was made for.</param>
    public string Create(string clientId, DateTimeOffset now, int generation)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));

        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO client_shares (token_hash, client_id, generation, created_at, expires_at) VALUES ($hash, $clientId, $generation, $created, $expires)",
            ("$hash", Hash(token)),
            ("$clientId", clientId),
            ("$generation", generation),
            ("$created", now.ToStorage()),
            ("$expires", now.Add(Lifetime).ToStorage()));

        command.ExecuteNonQuery();
        return token;
    }

    /// <param name="generation">
    /// The fleet generation as it stands now. A link made for an earlier one does not resolve: the
    /// config it promised no longer exists, and the replacement is not something to hand to
    /// whoever is still holding a token that was passed around before the identity was rotated.
    /// A rotation sweeps these rows itself, so this is what the sweep is being checked against
    /// rather than the only thing stopping them.
    /// </param>
    public ClientShare? Resolve(string token, DateTimeOffset now, int generation)
    {
        using var connection = database.Open();

        // Expired rows are cleared on access rather than by a background job: shares are few and
        // this keeps the table from growing without adding a scheduler.
        using (var prune = connection.Sql("DELETE FROM client_shares WHERE expires_at <= $now", ("$now", now.ToStorage())))
        {
            prune.ExecuteNonQuery();
        }

        using var command = connection.Sql(
            "SELECT client_id, expires_at FROM client_shares WHERE token_hash = $hash AND generation = $generation",
            ("$hash", Hash(token)),
            ("$generation", generation));

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
