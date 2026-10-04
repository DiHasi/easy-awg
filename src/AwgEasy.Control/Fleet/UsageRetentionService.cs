namespace AwgEasy.Control;

/// <summary>
/// Keeps the traffic history inside its retention window.
///
/// It runs on a clock of its own rather than on the status reports that write the rows: pruning
/// on the write path would put a delete in front of every report from every node, twenty seconds
/// apart, to drop rows whose age changes by the hour. The first pass is immediate, so an operator
/// who shortens retention - or sets it to zero - sees the history actually shrink rather than
/// being told it will, eventually.
/// </summary>
public sealed class UsageRetentionService(
    UsageRepository usage,
    ControlOptions options,
    ILogger<UsageRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                // Zero retention drops everything and keeps it dropped: switching recording off has
                // to clear what was recorded while it was on, or the switch only hides it.
                var dropped = usage.Prune(options.Usage.Records
                    ? DateTimeOffset.UtcNow.AddDays(-options.Usage.RetentionDays)
                    : DateTimeOffset.MaxValue);

                if (dropped > 0)
                {
                    logger.LogInformation(
                        "Pruned {Rows} traffic rows outside the {Days}-day retention window.",
                        dropped,
                        options.Usage.RetentionDays);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed prune is a disk-space problem at worst, never a reason to stop the panel.
                logger.LogWarning(exception, "Could not prune the traffic history.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
