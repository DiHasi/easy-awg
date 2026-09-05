using System.Text.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public sealed class FleetRepository(Database database)
{
    public FleetRecord? Find()
    {
        using var connection = database.Open();
        using var command = connection.Sql("SELECT * FROM fleet WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new FleetRecord(
            reader.GetInt32("generation"),
            reader.GetString("server_private_key"),
            reader.GetString("server_public_key"),
            reader.GetString("signing_private_key"),
            reader.GetString("signing_public_key"),
            reader.GetString("signing_key_id"),
            reader.GetString("subnet"),
            reader.GetInt32("listen_port"),
            reader.GetString("client_allowed_ips"),
            reader.GetStringOrNull("client_dns"),
            reader.GetString("endpoint_host"),
            Deserialize(reader.GetStringOrNull("obfuscation_json")),
            reader.GetInt64("revision"));
    }

    public void Insert(FleetRecord fleet, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO fleet (id, generation, server_private_key, server_public_key,
                               signing_private_key, signing_public_key, signing_key_id,
                               subnet, listen_port, client_allowed_ips, client_dns, endpoint_host,
                               obfuscation_json, revision, created_at, updated_at)
            VALUES (1, $generation, $serverPrivate, $serverPublic,
                    $signingPrivate, $signingPublic, $signingKeyId,
                    $subnet, $listenPort, $allowedIps, $dns, $endpointHost,
                    $obfuscation, $revision, $now, $now)
            """,
            ("$generation", fleet.Generation),
            ("$serverPrivate", fleet.ServerPrivateKey),
            ("$serverPublic", fleet.ServerPublicKey),
            ("$signingPrivate", fleet.SigningPrivateKey),
            ("$signingPublic", fleet.SigningPublicKey),
            ("$signingKeyId", fleet.SigningKeyId),
            ("$subnet", fleet.Subnet),
            ("$listenPort", fleet.ListenPort),
            ("$allowedIps", fleet.ClientAllowedIps),
            ("$dns", fleet.ClientDns),
            ("$endpointHost", fleet.EndpointHost),
            ("$obfuscation", Serialize(fleet.Obfuscation)),
            ("$revision", fleet.Revision),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void UpdateObfuscation(ServerObfuscationProfile? obfuscation, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET obfuscation_json = $obfuscation, updated_at = $now WHERE id = 1",
            ("$obfuscation", Serialize(obfuscation)),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    public void ReplaceIdentity(string serverPrivateKey, string serverPublicKey, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            """
            UPDATE fleet SET server_private_key = $private, server_public_key = $public, updated_at = $now
            WHERE id = 1
            """,
            ("$private", serverPrivateKey),
            ("$public", serverPublicKey),
            ("$now", now.ToStorage()));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Bumps the fleet revision and returns the new value. Every change that alters what a node
    /// should be running goes through here, so a node can decide whether it is in sync by
    /// comparing one number.
    /// </summary>
    public long BumpRevision(DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.Sql(
            "UPDATE fleet SET revision = revision + 1, updated_at = $now WHERE id = 1 RETURNING revision",
            ("$now", now.ToStorage()));

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? Serialize(ServerObfuscationProfile? value)
        => value is null ? null : JsonSerializer.Serialize(value, ControlJsonContext.Default.ServerObfuscationProfile);

    private static ServerObfuscationProfile? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, ControlJsonContext.Default.ServerObfuscationProfile);
}
