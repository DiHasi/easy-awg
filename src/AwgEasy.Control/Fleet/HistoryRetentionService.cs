namespace AwgEasy.Control;

/// <summary>
/// Keeps the panel's two histories inside their retention windows: what each person moved, and how
/// each node stood.
///
/// One timer for both, on a clock of its own rather than on the writes. Pruning on the write path
/// would put a delete in front of every status report from every node, twenty seconds apart, to
/// drop rows whose age changes by the hour. The first pass is immediate, so an operator who
/// shortens retention - or sets it to zero - sees the history actually shrink rather than being
/// told it will, eventually.
///
/// The two windows are read separately on purpose: the traffic history is a record about people
/// and the availability history is a record about the operator's own servers, so switching one off
/// says nothing about the other.
/// </summary>
public sealed class HistoryRetentionService(
    UsageRepository usage,
    AvailabilityRepository availability,
    ControlOptions options,
    ILogger<HistoryRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            // Zero retention drops everything and keeps it dropped: switching recording off has to
            // clear what was recorded while it was on, or the switch only hides it.
            Prune("traffic", options.Usage.Records, options.Usage.RetentionDays, usage.Prune);
            Prune("availability", options.Availability.Records, options.Availability.RetentionDays, availability.Prune);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Prune(string what, bool records, int retentionDays, Func<DateTimeOffset, int> prune)
    {
        try
        {
            var dropped = prune(records ? DateTimeOffset.UtcNow.AddDays(-retentionDays) : DateTimeOffset.MaxValue);
            if (dropped > 0)
            {
                logger.LogInformation(
                    "Pruned {Rows} {What} rows outside the {Days}-day retention window.",
                    dropped,
                    what,
                    retentionDays);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed prune is a disk-space problem at worst, never a reason to stop the panel.
            logger.LogWarning(exception, "Could not prune the {What} history.", what);
        }
    }
}
