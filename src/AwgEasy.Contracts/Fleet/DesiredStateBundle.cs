namespace AwgEasy.Contracts;

/// <summary>
/// The desired state a single node must converge to. Produced by the control plane,
/// signed, and pulled by the node agent.
///
/// Deliberately excludes client private keys and client names: a node only ever needs
/// public keys, preshared keys and tunnel addresses to build its peer list. A seized or
/// snapshotted node therefore never reveals who the clients are or how to impersonate them.
/// </summary>
public sealed record DesiredStateBundle(
    int SchemaVersion,
    long Revision,
    string NodeId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    FleetIdentity Identity,
    NetworkProfile Network,
    ServerObfuscationProfile? Obfuscation,
    NodeSettings Node,
    BundlePeer[] Peers)
{
    /// <summary>
    /// 2 added the AmneziaWG 3.x settings: header protection, random trailers, the timing and
    /// padding ranges, and H1-H4 as ranges rather than single values.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The oldest schema this build still understands. The control plane must be able to serve
    /// agents one version behind, because the panel is always upgraded before the nodes are, so
    /// both sides accept the whole window rather than only the current version.
    /// </summary>
    public const int MinimumSupportedSchemaVersion = 1;
}

/// <summary>Fleet-wide AmneziaWG server identity. Identical on every node, which is what makes
/// endpoint failover invisible to clients.</summary>
public sealed record FleetIdentity(
    int Generation,
    string ServerPrivateKey,
    string ServerPublicKey);

/// <summary>Only what the node needs to bring up its interface. AllowedIPs and client DNS are
/// client-config concerns and stay in the control plane.</summary>
public sealed record NetworkProfile(
    string Subnet,
    int ListenPort);

/// <summary>Node-local knobs. <see cref="EgressInterface"/> is null when the agent should detect
/// the default-route interface itself instead of assuming eth0.</summary>
public sealed record NodeSettings(
    string InterfaceName,
    string? EgressInterface,
    int? Mtu);

public sealed record BundlePeer(
    string PublicKey,
    string PresharedKey,
    string Address);
