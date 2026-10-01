using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>
/// Automatic failover: whether it is armed, what it last decided, and a way to make it look now.
/// The switch itself is still <c>POST /nodes/{id}/activate</c>; this only decides who presses it.
/// </summary>
public static class FailoverApi
{
    public static void MapFailover(this RouteGroupBuilder admin)
    {
        admin.MapGet("/failover", (FleetRepository fleet, FailoverMonitor monitor, IDnsRecordUpdater updater, Notifier notifier, ControlOptions options)
            => TypedResults.Ok(Describe(fleet, monitor, updater, notifier, options)));

        admin.MapPut("/failover", async Task<IResult> (
            UpdateFailoverRequest request,
            FleetRepository fleet,
            FailoverMonitor monitor,
            DnsFailoverService failover,
            IDnsRecordUpdater updater,
            Notifier notifier,
            ControlOptions options,
            EventLog events,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var mode = request.Mode?.Trim().ToLowerInvariant();
            if (!FailoverModes.IsKnown(mode))
            {
                return Results.BadRequest(new ApiError("failover_mode_invalid", "Mode must be 'manual' or 'automatic'."));
            }

            if (mode == FailoverModes.Automatic)
            {
                // Automatic failover with nobody to edit the record would only ever mark a node
                // active that clients are not sent to - the one lie the active flag must not tell.
                if (!updater.IsConfigured)
                {
                    return Results.BadRequest(new ApiError(
                        "failover_requires_dns_provider",
                        "Automatic failover moves the DNS record itself, so it needs a DNS provider. Set AWG_CLOUDFLARE_API_TOKEN "
                        + "and AWG_CLOUDFLARE_ZONE_ID on the panel first."));
                }

                // Checked now, while someone is looking, rather than discovered at the moment a
                // node dies and the switch is refused.
                if (await updater.CheckAsync(failover.RecordName, cancellationToken) is { } problem)
                {
                    return Results.BadRequest(problem);
                }
            }

            fleet.SetFailoverMode(mode!, DateTimeOffset.UtcNow);
            events.Record(
                "failover.mode",
                mode == FailoverModes.Automatic
                    ? $"Automatic failover armed: the panel moves {failover.RecordName} once the active node has failed for {FailoverPolicy.Describe(options.Failover.Grace)}."
                    : "Automatic failover disarmed: switching is manual.",
                actor: context.User.Identity?.Name);

            return Results.Ok(Describe(fleet, monitor, updater, notifier, options));
        });

        // Runs a round now instead of at the next tick. What an operator presses after fixing
        // something, and what lets the test suite drive the monitor without waiting on a timer.
        admin.MapPost("/failover/evaluate", async (
            FleetRepository fleet,
            FailoverMonitor monitor,
            IDnsRecordUpdater updater,
            Notifier notifier,
            ControlOptions options,
            CancellationToken cancellationToken) =>
        {
            await monitor.EvaluateOnceAsync(cancellationToken);
            return TypedResults.Ok(Describe(fleet, monitor, updater, notifier, options));
        });

        admin.MapPost("/failover/test-notification", async Task<IResult> (Notifier notifier, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!notifier.IsConfigured)
            {
                return Results.BadRequest(new ApiError(
                    "notifications_not_configured",
                    "No notification channel is configured. Set AWG_NOTIFY_WEBHOOK_URL, or AWG_NOTIFY_TELEGRAM_BOT_TOKEN "
                    + "and AWG_NOTIFY_TELEGRAM_CHAT_ID, on the panel."));
            }

            await notifier.SendAsync(
                new Notification(
                    "test",
                    "Test notification",
                    $"Sent from the panel by {context.User.Identity?.Name ?? "an admin"}. Failover alerts will arrive here.",
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            return Results.NoContent();
        });
    }

    private static FailoverStatusResponse Describe(
        FleetRepository fleet,
        FailoverMonitor monitor,
        IDnsRecordUpdater updater,
        Notifier notifier,
        ControlOptions options)
    {
        var round = monitor.LastRound;
        var settings = options.Failover;

        return new FailoverStatusResponse(
            fleet.FindFailoverMode(),
            updater.ProviderName,
            updater.IsConfigured,
            Seconds(settings.CheckInterval),
            Seconds(settings.Grace),
            Seconds(settings.Cooldown),
            Seconds(settings.AgentStaleAfter),
            Seconds(settings.ProbeStaleAfter),
            notifier.ChannelNames,
            round?.At,
            round?.Decision.Action.ToString().ToLowerInvariant(),
            round?.Decision.Message,
            round?.Decision.ActiveNodeId,
            round?.Decision.TargetNodeId);
    }

    private static int Seconds(TimeSpan span) => (int)span.TotalSeconds;
}
