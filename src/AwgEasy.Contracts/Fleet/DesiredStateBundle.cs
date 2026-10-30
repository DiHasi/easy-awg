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
    /// padding ranges, and H1-H4 as ranges rather than single values. 3 added the egress
    /// addresses: one source address per node and one per peer that needs its own.
    ///
    /// What a 3.x profile requires of a node is <see cref="BundleSchema.Schema3ObfuscationVersion"/>
    /// and not this constant - the two stopped being the same number here.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

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
/// <param name="DefaultEgressAddress">
/// The source address traffic leaves this node with unless a peer names its own. Null leaves the
/// choice to the kernel, which is what every node did before this existed - and on a host with
/// several addresses on one interface that choice is invisible, which is the reason an operator
/// can now state it.
/// </param>
public sealed record NodeSettings(
    string InterfaceName,
    string? EgressInterface,
    int? Mtu,
    string? DefaultEgressAddress = null);

/// <param name="EgressAddress">
/// The source address this one peer leaves with, overriding the node's default. An address, not a
/// label: the panel keeps the labels and resolves them per node, so a node never has to know that
/// two of its addresses mean anything to anybody.
/// </param>
public sealed record BundlePeer(
    string PublicKey,
    string PresharedKey,
    string Address,
    string? EgressAddress = null);
