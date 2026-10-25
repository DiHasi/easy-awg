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
    ProbeService probeService,
    PanelRepository panels,
    PanelContext panelContext,
    PanelLocalStore panelStore,
    FleetService fleet,
    ControlOptions options,
    EventLog events,
    ILogger<EnrollmentService> logger)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    public const string NodeToken = "node";
    public const string ProbeToken = "probe";

    /// <summary>
    /// A standby panel. Kept apart from the other two by more than tidiness: a node token hands
    /// over the fleet identity, a probe token hands over one client key, and this one hands over
    /// the entire panel - every client private key, every admin password hash and the bundle
    /// signing key. An install command pasted into the wrong place must not be able to turn one
    /// into another, which is what <see cref="ReadToken"/> enforces.
    /// </summary>
    public const string PanelToken = "panel";

    /// <param name="overrides">
    /// Per-node settings the node is to be born with, carried on the token rather than applied
    /// afterwards: the MTU and the egress interface are in the first bundle the agent fetches, so
    /// a node that needs them gets them before it ever brings an interface up. Probes have none -
    /// a probe receives no bundle at all.
    /// </param>
    public EnrollmentTokenResponse CreateToken(
        string nodeName,
        string controlUrl,
        string kind = NodeToken,
        NodeOverrides? overrides = null)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TokenLifetime);
        var settings = overrides ?? NodeOverrides.None;

        using var connection = database.Open();
        using var command = connection.Sql(
            """
            INSERT INTO enrollment_tokens (token_hash, node_name, created_at, expires_at, kind, endpoint_host, egress_interface, mtu)
            VALUES ($hash, $name, $created, $expires, $kind, $endpointHost, $egress, $mtu)
            """,
            ("$hash", Hash(token)),
            ("$kind", kind),
            ("$name", nodeName),
            ("$created", now.ToStorage()),
            ("$expires", expiresAt.ToStorage()),
            ("$endpointHost", settings.EndpointHost),
            ("$egress", settings.EgressInterface),
            ("$mtu", settings.Mtu));

        command.ExecuteNonQuery();
        events.Record($"{kind}.token_issued", $"Enrollment token issued for {kind} '{nodeName}'.");

        var baseUrl = controlUrl.TrimEnd('/');
        var install = kind switch
        {
            ProbeToken => ProbeInstallCommand(baseUrl, token),
            PanelToken => PanelInstallHint(baseUrl, token),
            // The agent runs with host networking and takes its listen port from the bundle, so
            // the command needs nothing but where to enroll.
            _ => $"curl -fsSL {baseUrl}/install.sh | sh -s -- --url {baseUrl} --token {token}"
        };

        return new EnrollmentTokenResponse(token, nodeName, expiresAt, install);
    }

    /// <summary>
    /// A probe needs no host networking and no firewall rules - it only makes outbound handshakes -
    /// so it runs on Docker's default bridge with just enough privilege to create its interface.
    /// It is the node image: the tooling a handshake needs is already in it.
    /// </summary>
    private static string ProbeInstallCommand(string baseUrl, string token)
        => "docker run -d --name awg-probe --restart unless-stopped --cap-add NET_ADMIN --device /dev/net/tun:/dev/net/tun "
            + $"-e AWG_ROLE=probe -e AWG_CONTROL_URL={baseUrl} -e AWG_ENROLLMENT_TOKEN={token} "
            + "-v awg-probe-state:/etc/awg-probe dihasi/awg-node:latest";

    /// <summary>
    /// What to put in the standby panel's own environment. Not a one-liner like the other two:
    /// a panel is the same image as this one with a database of its own, so what is handed over
    /// is the three variables that point it here - and the warning that comes with them.
    /// </summary>
    private static string PanelInstallHint(string baseUrl, string token)
        => $"""
            AWG_PANEL_ROLE=standby
            AWG_PANEL_PEER_URL={baseUrl}
            AWG_PANEL_ENROLLMENT_TOKEN={token}
            AWG_PANEL_URL=https://standby.example.com
            AWG_PANEL_PUBLIC_IP=203.0.113.10
            """;

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

        // An agent keeps its key pair across restarts, so a server whose node was revoked - but not
        // deleted - would otherwise hit a unique-index violation and get an opaque 500. Refuse it
        // with a reason: re-adopting a revoked node should be a deliberate act in the panel.
        if (nodes.FindByAgentKey(request.AgentPublicKey) is { } existing)
        {
            return (null, new ApiError(
                "enrollment_key_registered",
                existing.Revoked
                    ? $"This server is already registered as '{existing.Name}' and was revoked. Delete that node in the panel first, then enroll again."
                    : $"This server is already registered as '{existing.Name}'. Delete that node in the panel first, or leave the existing agent running."));
        }

        var now = DateTimeOffset.UtcNow;
        var (nodeName, overrides, tokenError) = ReadToken(request, NodeToken, now);
        if (nodeName is null)
        {
            return (null, tokenError);
        }

        var node = new NodeRecord(
            Id: Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name: nodeName,
            Hostname: request.Hostname,
            EndpointHost: overrides.EndpointHost,
            AgentPublicKey: request.AgentPublicKey,
            AgentVersion: request.AgentVersion,
            Status: NodeStatuses.Provisioning,
            AppliedRevision: 0,
            InterfaceUp: false,
            Backend: null,
            BundleSchemaVersion: BundleSchema.Normalize(request.BundleSchemaVersion),
            EgressInterface: overrides.EgressInterface,
            Mtu: overrides.Mtu,
            PublicIp: null,
            LastSeenAt: null,
            LastError: null,
            Revoked: false,
            EnrolledAt: now);

        nodes.Insert(node);
        Consume(request.EnrollmentToken, node.Id, now);

        var current = fleet.Current;
        events.Record("node.enrolled", $"Node '{nodeName}' enrolled from {request.Hostname}.", nodeId: node.Id);
        logger.LogInformation("Node {NodeName} ({NodeId}) enrolled from {Hostname}.", nodeName, node.Id, request.Hostname);

        return (new EnrollResponse(node.Id, current.SigningPublicKey, current.SigningKeyId), ApiError.Empty);
    }

    /// <summary>
    /// Adopts a probe. Same token flow and the same kind of key as a node, and the same pinned
    /// signing key in the answer - but what it is registered as is a client, not a server.
    /// </summary>
    public (EnrollResponse? Response, ApiError Error) EnrollProbe(EnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || string.IsNullOrWhiteSpace(request.AgentPublicKey))
        {
            return (null, new ApiError("enrollment_invalid", "Enrollment token and agent public key are required."));
        }

        try
        {
            using var _ = BundleSigning.ImportPublicKey(request.AgentPublicKey);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return (null, new ApiError("enrollment_invalid", "Agent public key is not a valid P-256 SubjectPublicKeyInfo."));
        }

        if (probeService.FindByAgentKey(request.AgentPublicKey) is { } existing)
        {
            return (null, new ApiError(
                "enrollment_key_registered",
                $"This probe is already registered as '{existing.Name}'. Delete it in the panel first, then enroll again."));
        }

        var now = DateTimeOffset.UtcNow;
        var (probeName, _, tokenError) = ReadToken(request, ProbeToken, now);
        if (probeName is null)
        {
            return (null, tokenError);
        }

        var (probe, createError) = probeService.Create(probeName, request.Hostname, request.AgentPublicKey, request.AgentVersion, now);
        if (probe is null)
        {
            return (null, createError);
        }

        Consume(request.EnrollmentToken, probe.Id, now);

        var current = fleet.Current;
        events.Record("probe.enrolled", $"Probe '{probeName}' enrolled from {request.Hostname}.");
        logger.LogInformation("Probe {ProbeName} ({ProbeId}) enrolled from {Hostname}.", probeName, probe.Id, request.Hostname);

        return (new EnrollResponse(probe.Id, current.SigningPublicKey, current.SigningKeyId), ApiError.Empty);
    }

    /// <summary>
    /// Adopts a standby panel: registers it, and answers with the fleet signing key for it to pin.
    ///
    /// The same token flow and the same kind of key as a node or a probe, and deliberately so -
    /// but what this one is handed is not comparable. A node gets public keys and addresses; a
    /// probe gets one client config. A panel gets the fleet private key, the bundle signing key
    /// pair, every client private key and every admin password hash, because that is what it takes
    /// to keep the fleet converging when the panel that was in charge is gone. Enrolling one is
    /// handing over the panel, not a share of it.
    /// </summary>
    public (PanelEnrollResponse? Response, ApiError Error) EnrollPanel(EnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || string.IsNullOrWhiteSpace(request.AgentPublicKey))
        {
            return (null, new ApiError("enrollment_invalid", "Enrollment token and panel public key are required."));
        }

        try
        {
            using var _ = BundleSigning.ImportPublicKey(request.AgentPublicKey);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return (null, new ApiError("enrollment_invalid", "Panel public key is not a valid P-256 SubjectPublicKeyInfo."));
        }

        if (panels.FindByAgentKey(request.AgentPublicKey) is { } registered)
        {
            return (null, new ApiError(
                "enrollment_key_registered",
                $"This panel is already enrolled as '{registered.Name}'. Delete it here first, then enroll again."));
        }

        var now = DateTimeOffset.UtcNow;
        var (panelName, _, tokenError) = ReadToken(request, PanelToken, now);
        if (panelName is null)
        {
            return (null, tokenError);
        }

        var self = panelContext.State;

        // The first standby is what turns one panel into a pair, so this is where the panel that
        // has been serving all along writes down that it is the one in charge. Adopted at term 1
        // rather than promoted: nothing failed over, and the log should not say it did.
        var selfRow = panels.Find(self.PanelId);
        if (selfRow is null)
        {
            panels.Insert(new PanelRecord(
                self.PanelId,
                options.Panel.DisplayName,
                Environment.MachineName,
                self.AgentPublicKey,
                Revoked: false,
                EnrolledAt: now,
                Url: options.Panel.Url,
                PublicIp: options.Panel.PublicIp,
                AgentVersion: PanelVersion.Current,
                Role: PanelRoles.Active));
        }

        var term = panels.AdoptAsActive(self.PanelId, now);

        var panel = new PanelRecord(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            panelName,
            request.Hostname,
            request.AgentPublicKey,
            Revoked: false,
            EnrolledAt: now,
            AgentVersion: request.AgentVersion,
            Role: PanelRoles.Standby,
            Term: term);

        panels.Insert(panel);
        Consume(request.EnrollmentToken, panel.Id, now);

        var current = fleet.Current;

        // This panel created the fleet, so it never enrolled into anything and holds no pinned
        // signing key - and without one it could never accept a snapshot, which means it could
        // never become a standby. Pinned from its own fleet row, because this is the moment it
        // stops being the only panel there is. The key is the same one the other panel will sign
        // with: the signing key pair is what a rotation deliberately leaves alone.
        panelStore.PinOwnSigningKey(current.SigningPublicKey, current.SigningKeyId);

        events.Record("panel.enrolled", $"Standby panel '{panelName}' enrolled from {request.Hostname}.");
        logger.LogWarning(
            "Standby panel {PanelName} ({PanelId}) enrolled from {Hostname}. It now replicates the fleet identity, "
            + "every client private key and every admin credential.",
            panelName,
            panel.Id,
            request.Hostname);

        return (
            new PanelEnrollResponse(
                panel.Id,
                current.SigningPublicKey,
                current.SigningKeyId,
                selfRow?.Name ?? options.Panel.DisplayName,
                term,
                options.Panel.Url),
            ApiError.Empty);
    }

    /// <summary>
    /// Checks a token without spending it. A node token cannot enroll a probe or the other way
    /// round: one registers a server that holds the fleet key, the other a client in a network
    /// the operator does not control, and an install command pasted in the wrong place must not
    /// quietly turn one into the other.
    /// </summary>
    private (string? Name, NodeOverrides Overrides, ApiError Error) ReadToken(EnrollRequest request, string kind, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var lookup = connection.Sql(
            """
            SELECT node_name, expires_at, used_at, kind, endpoint_host, egress_interface, mtu
              FROM enrollment_tokens
             WHERE token_hash = $hash
            """,
            ("$hash", Hash(request.EnrollmentToken)));
        using var reader = lookup.ExecuteReader();

        if (!reader.Read())
        {
            logger.LogWarning("Enrollment attempted with an unknown token from {Hostname}.", request.Hostname);
            return (null, NodeOverrides.None, new ApiError("enrollment_invalid", "Enrollment token is not valid."));
        }

        if (reader.GetString("kind") != kind)
        {
            return (null, NodeOverrides.None, new ApiError(
                "enrollment_wrong_kind",
                $"This token enrolls a {reader.GetString("kind")}, not a {kind}. Issue a {kind} token in the panel."));
        }

        if (reader.GetStringOrNull("used_at") is not null)
        {
            return (null, NodeOverrides.None, new ApiError("enrollment_used", "Enrollment token has already been used."));
        }

        if (reader.GetTimestamp("expires_at") < now)
        {
            return (null, NodeOverrides.None, new ApiError("enrollment_expired", "Enrollment token has expired."));
        }

        return (
            reader.GetString("node_name"),
            new NodeOverrides(
                reader.GetStringOrNull("endpoint_host"),
                reader.GetStringOrNull("egress_interface"),
                reader.GetInt32OrNull("mtu")),
            ApiError.Empty);
    }

    private void Consume(string token, string enrolledId, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var consume = connection.Sql(
            "UPDATE enrollment_tokens SET used_at = $now, used_by_node_id = $enrolledId WHERE token_hash = $hash",
            ("$now", now.ToStorage()),
            ("$enrolledId", enrolledId),
            ("$hash", Hash(token)));

        consume.ExecuteNonQuery();
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
