namespace AwgEasy.Control;

/// <summary>How a quota is measured. Two words, because the two things operators actually hand out
/// are a trial with an allowance and a person with a monthly one.</summary>
public static class PeerQuotaWindows
{
    /// <summary>Counted from the moment the quota was set, and never reset by the panel. What a
    /// trial config is.</summary>
    public const string Total = "total";

    /// <summary>Counted from the start of the current calendar month in UTC, and put back - peer
    /// included - when the next one begins.</summary>
    public const string Monthly = "monthly";

    public static bool IsKnown(string? window) => window is Total or Monthly;
}

/// <summary>Why the panel switched a peer off, as opposed to an operator doing it.</summary>
public static class PeerDisableReasons
{
    public const string Expiry = "expiry";
    public const string Quota = "quota";

    public static bool IsAutomatic(string? reason) => reason is Expiry or Quota;
}

/// <summary>
/// What a peer is allowed, as the panel holds it.
/// </summary>
/// <param name="QuotaStartedAt">When the allowance currently being counted began: set when the
/// quota is set or reset, and moved forward when a monthly window rolls. Null when there is no
/// quota.</param>
/// <param name="QuotaUsedBytes">What this peer has moved since then, received and sent together.
/// Accrued where the evidence is - inside the transaction that overwrites a node's counters - so
/// it survives the pruning of the traffic history, a node reboot, and an operator zeroing the
/// live counters, none of which say anything about what a person actually used.</param>
/// <param name="DisabledBy">Which limit switched this peer off, or null for a peer the panel has
/// not touched. An operator's own disable leaves it null, which is what keeps a monthly rollover
/// from re-enabling somebody who was switched off by hand.</param>
public sealed record PeerLimits(
    DateTimeOffset? ExpiresAt = null,
    long? QuotaBytes = null,
    string QuotaWindow = PeerQuotaWindows.Total,
    DateTimeOffset? QuotaStartedAt = null,
    long QuotaUsedBytes = 0,
    string? DisabledBy = null)
{
    public static readonly PeerLimits None = new();

    public bool Any => ExpiresAt is not null || QuotaBytes is not null;

    /// <summary>What is left of the allowance, or null when there is no quota. Never negative:
    /// a peer reports its counters every twenty seconds and the sweep runs on its own clock, so
    /// an overshoot is normal and "minus 400 MB left" is not a thing worth drawing.</summary>
    public long? QuotaRemaining => QuotaBytes is { } quota ? Math.Max(0, quota - QuotaUsedBytes) : null;
}

/// <summary>What the panel should do about one peer's limits right now.</summary>
public enum PeerLimitAction
{
    /// <summary>Nothing. Either there are no limits, or none of them has been reached.</summary>
    None,

    /// <summary>Switch it off: its expiry has passed.</summary>
    Expire,

    /// <summary>Switch it off: it has moved its whole allowance.</summary>
    Exhaust,

    /// <summary>A new calendar month: zero what it has used and put it back if the quota is what
    /// took it out.</summary>
    Renew
}

/// <summary>
/// Whether a peer has reached a limit, as a pure function of what the panel holds about it.
///
/// Pure, and in one place, for the same reason <see cref="NodeHealthEvaluator"/> and
/// <see cref="PeerPresence"/> are: several readers depend on this agreeing with itself. The sweep
/// that switches peers off, the refusal an operator meets when they enable one that is still
/// expired, and the words the peer list draws beside it all ask this - and three readers
/// disagreeing about whether somebody's config works would be worse than none of them.
///
/// Reading a limit is deliberately not the same thing as applying it. Nothing here writes, so a
/// limit reached between two sweeps is simply a peer that is still enabled for up to a minute,
/// which is also true of the twenty seconds between a node's reports. A quota is a billing
/// instrument, not a packet filter.
/// </summary>
public static class PeerLimitPolicy
{
    public static PeerLimitAction Evaluate(PeerLimits limits, bool enabled, DateTimeOffset now)
    {
        // The rollover first, and whether the peer is enabled or not: a monthly allowance that
        // only renewed for the peers it had not yet switched off would renew for everybody except
        // the people waiting for it.
        if (limits.QuotaBytes is not null
            && limits.QuotaWindow == PeerQuotaWindows.Monthly
            && limits.QuotaStartedAt is { } started
            && IsEarlierMonth(started, now))
        {
            return PeerLimitAction.Renew;
        }

        if (!enabled)
        {
            // Already off. Whether it was the panel or the operator who switched it off, there is
            // nothing further to do about it until the quota renews above.
            return PeerLimitAction.None;
        }

        // The expiry is read first of the two because it is the one that cannot be undone by
        // traffic stopping: a peer that is both out of time and out of allowance is out of time.
        if (limits.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return PeerLimitAction.Expire;
        }

        if (limits.QuotaBytes is { } quota && limits.QuotaUsedBytes >= quota)
        {
            return PeerLimitAction.Exhaust;
        }

        return PeerLimitAction.None;
    }

    /// <summary>
    /// Whether enabling this peer by hand would be undone by the next sweep, which is what makes
    /// it worth refusing instead.
    ///
    /// The alternative is a peer that goes back on, stays on for up to a minute and switches
    /// itself off again, leaving an operator to guess which of the two limits did it. Saying so
    /// at the moment they press the button costs them one more decision - extend the expiry, or
    /// reset the allowance - and that decision is the thing they actually meant to make.
    /// </summary>
    public static PeerLimitAction WouldRefuseEnabling(PeerLimits limits, DateTimeOffset now)
    {
        var verdict = Evaluate(limits, enabled: true, now);
        return verdict is PeerLimitAction.Expire or PeerLimitAction.Exhaust ? verdict : PeerLimitAction.None;
    }

    /// <summary>The start of the UTC month a stamp falls in, which is where a renewed window
    /// begins: an allowance that restarted at the moment the sweep happened to notice would drift
    /// a little later every month.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static bool IsEarlierMonth(DateTimeOffset started, DateTimeOffset now)
        => MonthStart(started) < MonthStart(now);
}
