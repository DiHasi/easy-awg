using System.Globalization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <summary>How wide one point of a series is. Both histories are stored hourly; a day is folded
/// out of the hours once the window is too long to read as hours.</summary>
public enum HistoryGrain
{
    Hour,
    Day
}

/// <param name="Span">How far back the window reaches.</param>
/// <param name="Grain">How wide one point is. Hours past a week stop being readable as a shape.</param>
public sealed record HistoryWindow(string Key, TimeSpan Span, HistoryGrain Grain);

/// <summary>
/// The windows a history is read over, and the arithmetic that turns one into a range of buckets.
///
/// Shared by the traffic history and the availability history because they answer the same shape
/// of question - what happened over the last day, week or month - and two sets of windows that
/// drifted apart would make the same button mean different spans on two pages.
///
/// The set is fixed rather than a free from/to pair, and the bucket width follows from the window
/// rather than from the caller: that is what keeps a request for ninety days of hourly points from
/// existing, and keeps every answer a few hundred numbers whatever the size of the fleet.
/// </summary>
public static class HistoryWindows
{
    public static readonly HistoryWindow[] All =
    [
        new("24h", TimeSpan.FromHours(24), HistoryGrain.Hour),
        new("7d", TimeSpan.FromDays(7), HistoryGrain.Hour),
        new("30d", TimeSpan.FromDays(30), HistoryGrain.Day),
        new("90d", TimeSpan.FromDays(90), HistoryGrain.Day)
    ];

    /// <summary>
    /// The day in front of the operator, which is what they came to the page to read. A request
    /// without a window answers the same thing the panel opens on, so the two cannot disagree.
    /// </summary>
    public static HistoryWindow Default => All[0];

    /// <summary>Plus or minus 14h, the span of real civil offsets. Beyond it the caller is not naming a place.</summary>
    public const int OffsetLimit = 14 * 60;

    /// <param name="subject">
    /// What the error code is about, so each page keeps the code it has always answered with:
    /// error codes are part of the API and stable, and sharing the arithmetic must not rename them.
    /// </param>
    public static bool TryRead(string? key, string subject, out HistoryWindow window, out ApiError error)
    {
        window = Default;
        error = default!;

        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        if (All.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            error = new ApiError(
                $"{subject}_window_invalid",
                $"Window must be one of {string.Join(", ", All.Select(candidate => candidate.Key))}.");

            return false;
        }

        window = found;
        return true;
    }

    /// <summary>
    /// The reader's offset east of UTC, in minutes - the ISO reading of a zone, so +03:00 is 180.
    /// Omitted means UTC, which is what a caller that is not a browser gets. Whole minutes only,
    /// because :45 zones are real.
    /// </summary>
    public static bool TryReadOffset(int? offset, string subject, out int minutes, out ApiError error)
    {
        minutes = offset ?? 0;
        error = default!;

        if (minutes is < -OffsetLimit or > OffsetLimit)
        {
            error = new ApiError($"{subject}_offset_invalid", $"Offset must be between -{OffsetLimit} and {OffsetLimit} minutes.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The window, aligned to its own bucket width. The hour - or the day - in progress is the last
    /// bucket and is deliberately included: it is partial, and leaving it out would mean the page
    /// never shows what is happening right now.
    ///
    /// A day is aligned in the reader's frame, not in UTC, so "yesterday" on the page is the day
    /// they actually lived through. An hour is not: the stored rows sit on UTC hour boundaries, so
    /// there is nothing finer to align to and the instant is already correct - only the label it is
    /// drawn with is local.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(HistoryWindow window, DateTimeOffset now, int offsetMinutes)
    {
        var utc = now.ToUniversalTime();
        DateTimeOffset current;

        if (window.Grain == HistoryGrain.Day)
        {
            var shift = TimeSpan.FromMinutes(offsetMinutes);
            var local = utc + shift;
            current = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero) - shift;
        }
        else
        {
            current = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        }

        var to = current + Step(window.Grain);
        return (to - window.Span, to);
    }

    /// <summary>The hour a reading is credited to, in UTC, which is the row it lands in.</summary>
    public static DateTimeOffset Bucket(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    public static TimeSpan Step(HistoryGrain grain)
        => grain == HistoryGrain.Day ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);

    public static string Name(HistoryGrain grain) => grain == HistoryGrain.Day ? "day" : "hour";

    /// <summary>Everything is compared as the string it is stored as, so both ends must be UTC.</summary>
    public static string Key(DateTimeOffset at) => at.ToUniversalTime().ToStorage();

    /// <summary>
    /// The offset as a SQLite modifier. Stored timestamps carry their own <c>+00:00</c>, so SQLite
    /// reads them as UTC and this shifts from there into the reader's frame.
    /// </summary>
    public static string Shift(int offsetMinutes)
        => string.Create(CultureInfo.InvariantCulture, $"{offsetMinutes:+0;-0;+0} minutes");

    /// <summary>
    /// How a stored bucket is grouped into one point of a series. <c>substr</c> over an ISO-8601
    /// string is the cheapest possible date_trunc: every bucket is written in UTC in the same
    /// format, so 13 characters is an hour. A day has to be truncated in the reader's own frame
    /// instead, which is a shift before the same cut.
    /// </summary>
    public static string BucketExpression(HistoryGrain grain)
        => grain == HistoryGrain.Day
            ? "strftime('%Y-%m-%d', bucket_start, $shift)"
            : "substr(bucket_start, 1, 13)";

    /// <summary>
    /// A grouping key back into the instant its bucket starts. An hour key is already UTC; a day
    /// key is a date in the reader's frame, so the instant is the midnight that date began there.
    /// </summary>
    public static DateTimeOffset ParseBucket(string key, HistoryGrain grain, int offsetMinutes)
    {
        if (grain != HistoryGrain.Day)
        {
            return DateTimeOffset.Parse($"{key}:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        var date = DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
    }

    /// <summary>
    /// Every bucket the window covers, in order. This is what makes a series dense: a bucket
    /// nothing was recorded in has to be drawn as a zero, because a chart that silently closes
    /// over a quiet night reads as a busy one.
    /// </summary>
    public static IEnumerable<DateTimeOffset> Buckets(DateTimeOffset from, DateTimeOffset to, HistoryGrain grain)
    {
        var step = Step(grain);
        for (var at = from; at < to; at += step)
        {
            yield return at;
        }
    }
}
