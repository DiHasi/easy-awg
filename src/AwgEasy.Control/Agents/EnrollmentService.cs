using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Day-0 enrollment: turns a one-time token into a registered node.
///
/// Only the token hash is stored, so a database dump does not hand out working enrollment
/// tokens. The agent generates its own key pair and sends only the public half, so nothing that
/// authenticates a node ever crosses the wire.
/// </summary>
public sealed class EnrollmentService(
    Database database,
    NodeRepository nodes,
    FleetService fleet,
    EventLog events,
    ILogger<EnrollmentService> logger)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    public EnrollmentTokenResponse CreateToken(string nodeName, string controlUrl)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TokenLifetime);

        using var connection = database.Open();
        using var command = connection.Sql(
            "INSERT INTO enrollment_tokens (token_hash, node_name, created_at, expires_at) VALUES ($hash, $name, $created, $expires)",
            ("$hash", Hash(token)),
            ("$name", nodeName),
            ("$created", now.ToStorage()),
            ("$expires", expiresAt.ToStorage()));

        command.ExecuteNonQuery();
        events.Record("node.token_issued", $"Enrollment token issued for node '{nodeName}'.");

        // The agent runs with host networking and takes its listen port from the bundle, so the
        // command needs nothing but where to enroll.
        var baseUrl = controlUrl.TrimEnd('/');
        var install = $"curl -fsSL {baseUrl}/install.sh | sh -s -- --url {baseUrl} --token {token}";

        return new EnrollmentTokenResponse(token, nodeName, expiresAt, install);
    }

    public (EnrollResponse? Response, ApiError Error) Enroll(EnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || string.IsNullOrWhiteSpace(request.AgentPublicKey))
        {
            return (null, new ApiError("enrollment_invalid", "Enrollment token and agent public key are required."));
        }

        // Reject a malformed key here rather than storing something that can never verify.
        try
        {
            using var _ = BundleSigning.ImportPublicKey(request.AgentPublicKey);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return (null, new ApiError("enrollment_invalid", "Agent public key is not a valid P-256 SubjectPublicKeyInfo."));
        }

        var now = DateTimeOffset.UtcNow;
        var hash = Hash(request.EnrollmentToken);

        using var connection = database.Open();
        using var lookup = connection.Sql(
            "SELECT node_name, expires_at, used_at FROM enrollment_tokens WHERE token_hash = $hash",
            ("$hash", hash));

        string nodeName;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read())
            {
                logger.LogWarning("Enrollment attempted with an unknown token from {Hostname}.", request.Hostname);
                return (null, new ApiError("enrollment_invalid", "Enrollment token is not valid."));
            }

            if (reader.GetStringOrNull("used_at") is not null)
            {
                return (null, new ApiError("enrollment_used", "Enrollment token has already been used."));
            }

            if (reader.GetTimestamp("expires_at") < now)
            {
                return (null, new ApiError("enrollment_expired", "Enrollment token has expired."));
            }

            nodeName = reader.GetString("node_name");
        }

        var node = new NodeRecord(
            Id: Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name: nodeName,
            Hostname: request.Hostname,
            EndpointHost: null,
            AgentPublicKey: request.AgentPublicKey,
            AgentVersion: request.AgentVersion,
            Status: NodeStatuses.Provisioning,
            AppliedRevision: 0,
            InterfaceUp: false,
            Backend: null,
            EgressInterface: null,
            Mtu: null,
            LastSeenAt: null,
            LastError: null,
            Revoked: false,
            EnrolledAt: now);

        nodes.Insert(node);

        using var consume = connection.Sql(
            "UPDATE enrollment_tokens SET used_at = $now, used_by_node_id = $nodeId WHERE token_hash = $hash",
            ("$now", now.ToStorage()),
            ("$nodeId", node.Id),
            ("$hash", hash));

        consume.ExecuteNonQuery();

        var current = fleet.Current;
        events.Record("node.enrolled", $"Node '{nodeName}' enrolled from {request.Hostname}.", nodeId: node.Id);
        logger.LogInformation("Node {NodeName} ({NodeId}) enrolled from {Hostname}.", nodeName, node.Id, request.Hostname);

        return (new EnrollResponse(node.Id, current.SigningPublicKey, current.SigningKeyId), ApiError.Empty);
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
