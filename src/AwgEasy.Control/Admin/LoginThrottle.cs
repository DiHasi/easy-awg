namespace AwgEasy.Control;

/// <summary>Whether this attempt may be verified at all, and how long until it may be.</summary>
public sealed record LoginVerdict(bool Allowed, TimeSpan RetryAfter)
{
    public static readonly LoginVerdict Allow = new(true, TimeSpan.Zero);
}

/// <summary>
/// How many wrong passwords the panel will look at.
///
/// Without this the sign-in endpoint accepts guesses at whatever rate the network allows, which
/// is how a panel on the open internet is taken: the password is the only thing between a stranger
/// and the fleet private key, every peer's preshared key and every client's own key pair. PBKDF2
/// at 600k iterations is a brake of its own - each guess costs the panel a few hundred
/// milliseconds of CPU - but that cuts both ways, because unlimited guessing then pins every core
/// the panel has and the operator cannot sign in to do anything about it. So a refusal that costs
/// nothing to issue comes first, and the hash is never computed for an attempt this says no to.
///
/// Counted per account and per source address, separately, because the two answer different
/// attacks: a stranger working through a wordlist against the one account the panel has trips the
/// account counter wherever they come from, and one host spraying names trips the address one.
/// The account counter is the brake that has to hold. The panel sits behind a TLS terminator and
/// takes X-Forwarded-For from whoever presents it - which it must, or every request would read as
/// coming from the proxy - so the source address is a hint rather than a fact and an attacker who
/// knows that can rotate it freely. The address counter is there for the honest client that is
/// hammering, and for nothing stronger than that.
///
/// In memory, and deliberately not in the database. A lockout that survived a restart would be a
/// way to lock an operator out of their own panel for good, and the attacker it would stop has no
/// way to know a container was recreated. The cost is that a restart clears it, which is a worse
/// outcome for an attack that is minutes long and no outcome at all for one that is hours long,
/// since the counters hold for as long as the process does.
/// </summary>
public sealed class LoginThrottle
{
    /// <summary>
    /// How far back failures are counted, and therefore how long a tripped limit holds: the window
    /// slides, so a key opens again once its oldest counted failure has aged out. Nothing clears
    /// it by hand on purpose - an endpoint that unlocked an account would have to be reachable
    /// without a session, which is the thing being protected.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wrong passwords for one account before it stops being verified. High enough that an
    /// operator mistyping a long password is not locked out of their own panel, low enough that a
    /// wordlist gets nowhere: ten guesses a quarter of an hour is forty a day.
    ///
    /// This is also a way to keep an operator out for fifteen minutes if a stranger knows the
    /// account name, and that trade is accepted deliberately. Being made to wait is recoverable;
    /// a guessed password is not.
    /// </summary>
    public const int AccountFailures = 10;

    /// <summary>
    /// Wrong passwords from one source address, across every account. Looser than the account
    /// limit because it is the weaker of the two - the address is whatever the forwarded headers
    /// said - and because an operator's whole office shares one of these.
    /// </summary>
    public const int AddressFailures = 30;

    /// <summary>
    /// How many addresses are tracked at once. A spoofable key must not be able to grow a
    /// dictionary without bound, so once this many are being remembered the expired ones are
    /// dropped and, if that is not enough, new addresses are simply not counted. Losing the weaker
    /// of the two counters under a flood is the right failure: the account counter does not share
    /// this limit, because the set of account names is the panel's own and small.
    /// </summary>
    private const int AddressesTracked = 4096;

    private readonly Dictionary<string, Attempts> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Attempts> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LoginVerdict Check(string username, string? address, DateTimeOffset now)
    {
        lock (_gate)
        {
            var account = Verdict(_accounts, username, AccountFailures, now);
            var source = address is null ? LoginVerdict.Allow : Verdict(_addresses, address, AddressFailures, now);

            // Whichever has longer to wait decides, so a caller is never told to come back before
            // both limits have actually opened.
            return account.RetryAfter >= source.RetryAfter ? account : source;
        }
    }

    /// <returns>
    /// True when this failure is the one that tripped a limit. The call site logs that and
    /// nothing else: an attacker who kept going would otherwise write a row per guess into the
    /// event log, and the log an operator reads afterwards is the first casualty.
    /// </returns>
    public bool RecordFailure(string username, string? address, DateTimeOffset now)
    {
        lock (_gate)
        {
            var tripped = Record(_accounts, username, AccountFailures, now, capacity: null);
            if (address is not null)
            {
                tripped |= Record(_addresses, address, AddressFailures, now, AddressesTracked);
            }

            return tripped;
        }
    }

    /// <summary>
    /// Forgets the failures behind a correct password, so an operator who mistyped twice and then
    /// got it right starts from zero rather than carrying the attempts for a quarter of an hour.
    /// Only this account and this address: the point is to clear what the operator themselves did,
    /// not to let one successful sign-in wipe a wordlist's progress against another account.
    /// </summary>
    public void RecordSuccess(string username, string? address)
    {
        lock (_gate)
        {
            _accounts.Remove(username);
            if (address is not null)
            {
                _addresses.Remove(address);
            }
        }
    }

    private static LoginVerdict Verdict(Dictionary<string, Attempts> keys, string key, int limit, DateTimeOffset now)
    {
        if (!keys.TryGetValue(key, out var attempts))
        {
            return LoginVerdict.Allow;
        }

        attempts.Prune(now);
        if (attempts.Count < limit)
        {
            return LoginVerdict.Allow;
        }

        // The oldest failure still counted is the one that has to age out, and until it does the
        // key stays at the limit.
        var opensAt = attempts.Oldest + Window;
        return new LoginVerdict(false, opensAt > now ? opensAt - now : TimeSpan.Zero);
    }

    private static bool Record(Dictionary<string, Attempts> keys, string key, int limit, DateTimeOffset now, int? capacity)
    {
        if (!keys.TryGetValue(key, out var attempts))
        {
            if (capacity is { } maximum && keys.Count >= maximum)
            {
                PruneAll(keys, now);
                if (keys.Count >= maximum)
                {
                    return false;
                }
            }

            attempts = new Attempts(limit);
            keys[key] = attempts;
        }

        var wasUnder = attempts.Count < limit;
        attempts.Add(now);
        return wasUnder && attempts.Count >= limit;
    }

    private static void PruneAll(Dictionary<string, Attempts> keys, DateTimeOffset now)
    {
        foreach (var (key, attempts) in keys.ToArray())
        {
            attempts.Prune(now);
            if (attempts.Count == 0)
            {
                keys.Remove(key);
            }
        }
    }

    /// <summary>
    /// The most recent failures for one key, capped at the limit. Capped because nothing above it
    /// changes any answer: the limit is already reached, and the only question left is when the
    /// oldest of those failures ages out.
    /// </summary>
    private sealed class Attempts(int limit)
    {
        private readonly Queue<DateTimeOffset> _at = new(limit);

        public int Count => _at.Count;

        public DateTimeOffset Oldest => _at.Peek();

        public void Add(DateTimeOffset now)
        {
            if (_at.Count >= limit)
            {
                _at.Dequeue();
            }

            _at.Enqueue(now);
        }

        public void Prune(DateTimeOffset now)
        {
            while (_at.Count > 0 && now - _at.Peek() >= Window)
            {
                _at.Dequeue();
            }
        }
    }
}
