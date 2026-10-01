using System.Collections.Concurrent;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

public sealed record AgentAuthResult(bool Succeeded, NodeRecord? Node, string? Failure)
{
    public static AgentAuthResult Ok(NodeRecord node) => new(true, node, null);

    public static AgentAuthResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Verifies that a request really came from the node it claims to be, by checking the signature
/// against the public key recorded at enrollment.
///
/// Authorization is a lookup in the nodes table rather than certificate chain validation, which
/// means revoking a node is a single UPDATE that takes effect on the very next request - no CRL,
/// no OCSP, no distribution delay.
/// </summary>
public sealed class AgentAuthenticator(NodeRepository nodes, ProbeRepository probes, ILogger<AgentAuthenticator> logger)
{
    // Nonces are only remembered for as long as a timestamp stays valid, so this cannot grow
    // without bound.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);

    public async Task<AgentAuthResult> AuthenticateAsync(HttpContext context)
    {
        NodeRecord? node = null;
        var failure = await VerifyAsync(context, id =>
        {
            node = nodes.Find(id);
            return node is null ? null : (node.AgentPublicKey, node.Revoked);
        });

        return failure is null ? AgentAuthResult.Ok(node!) : AgentAuthResult.Fail(failure);
    }

    /// <summary>
    /// The same check for a probe. Probes sign exactly as agents do, with their id in the node
    /// header; they are looked up in their own table, so a probe id can never pass as a node and
    /// be handed a bundle carrying the fleet private key.
    /// </summary>
    public async Task<ProbeRecord?> AuthenticateProbeAsync(HttpContext context)
    {
        ProbeRecord? probe = null;
        var failure = await VerifyAsync(context, id =>
        {
            probe = probes.Find(id);
            return probe is null ? null : (probe.AgentPublicKey, probe.Revoked);
        });

        return failure is null ? probe : null;
    }

    private async Task<string?> VerifyAsync(HttpContext context, Func<string, (string PublicKey, bool Revoked)?> lookup)
    {
        var nodeId = context.Request.Headers[AgentRequestSignature.NodeHeader].ToString();
        var timestampHeader = context.Request.Headers[AgentRequestSignature.TimestampHeader].ToString();
        var nonce = context.Request.Headers[AgentRequestSignature.NonceHeader].ToString();
        var signature = context.Request.Headers[AgentRequestSignature.SignatureHeader].ToString();

        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(timestampHeader)
            || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
        {
            return "missing signature headers";
        }

        if (!AgentRequestSignature.TryParseTimestamp(timestampHeader, out var timestamp))
        {
            return "invalid timestamp";
        }

        var now = DateTimeOffset.UtcNow;
        if (Abs(now - timestamp) > AgentRequestSignature.MaxClockSkew)
        {
            return "timestamp outside the allowed window";
        }

        PruneNonces(now);
        if (!_seenNonces.TryAdd(nonce, timestamp))
        {
            return "nonce replay";
        }

        if (lookup(nodeId) is not { } registered)
        {
            return "unknown agent";
        }

        if (registered.Revoked)
        {
            logger.LogWarning("Revoked agent {AgentId} attempted to authenticate.", nodeId);
            return "agent revoked";
        }

        var body = await ReadBodyAsync(context);
        var canonical = AgentRequestSignature.BuildCanonicalRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            timestampHeader,
            nonce,
            body);

        using var publicKey = BundleSigning.ImportPublicKey(registered.PublicKey);
        return AgentRequestSignature.Verify(publicKey, canonical, signature) ? null : "signature mismatch";
    }

    /// <summary>Buffers the body so the signature can cover it and the endpoint can still read it.</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var memory = new MemoryStream();
        await context.Request.Body.CopyToAsync(memory);
        context.Request.Body.Position = 0;
        return memory.ToArray();
    }

    private void PruneNonces(DateTimeOffset now)
    {
        foreach (var entry in _seenNonces)
        {
            if (Abs(now - entry.Value) > AgentRequestSignature.MaxClockSkew)
            {
                _seenNonces.TryRemove(entry.Key, out _);
            }
        }
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
