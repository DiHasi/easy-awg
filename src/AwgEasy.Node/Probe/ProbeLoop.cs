using AwgEasy.Contracts;

namespace AwgEasy.Node;

public sealed record ProbeHealthResponse(
    string Status,
    string? ProbeId,
    DateTimeOffset? LastRoundAt,
    int Targets,
    int Reachable,
    string? LastError,
    string AgentVersion);

/// <summary>What the probe last did, for its loopback health endpoint.</summary>
public sealed class ProbeStatus
{
    private readonly object _lock = new();
    private ProbeHealthResponse _snapshot = new("starting", null, null, 0, 0, null, AgentVersion.Current);

    public void RecordRound(string probeId, ProbeResult[] results)
    {
        lock (_lock)
        {
            _snapshot = new ProbeHealthResponse(
                "running",
                probeId,
                DateTimeOffset.UtcNow,
                results.Length,
                results.Count(result => result.Outcome == ProbeOutcomes.Reachable),
                null,
                AgentVersion.Current);
        }
    }

    public void RecordError(string message)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with { Status = "degraded", LastError = message };
        }
    }

    public ProbeHealthResponse Snapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }
}

/// <summary>
/// The probe's loop: fetch what to check, check each node in turn, report, wait.
///
/// It has nothing to fail static about - it serves no traffic - so every failure here simply
/// costs a round. What it must not do is report a failure of its own as a failure of a node, which
/// is why a round that could not even fetch its assignment reports nothing at all.
/// </summary>
public sealed class ProbeLoop(
    NodeOptions options,
    AgentIdentityStore identityStore,
    ControlPlaneClient controlPlane,
    HandshakeProbe probe,
    ProbeStatus status,
    ILogger<ProbeLoop> logger) : BackgroundService
{
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.IsOffline)
        {
            logger.LogError("AWG_ROLE=probe needs AWG_CONTROL_URL: a probe reports to the panel and has nothing to do without it.");
            return;
        }

        var identity = identityStore.LoadOrCreate();

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = FallbackInterval;
            try
            {
                wait = await RunRoundAsync(identity, stoppingToken) ?? FallbackInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                status.RecordError(exception.Message);
                logger.LogWarning(exception, "Probe round failed. Retrying at the next interval.");
            }

            await Task.Delay(wait, stoppingToken);
        }
    }

    /// <summary>Runs one round and returns how long the panel wants the probe to wait before the next.</summary>
    private async Task<TimeSpan?> RunRoundAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (!identity.IsEnrolled && !await EnrollAsync(identity, cancellationToken))
        {
            return null;
        }

        var (outcome, assignment) = await controlPlane.FetchAssignmentAsync(identity, cancellationToken);
        if (outcome == FetchOutcome.IdentityRejected)
        {
            status.RecordError("The panel rejected this probe's identity.");
            logger.LogError(
                "The panel rejected probe {ProbeId}: it was revoked or removed. Issue a new probe token and restart the container with it.",
                identity.NodeId);

            if (!string.IsNullOrWhiteSpace(options.EnrollmentToken))
            {
                identity.NodeId = null;
                identity.ControlSigningPublicKey = null;
                identity.ControlSigningKeyId = null;
                identityStore.Save(identity);
            }

            return null;
        }

        if (assignment is null)
        {
            status.RecordError("Could not fetch the assignment from the panel.");
            return null;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(assignment.HandshakeTimeoutSeconds, 3, 60));
        var trafficTimeout = TimeSpan.FromSeconds(Math.Clamp(assignment.TrafficTimeoutSeconds, 3, 60));
        var checkUrls = assignment.CheckUrls ?? [];

        // One at a time: they share one interface name, and checking nodes in parallel from one
        // host would measure the host's uplink as much as the nodes.
        var results = new List<ProbeResult>(assignment.Targets.Length);
        foreach (var target in assignment.Targets)
        {
            var result = await probe.CheckAsync(target, timeout, checkUrls, trafficTimeout, cancellationToken);
            logger.LogInformation(
                "{Address}:{Port} {Outcome}{Detail}",
                target.Address,
                target.Port,
                result.Outcome,
                result.LatencyMs is { } latency ? $" in {latency} ms" : result.Detail is null ? string.Empty : $" - {result.Detail}");
            results.Add(result);
        }

        var report = new ProbeReport(AgentVersion.Current, DateTimeOffset.UtcNow, [.. results]);
        await controlPlane.ReportProbeAsync(identity, report, cancellationToken);
        status.RecordRound(assignment.ProbeId, report.Results);

        return TimeSpan.FromSeconds(Math.Clamp(assignment.IntervalSeconds, 10, 3600));
    }

    private async Task<bool> EnrollAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
        {
            status.RecordError("Not enrolled and AWG_ENROLLMENT_TOKEN is not set.");
            logger.LogError("Probe is not enrolled and AWG_ENROLLMENT_TOKEN is not set. Issue a probe token in the panel.");
            return false;
        }

        var response = await controlPlane.EnrollProbeAsync(identity, options.EnrollmentToken, cancellationToken);
        if (response is null)
        {
            return false;
        }

        identity.NodeId = response.NodeId;
        identity.ControlSigningPublicKey = response.ControlSigningPublicKey;
        identity.ControlSigningKeyId = response.ControlSigningKeyId;
        identityStore.Save(identity);

        logger.LogInformation("Enrolled as probe {ProbeId}.", response.NodeId);
        return true;
    }
}
