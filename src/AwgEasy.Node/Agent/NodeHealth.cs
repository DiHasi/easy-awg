namespace AwgEasy.Node;

public sealed record NodeHealthResponse(
    string Status,
    long AppliedRevision,
    bool InterfaceUp,
    string? Backend,
    DateTimeOffset? LastAppliedAt,
    string? LastError,
    string AgentVersion);

/// <summary>
/// In-memory view of what the agent is currently doing, exposed on a loopback-bound endpoint.
///
/// Useful for local debugging, and later as the target for a dumb always-on health check at the
/// DNS provider - the layer that keeps failover working even while the control plane is down.
/// </summary>
public sealed class NodeHealth
{
    private readonly object _lock = new();

    public long AppliedRevision { get; private set; }

    public bool InterfaceUp { get; private set; }

    public string? Backend { get; private set; }

    public DateTimeOffset? LastAppliedAt { get; private set; }

    public string? LastError { get; private set; }

    public void RecordApplied(long revision)
    {
        lock (_lock)
        {
            AppliedRevision = revision;
            LastAppliedAt = DateTimeOffset.UtcNow;
            LastError = null;
        }
    }

    public void RecordInterface(bool isUp, string? backend)
    {
        lock (_lock)
        {
            InterfaceUp = isUp;
            Backend = backend;
        }
    }

    public void RecordError(string message)
    {
        lock (_lock)
        {
            LastError = message;
        }
    }

    public NodeHealthResponse Snapshot()
    {
        lock (_lock)
        {
            // Healthy means the tunnel is up. A control-plane error while the tunnel serves
            // traffic is degraded, not down - that distinction is the whole point of fail-static.
            var status = InterfaceUp
                ? LastError is null ? "healthy" : "degraded"
                : "down";

            return new NodeHealthResponse(
                status,
                AppliedRevision,
                InterfaceUp,
                Backend,
                LastAppliedAt,
                LastError,
                AgentVersion.Current);
        }
    }
}
