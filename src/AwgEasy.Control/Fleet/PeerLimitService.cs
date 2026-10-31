namespace AwgEasy.Control;

/// <summary>What one sweep did, so the API can run one on demand and say so.</summary>
public sealed record PeerLimitSweep(int Expired, int Exhausted, int Renewed, DateTimeOffset At);

/// <summary>
/// Switches peers off when they run out of time or allowance, and puts the monthly ones back.
///
/// On a clock of its own rather than on the status-report path, for the same reason the history is
/// pruned on one: a node reports every twenty seconds, and the alternative is a disable, an event,
/// a notification and a revision bump sitting in front of every one of those reports from every
/// node in the fleet. What that costs is precision, and the right amount of it: a peer can carry
/// traffic for up to a minute after its allowance is gone. A quota is a billing instrument, not a
/// packet filter - the node enforces nothing and was never asked to - so a minute of overshoot
/// against an interval the nodes themselves report in at twenty seconds is as exact as the
/// evidence allows anybody to be.
///
/// Only the panel in charge sweeps. A standby refuses every write, and two panels switching the
/// same peers off would each bump the revision for it.
///
/// The decision itself is <see cref="PeerLimitPolicy"/>'s and not this loop's, so the same answer
/// is given to the sweep, to an operator trying to re-enable an expired peer, and to the peer list
/// drawing words beside somebody's name.
/// </summary>
public sealed class PeerLimitService(
    ClientRepository clients,
    FleetService fleet,
    PanelContext panel,
    Notifier notifier,
    EventLog events,
    ILogger<PeerLimitService> logger) : BackgroundService
{
    /// <summary>
    /// How often the limits are read. A constant rather than a setting: it is not a judgement an
    /// operator has better information about, and a value somebody shortened to make a quota
    /// "tighter" would buy seconds of precision against a node that only reports every twenty of
    /// them, at the price of a disable, an event and a revision bump arriving in bursts.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public PeerLimitSweep? Last { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep means a peer stays on for another minute, which is never a
                // reason to take the loop - or the panel - down with it.
                logger.LogWarning(exception, "Could not apply the peer limits.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One pass over the peers that have a limit. Public so a test - and the operator, through the
    /// API - can run exactly what the loop runs rather than something shaped like it.
    /// </summary>
    public async Task<PeerLimitSweep> SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!panel.IsActive)
        {
            // Nothing to do and nothing to say: a standby's copy of the fleet is the active's, and
            // the active is the one sweeping it.
            return Last = new PeerLimitSweep(0, 0, 0, now);
        }

        var expired = new List<string>();
        var exhausted = new List<string>();
        var renewed = new List<string>();

        foreach (var client in clients.ListLimited())
        {
            var limits = client.Limits ?? PeerLimits.None;
            switch (PeerLimitPolicy.Evaluate(limits, client.Enabled, now))
            {
                case PeerLimitAction.Expire when clients.Disable(client.Id, PeerDisableReasons.Expiry, now):
                    expired.Add(client.Name);
                    events.Record("client.expired", $"Client {client.Name} reached its expiry and has been disabled.");
                    break;

                case PeerLimitAction.Exhaust when clients.Disable(client.Id, PeerDisableReasons.Quota, now):
                    exhausted.Add(client.Name);
                    events.Record(
                        "client.quota_exhausted",
                        $"Client {client.Name} used its allowance of {Bytes(limits.QuotaBytes)} and has been disabled.");
                    break;

                case PeerLimitAction.Renew:
                    // The allowance starts again whether or not the peer is still on, and the
                    // peers the quota itself switched off come back. One event per peer either
                    // way: an operator reading the log in the first minute of a month wants to
                    // see whose service resumed.
                    if (clients.RenewMonthlyQuota(client.Id, now).Count > 0)
                    {
                        renewed.Add(client.Name);
                        events.Record("client.quota_renewed", $"Client {client.Name} has a new monthly allowance and is enabled again.");
                    }
                    else
                    {
                        events.Record("client.quota_renewed", $"Client {client.Name} has a new monthly allowance.");
                    }

                    break;
            }
        }

        var changed = expired.Count + exhausted.Count + renewed.Count;
        if (changed > 0)
        {
            // Once for the whole sweep rather than once per peer: every one of these is a peer
            // appearing in or disappearing from the bundle, and a fleet of nodes does not need to
            // re-apply the same change five times because five allowances ran out in the same
            // minute.
            fleet.BumpRevision($"{changed} peer(s) changed by their limits");
            await AnnounceAsync(expired, exhausted, renewed, now, cancellationToken);
        }

        return Last = new PeerLimitSweep(expired.Count, exhausted.Count, renewed.Count, now);
    }

    /// <summary>
    /// One message for the sweep, because the person who gets it is the person who will be asked
    /// why somebody's VPN stopped working - and they would rather be told once that three configs
    /// ran out than three times that one did.
    /// </summary>
    private async Task AnnounceAsync(
        List<string> expired,
        List<string> exhausted,
        List<string> renewed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!notifier.IsConfigured || (expired.Count == 0 && exhausted.Count == 0))
        {
            // A renewal on its own is not news: it is the month starting, which the operator
            // already knows about, and the peers it put back were never anybody's problem.
            return;
        }

        var lines = new List<string>();
        if (expired.Count > 0)
        {
            lines.Add($"Expired: {string.Join(", ", expired)}.");
        }

        if (exhausted.Count > 0)
        {
            lines.Add($"Out of allowance: {string.Join(", ", exhausted)}.");
        }

        if (renewed.Count > 0)
        {
            lines.Add($"New monthly allowance: {string.Join(", ", renewed)}.");
        }

        await notifier.SendAsync(
            new Notification(
                "client.limits_applied",
                $"{expired.Count + exhausted.Count} config(s) switched off by their limits",
                string.Join(" ", lines)
                + " Extend the expiry or reset the allowance in the panel to put one back - nothing else has changed.",
                null,
                now),
            cancellationToken);
    }

    /// <summary>Allowances are set in whole gigabytes by any operator who ever types one, so the
    /// log reads in them too rather than in eleven digits.</summary>
    private static string Bytes(long? bytes)
        => bytes is null ? "none" : $"{bytes.Value / 1024d / 1024 / 1024:0.##} GB";
}
