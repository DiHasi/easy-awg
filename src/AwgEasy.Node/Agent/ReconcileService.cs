using System.Text.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// The agent's control loop: converge this node onto the desired state, then report what
/// actually happened.
///
/// Order matters on startup. The cached bundle is applied *before* the control plane is ever
/// contacted, so a node reboot during a control-plane outage still brings the tunnel back up.
/// </summary>
public sealed class ReconcileService(
    NodeOptions options,
    AgentIdentityStore identityStore,
    BundleStore bundleStore,
    BundleAcceptor acceptor,
    AwgInterface awgInterface,
    AwgRuntime runtime,
    ControlPlaneClient controlPlane,
    NodeHealth health,
    ILogger<ReconcileService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = identityStore.LoadOrCreate();

        await ApplyLocalBundleFileAsync(identity, stoppingToken);
        await ApplyCachedBundleAsync(identity, stoppingToken);

        if (options.IsOffline)
        {
            logger.LogInformation("No AWG_CONTROL_URL configured. Running offline: the cached bundle stays in effect.");
            return;
        }

        await EnsureEnrolledAsync(identity, stoppingToken);

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(identity, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never let a transient control-plane problem kill the loop: the tunnel is already
                // up and must stay up.
                health.RecordError(exception.Message);
                logger.LogWarning(exception, "Reconcile cycle failed. Retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Bootstrap path: a bundle handed to the node directly, without a control plane.</summary>
    private async Task ApplyLocalBundleFileAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BundleFile))
        {
            return;
        }

        if (!File.Exists(options.BundleFile))
        {
            logger.LogError("AWG_BUNDLE_FILE points at {Path}, which does not exist.", options.BundleFile);
            return;
        }

        var envelope = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(options.BundleFile, cancellationToken),
            NodeJsonContext.Default.SignedBundle);

        if (envelope is not null && await TryApplyAsync(envelope, identity, cancellationToken))
        {
            bundleStore.Save(envelope);
        }
    }

    private async Task ApplyCachedBundleAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        var cached = bundleStore.Load();
        if (cached is null)
        {
            logger.LogInformation("No cached bundle yet. Waiting for the control plane.");
            return;
        }

        logger.LogInformation("Applying cached bundle before contacting the control plane.");
        await TryApplyAsync(cached, identity, cancellationToken);
    }

    private async Task EnsureEnrolledAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (identity.IsEnrolled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
        {
            logger.LogError("Node is not enrolled and AWG_ENROLLMENT_TOKEN is not set. Cannot reach the control plane.");
            return;
        }

        var response = await controlPlane.EnrollAsync(identity, options.EnrollmentToken, cancellationToken);
        if (response is null)
        {
            return;
        }

        identity.NodeId = response.NodeId;
        identity.ControlSigningPublicKey = response.ControlSigningPublicKey;
        identity.ControlSigningKeyId = response.ControlSigningKeyId;
        identityStore.Save(identity);

        logger.LogInformation("Enrolled as node {NodeId}; pinned control plane signing key {KeyId}.", response.NodeId, response.ControlSigningKeyId);
    }

    private async Task PollOnceAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (!identity.IsEnrolled)
        {
            await EnsureEnrolledAsync(identity, cancellationToken);
            if (!identity.IsEnrolled)
            {
                return;
            }
        }

        var fetch = await controlPlane.FetchBundleAsync(identity, cancellationToken);
        if (fetch.Outcome == FetchOutcome.IdentityRejected)
        {
            await HandleIdentityRejectedAsync(identity, cancellationToken);
            return;
        }

        if (fetch.Bundle is not null && await TryApplyAsync(fetch.Bundle, identity, cancellationToken))
        {
            bundleStore.Save(fetch.Bundle);
        }

        await ReportAsync(identity, cancellationToken);
    }

    /// <summary>
    /// The control plane no longer knows this node: it was revoked, or removed and forgotten.
    /// Retrying the same identity forever is pointless, so either adopt a fresh one or say
    /// plainly what an operator has to do.
    ///
    /// The tunnel is untouched throughout. A node that has lost management still carries traffic.
    /// </summary>
    private async Task HandleIdentityRejectedAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        health.RecordError("Control plane rejected this node's identity.");

        if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
        {
            logger.LogError(
                "The control plane rejected node {NodeId}: it was revoked or removed. The tunnel keeps "
                + "running on its cached configuration, but this node will not receive updates. Issue a "
                + "fresh enrollment token in the panel and re-run the installer with it.",
                identity.NodeId);
            return;
        }

        // Possession of an enrollment token is the authorization to adopt this server, so a stale
        // node id must not stand in the way. The agent keeps its key pair; only the registration
        // is redone.
        logger.LogWarning(
            "Node {NodeId} was rejected by the control plane. An enrollment token is configured, so re-enrolling.",
            identity.NodeId);

        identity.NodeId = null;
        identity.ControlSigningPublicKey = null;
        identity.ControlSigningKeyId = null;
        identity.AppliedRevision = 0;
        identityStore.Save(identity);

        await EnsureEnrolledAsync(identity, cancellationToken);
    }

    private async Task<bool> TryApplyAsync(SignedBundle envelope, AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        if (!acceptor.TryAccept(envelope, identity, DateTimeOffset.UtcNow, out var bundle, out var error))
        {
            health.RecordError($"{error.Code}: {error.Message}");
            return false;
        }

        await awgInterface.ApplyAsync(bundle, cancellationToken);

        identity.AppliedRevision = bundle.Revision;
        identityStore.Save(identity);
        health.RecordApplied(bundle.Revision);
        return true;
    }

    private async Task ReportAsync(AgentIdentityDocument identity, CancellationToken cancellationToken)
    {
        var peers = await runtime.GetPeerStatusAsync(cancellationToken);
        var status = await runtime.GetStatusAsync(cancellationToken);
        health.RecordInterface(status.IsRunning, status.Backend);

        var report = new NodeStatusReport(
            identity.AppliedRevision,
            status.IsRunning,
            status.Backend,
            AgentVersion.Current,
            DateTimeOffset.UtcNow,
            peers,
            Metrics: null,
            health.LastError);

        await controlPlane.ReportStatusAsync(identity, report, cancellationToken);
    }
}
