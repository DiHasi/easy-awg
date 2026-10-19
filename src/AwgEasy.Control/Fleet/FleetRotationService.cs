using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Replaces the fleet identity on purpose, which is the one thing the panel could not do.
///
/// Every node runs the same AmneziaWG key pair, the same preshared keys and the same wire format,
/// because that is what makes failover invisible. The cost of that design is its blast radius: a
/// hosting provider that reads one node's disk has read the whole fleet's secrets. Nothing else
/// in this panel may regenerate the identity - a rotation disconnects every client at once - so it
/// lives here, behind one endpoint an operator presses deliberately, and nowhere else.
///
/// What it does not do is rotate the bundle signing key pair. Agents pin that at enrollment and
/// refuse bundles signed by anything else, so replacing it would stop the fleet converging until
/// every node had been re-enrolled by hand, in the middle of the incident that prompted the
/// rotation. Re-pinning is a separate job with a separate design; half of it is worse than none.
///
/// Client key pairs are optional for the same kind of reason read the other way: a node never
/// held one, so a seized node does not expose them. The panel's own database does hold them, so
/// an operator who has to assume that was read asks for them too - and then every peer's public
/// key changes, which is a bigger change for the nodes than the identity itself.
/// </summary>
public sealed class FleetRotationService(
    FleetRepository fleet,
    FleetService fleetService,
    ClientRepository clients,
    NodeRepository nodes,
    IAwgKeyGenerator keys,
    EventLog events,
    ILogger<FleetRotationService> logger)
{
    public (FleetRotationResponse? Result, ApiError Error) Rotate(bool reissueClientKeys, string? actor)
    {
        var current = fleetService.Current;
        var now = DateTimeOffset.UtcNow;

        var serverPrivateKey = keys.GeneratePrivateKey();
        var serverPublicKey = keys.GeneratePublicKey(serverPrivateKey);
        var obfuscation = ServerObfuscationGenerator.Rotate(current.Obfuscation, keys);

        // A profile a node would refuse takes the whole tunnel down rather than part of it, so it
        // is checked before anything is written. Reaching here means the half carried over from
        // the existing profile is what fails, because the half just drawn satisfies these rules by
        // construction - so the honest answer is to name it and change nothing.
        if (!AwgObfuscationValidator.TryValidateServerProfile(obfuscation, out var profileError))
        {
            return (null, new ApiError(
                "rotation_profile_invalid",
                $"The fleet obfuscation profile cannot be rotated as it stands: {profileError.Message} Fix it under Obfuscation first."));
        }

        // Every peer, probes and disabled peers included: the preshared keys were all on the same
        // disk. Generated out here rather than inside the transaction, because every one of them
        // is a forked `awg` and a write transaction held open across that would block every node
        // polling for the bundle it is about to need.
        var peers = clients.ListAll();
        var material = peers
            .Select(peer =>
            {
                var privateKey = reissueClientKeys ? keys.GeneratePrivateKey() : null;
                return new RotatedPeerMaterial(
                    peer.Id,
                    keys.GeneratePresharedKey(),
                    privateKey,
                    privateKey is null ? null : keys.GeneratePublicKey(privateKey));
            })
            .ToArray();

        var outcome = fleet.Rotate(
            new FleetRotation(serverPrivateKey, serverPublicKey, obfuscation, material),
            now);

        if (reissueClientKeys)
        {
            // The old public keys are gone from the fleet, exactly as a deleted peer's are. A node
            // drops them from peer_stats on its next report, but nothing else would ever clear the
            // counter baselines standing against keys that no longer exist anywhere.
            foreach (var peer in peers)
            {
                nodes.ForgetPeer(peer.PublicKey);
            }
        }

        var reissued = reissueClientKeys ? "and every client key pair" : "client keys kept";

        events.Record(
            "fleet.rotated",
            $"Fleet identity rotated to generation {outcome.Generation}: new server key pair, "
            + $"wire format and preshared keys ({reissued}). {peers.Count} peer(s) affected, "
            + $"{outcome.ShareLinksRevoked} share link(s) revoked. Every client config must be handed out again.",
            actor: actor);

        logger.LogWarning(
            "Fleet identity rotated to generation {Generation} at revision {Revision}. {Peers} peer(s) re-keyed, client key pairs reissued: {Reissued}.",
            outcome.Generation,
            outcome.Revision,
            peers.Count,
            reissueClientKeys);

        // Read back after the write, so the keys named here are the ones a node will be handed.
        // People's peers only: a probe is a peer on every node, but it is not somebody who has to
        // be found and given a new config - it re-renders its own from the panel on its next poll.
        var rotated = clients.List()
            .Select(client => new RotatedClientResponse(client.Id, client.Name, client.PublicKey, reissueClientKeys))
            .ToArray();

        return (new FleetRotationResponse(
            outcome.Generation,
            current.Generation,
            serverPublicKey,
            outcome.Revision,
            reissueClientKeys,
            outcome.ShareLinksRevoked,
            peers.Count - rotated.Length,
            rotated),
            ApiError.Empty);
    }
}
