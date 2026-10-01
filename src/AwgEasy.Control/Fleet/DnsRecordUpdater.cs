using AwgEasy.Contracts;

namespace AwgEasy.Control;

/// <param name="Name">The record to point, normally the same host every client config carries.</param>
/// <param name="Address">The active node's public address.</param>
public sealed record DnsRecordTarget(string Name, string Address, int Ttl)
{
    /// <summary>The record type follows the address family, so a v6-only node still works.</summary>
    public string RecordType => Address.Contains(':', StringComparison.Ordinal) ? "AAAA" : "A";
}

public enum DnsUpdateOutcome
{
    /// <summary>The provider confirmed the record now points at the requested address.</summary>
    Applied,

    /// <summary>No provider is configured: the record is the operator's to change by hand.</summary>
    Manual,

    /// <summary>The provider refused or could not be reached. The record is unchanged.</summary>
    Failed
}

public sealed record DnsUpdateResult(DnsUpdateOutcome Outcome, string? Detail = null, ApiError? Error = null);

/// <summary>
/// Points the failover record at one node. Kept behind an interface because the provider is the
/// one part of failover that differs per deployment, and because Phase 2 has to be able to drive
/// exactly this from a health probe rather than from a button.
/// </summary>
public interface IDnsRecordUpdater
{
    string ProviderName { get; }

    bool IsConfigured { get; }

    Task<DnsUpdateResult> PointAsync(DnsRecordTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Checks, without changing anything, that a switch could move the record at all. Run before
    /// automatic failover is armed, so a zone or record problem surfaces while an operator is
    /// looking rather than at the moment a node dies. Null means nothing is in the way.
    /// </summary>
    Task<ApiError?> CheckAsync(string recordName, CancellationToken cancellationToken);
}

/// <summary>
/// The fallback when no provider credentials are set. Switching still works - the panel records
/// which node is active and shows the record to set - it just leaves the edit to the operator,
/// and the resolved-address check on the status endpoint is what confirms they made it.
/// </summary>
public sealed class ManualDnsRecordUpdater : IDnsRecordUpdater
{
    public string ProviderName => "manual";

    public bool IsConfigured => false;

    public Task<DnsUpdateResult> PointAsync(DnsRecordTarget target, CancellationToken cancellationToken)
        => Task.FromResult(new DnsUpdateResult(
            DnsUpdateOutcome.Manual,
            $"Set {target.Name} {target.RecordType} to {target.Address} with a TTL of {target.Ttl}s."));

    public Task<ApiError?> CheckAsync(string recordName, CancellationToken cancellationToken)
        => Task.FromResult<ApiError?>(null);
}
