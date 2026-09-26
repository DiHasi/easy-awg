using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Owns the fleet's source of truth: the shared AmneziaWG identity, the bundle signing key, and
/// the revision counter that tells nodes whether they are up to date.
/// </summary>
public sealed class FleetService(
    FleetRepository fleet,
    ClientRepository clients,
    NodeRepository nodes,
    IAwgKeyGenerator keys,
    ControlOptions options,
    EventLog events,
    ILogger<FleetService> logger)
{
    public FleetRecord Current => fleet.Find() ?? throw new InvalidOperationException("Fleet has not been initialized.");

    /// <summary>
    /// Creates the fleet identity on first run. The AmneziaWG key pair is what makes failover
    /// invisible - every node presents it, so switching endpoints never changes what the client
    /// is talking to. The separate signing key pair authenticates bundles to agents.
    /// </summary>
    public void EnsureInitialized()
    {
        if (fleet.Find() is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var serverPrivateKey = keys.GeneratePrivateKey();
        using var signingKey = BundleSigning.CreateKey();

        var record = new FleetRecord(
            Generation: 1,
            ServerPrivateKey: serverPrivateKey,
            ServerPublicKey: keys.GeneratePublicKey(serverPrivateKey),
            SigningPrivateKey: BundleSigning.ExportPrivateKey(signingKey),
            SigningPublicKey: BundleSigning.ExportPublicKey(signingKey),
            SigningKeyId: BundleSigning.ComputeKeyId(signingKey),
            Subnet: options.DefaultSubnet,
            ListenPort: options.DefaultListenPort,
            ClientAllowedIps: options.DefaultClientAllowedIps,
            ClientDns: options.DefaultClientDns,
            EndpointHost: options.DefaultEndpointHost,
            Obfuscation: null,
            Revision: 1);

        // Fail fast on a bad subnet rather than handing nodes an unusable bundle.
        Ipv4Network.Parse(record.Subnet);

        fleet.Insert(record, now);
        events.Record("fleet.initialized", $"Fleet identity created (generation 1, signing key {record.SigningKeyId}).");
        logger.LogInformation("Initialized fleet identity, signing key {KeyId}.", record.SigningKeyId);
    }

    public long BumpRevision(string reason)
    {
        var revision = fleet.BumpRevision(DateTimeOffset.UtcNow);
        logger.LogInformation("Fleet revision is now {Revision} ({Reason}).", revision, reason);
        return revision;
    }

    public FleetResponse Describe()
    {
        var current = Current;
        return new FleetResponse(
            current.Generation,
            current.ServerPublicKey,
            current.Subnet,
            current.ListenPort,
            current.ClientAllowedIps,
            current.ClientDns,
            current.EndpointHost,
            current.Obfuscation,
            current.Revision,
            clients.List().Count,
            nodes.List().Count);
    }

    /// <summary>
    /// Builds the desired state for one node and signs it.
    ///
    /// Client private keys and client names are deliberately left out: a node needs only public
    /// keys, preshared keys and addresses to serve peers, so a seized node reveals neither who
    /// the clients are nor anything that would let someone impersonate them.
    ///
    /// The bundle is issued at the schema the node reported it understands. The panel is always
    /// upgraded before the nodes are walked, so during a fleet upgrade some nodes are still a
    /// schema behind; handing one of those a profile it cannot parse would take its tunnel down
    /// rather than leave it on the older settings.
    /// </summary>
    public SignedBundle BuildBundle(NodeRecord node)
    {
        var current = Current;
        var now = DateTimeOffset.UtcNow;
        var schemaVersion = BundleSchema.Normalize(node.BundleSchemaVersion);

        var obfuscation = current.Obfuscation;
        if (schemaVersion < DesiredStateBundle.CurrentSchemaVersion && obfuscation is not null)
        {
            if (obfuscation.UsesSchema3Features)
            {
                // Worth a warning rather than a debug line: such a node serves a wire format its
                // own clients no longer speak, so it is effectively out of the fleet until it is
                // upgraded. Only fires when the revision actually moved - unchanged polls are 304s.
                logger.LogWarning(
                    "Node {NodeName} reports bundle schema {NodeSchema} and cannot apply the AmneziaWG 3.x profile. Serving it the downgraded profile; upgrade the agent on that server.",
                    node.Name,
                    schemaVersion);
            }

            obfuscation = obfuscation.ToSchemaV1();
        }

        var peers = clients.ListEnabled()
            .Select(client => new BundlePeer(client.PublicKey, client.PresharedKey, client.Address))
            .ToArray();

        var bundle = new DesiredStateBundle(
            schemaVersion,
            current.Revision,
            node.Id,
            now,
            now.Add(options.BundleLifetime),
            new FleetIdentity(current.Generation, current.ServerPrivateKey, current.ServerPublicKey),
            new NetworkProfile(current.Subnet, current.ListenPort),
            obfuscation,
            new NodeSettings("awg0", node.EgressInterface, node.Mtu),
            peers);

        using var signingKey = BundleSigning.ImportPrivateKey(current.SigningPrivateKey);
        return BundleSigning.Sign(bundle, signingKey, current.SigningKeyId, ContractsJsonContext.Default.DesiredStateBundle);
    }
}
