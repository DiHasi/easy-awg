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
public sealed class AgentAuthenticator(NodeRepository nodes, ILogger<AgentAuthenticator> logger)
{
    // Nonces are only remembered for as long as a timestamp stays valid, so this cannot grow
    // without bound.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);

    public async Task<AgentAuthResult> AuthenticateAsync(HttpContext context)
    {
        var nodeId = context.Request.Headers[AgentRequestSignature.NodeHeader].ToString();
        var timestampHeader = context.Request.Headers[AgentRequestSignature.TimestampHeader].ToString();
        var nonce = context.Request.Headers[AgentRequestSignature.NonceHeader].ToString();
        var signature = context.Request.Headers[AgentRequestSignature.SignatureHeader].ToString();

        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(timestampHeader)
            || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
        {
            return AgentAuthResult.Fail("missing signature headers");
        }

        if (!AgentRequestSignature.TryParseTimestamp(timestampHeader, out var timestamp))
        {
            return AgentAuthResult.Fail("invalid timestamp");
        }

        var now = DateTimeOffset.UtcNow;
        if (Abs(now - timestamp) > AgentRequestSignature.MaxClockSkew)
        {
            return AgentAuthResult.Fail("timestamp outside the allowed window");
        }

        PruneNonces(now);
        if (!_seenNonces.TryAdd(nonce, timestamp))
        {
            return AgentAuthResult.Fail("nonce replay");
        }

        var node = nodes.Find(nodeId);
        if (node is null)
        {
            return AgentAuthResult.Fail("unknown node");
        }

        if (node.Revoked)
        {
            logger.LogWarning("Revoked node {NodeId} attempted to authenticate.", nodeId);
            return AgentAuthResult.Fail("node revoked");
        }

        var body = await ReadBodyAsync(context);
        var canonical = AgentRequestSignature.BuildCanonicalRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            timestampHeader,
            nonce,
            body);

        using var publicKey = BundleSigning.ImportPublicKey(node.AgentPublicKey);
        return AgentRequestSignature.Verify(publicKey, canonical, signature)
            ? AgentAuthResult.Ok(node)
            : AgentAuthResult.Fail("signature mismatch");
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
