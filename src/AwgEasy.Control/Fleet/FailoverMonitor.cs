namespace AwgEasy.Control;

/// <summary>One evaluation of the fleet: what every node looked like and what was done about it.</summary>
public sealed record FailoverRound(
    DateTimeOffset At,
    string Mode,
    FailoverDecision Decision,
    IReadOnlyDictionary<string, NodeAssessment> Health);

/// <summary>
/// Phase 2: the judgement the operator used to supply. Every check interval it assesses each node,
/// asks <see cref="FailoverPolicy"/> whether the record should move, and - when automatic failover
/// is armed - moves it through <see cref="DnsFailoverService.ActivateAsync"/>, the very call the
/// "Make active" button makes. Nothing about switching itself changed; only who decides.
///
/// In manual mode it still watches: a node that gets blocked, or an active node that a standby
/// could replace, is worth a notification even when nobody has handed the panel the wheel.
/// </summary>
public sealed class FailoverMonitor(
    IServiceScopeFactory scopes,
    FleetRepository fleetRepository,
    NodeRepository nodes,
    ProbeRepository probes,
    FleetService fleet,
    ControlOptions options,
    Notifier notifier,
    EventLog events,
    ILogger<FailoverMonitor> logger) : BackgroundService
{
    /// <summary>What the event log and the DNS provider see as the one who switched.</summary>
    public const string Actor = "automatic failover";

    private readonly SemaphoreSlim _gate = new(1, 1);

    // What each node looked like last round, so only changes are reported. Null until the first
    // round, which records the fleet as it finds it: a panel restart is not news.
    private Dictionary<string, string>? _states;

    // The last fleet-level alert, so a stuck or refused switch is announced once rather than every
    // round for as long as it lasts.
    private string? _lastAlert;

    public FailoverRound? LastRound { get; private set; }

    public IReadOnlyDictionary<string, NodeAssessment> Assess(IReadOnlyList<NodeRecord> fleetNodes, DateTimeOffset now)
        => NodeHealthEvaluator.AssessAll(fleetNodes, probes.Observations(), now, options.Failover);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Failover.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A round that throws is a round skipped, never a monitor stopped: the next node
                // failure is exactly when this has to still be running.
                logger.LogError(exception, "Failover evaluation failed. Retrying at the next interval.");
            }
        }
    }

    public async Task<FailoverRound> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var fleetNodes = nodes.List();
            var health = Assess(fleetNodes, now);

            await ReportTransitionsAsync(fleetNodes, health, now, cancellationToken);

            var current = fleet.Current;
            var (activeNodeId, activeSince) = fleetRepository.FindActiveNode();
            var mode = fleetRepository.FindFailoverMode();

            var decision = FailoverPolicy.Decide(
                activeNodeId,
                activeSince,
                fleetNodes,
                health,
                current.Revision,
                current.Obfuscation?.UsesSchema3Features == true,
                now,
                options.Failover);

            decision = await ActAsync(decision, mode, fleetNodes, cancellationToken);

            LastRound = new FailoverRound(now, mode, decision, health);
            return LastRound;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FailoverDecision> ActAsync(
        FailoverDecision decision,
        string mode,
        IReadOnlyList<NodeRecord> fleetNodes,
        CancellationToken cancellationToken)
    {
        switch (decision.Action)
        {
            case FailoverAction.Switch when mode != FailoverModes.Automatic:
            {
                var target = Name(fleetNodes, decision.TargetNodeId);
                var advice = decision with
                {
                    Action = FailoverAction.Recommend,
                    Message = $"{decision.Message} Automatic failover is off: make {target} active to move clients."
                };
                await AlertOnceAsync("failover.recommended", "Switch recommended", advice, cancellationToken);
                return advice;
            }

            case FailoverAction.Switch:
            {
                using var scope = scopes.CreateScope();
                var failover = scope.ServiceProvider.GetRequiredService<DnsFailoverService>();
                var (_, error) = await failover.ActivateAsync(decision.TargetNodeId!, Actor, cancellationToken);

                if (error is not null)
                {
                    // The record did not move, so the active node did not either; the next round
                    // tries again. Announced once, not every round the provider keeps refusing.
                    var refused = decision with
                    {
                        Action = FailoverAction.Failed,
                        Message = $"Tried to move clients to {Name(fleetNodes, decision.TargetNodeId)} and the switch failed: {error.Message}"
                    };
                    logger.LogError("Automatic failover could not switch: {Code} - {Message}", error.Code, error.Message);
                    events.Record("failover.failed", refused.Message, actor: Actor, nodeId: decision.TargetNodeId);
                    await AlertOnceAsync("failover.failed", "Failover could not switch", refused, cancellationToken);
                    return refused;
                }

                logger.LogWarning("Automatic failover: {Message}", decision.Message);
                events.Record("failover.switched", decision.Message, actor: Actor, nodeId: decision.TargetNodeId);
                _lastAlert = null;
                await SendAsync(new Notification("failover.switched", "Failover: clients moved", decision.Message, decision.TargetNodeId, DateTimeOffset.UtcNow), cancellationToken);
                return decision;
            }

            case FailoverAction.Stuck:
                await AlertOnceAsync("failover.stuck", "Active node failing, nowhere to go", decision, cancellationToken);
                return decision;

            case FailoverAction.None:
                // Whatever was wrong is over, so the next time it goes wrong is news again.
                _lastAlert = null;
                return decision;

            default:
                return decision;
        }
    }

    private async Task AlertOnceAsync(string kind, string title, FailoverDecision decision, CancellationToken cancellationToken)
    {
        var key = $"{kind}:{decision.ActiveNodeId}:{decision.TargetNodeId}";
        if (key == _lastAlert)
        {
            return;
        }

        _lastAlert = key;
        if (kind != "failover.failed")
        {
            events.Record(kind, decision.Message, actor: Actor, nodeId: decision.ActiveNodeId);
        }

        await SendAsync(new Notification(kind, title, decision.Message, decision.ActiveNodeId, DateTimeOffset.UtcNow), cancellationToken);
    }

    /// <summary>
    /// Records and announces nodes that start or stop failing. Every node, not only the active
    /// one: a standby that got blocked is the node failover would have sent everyone to next.
    /// </summary>
    private async Task ReportTransitionsAsync(
        IReadOnlyList<NodeRecord> fleetNodes,
        IReadOnlyDictionary<string, NodeAssessment> health,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_states is null)
        {
            _states = health.ToDictionary(entry => entry.Key, entry => entry.Value.State, StringComparer.Ordinal);
            return;
        }

        foreach (var node in fleetNodes)
        {
            var assessment = health[node.Id];
            var previous = _states.GetValueOrDefault(node.Id);
            _states[node.Id] = assessment.State;

            if (previous == assessment.State)
            {
                continue;
            }

            if (assessment.IsFailing)
            {
                var message = $"Node '{node.Name}' is {assessment.State}: {assessment.Reason}";
                events.Record($"node.{assessment.State}", message, nodeId: node.Id);
                await SendAsync(new Notification($"node.{assessment.State}", $"{node.Name} is {assessment.State}", message, node.Id, now), cancellationToken);
            }
            else if (previous is not null && NodeHealthStates.IsFailing(previous) && assessment.State == NodeHealthStates.Healthy)
            {
                var message = $"Node '{node.Name}' recovered: {assessment.Reason}";
                events.Record("node.recovered", message, nodeId: node.Id);
                await SendAsync(new Notification("node.recovered", $"{node.Name} recovered", message, node.Id, now), cancellationToken);
            }
        }

        foreach (var gone in _states.Keys.Where(id => !health.ContainsKey(id)).ToArray())
        {
            _states.Remove(gone);
        }
    }

    private Task SendAsync(Notification notification, CancellationToken cancellationToken)
        => notifier.IsConfigured ? notifier.SendAsync(notification, cancellationToken) : Task.CompletedTask;

    private static string Name(IReadOnlyList<NodeRecord> fleetNodes, string? nodeId)
        => fleetNodes.FirstOrDefault(node => node.Id == nodeId)?.Name ?? nodeId ?? "another node";
}
