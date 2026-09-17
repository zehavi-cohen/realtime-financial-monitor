namespace Rtfm.Core;

/// <summary>
/// A financial transaction, as asserted by the producer. Immutable: a status
/// change produces a new instance rather than mutating an existing one, which
/// removes a whole class of concurrency question from the store implementations.
/// </summary>
public sealed record Transaction
{
    public required Guid TransactionId { get; init; }

    /// <summary>Monetary amount. decimal, never double - see ADR-004.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO-4217-shaped code, three upper-case letters. Stored, not interpreted.</summary>
    public required string Currency { get; init; }

    public required TransactionStatus Status { get; init; }

    /// <summary>
    /// When the producer says the transaction happened. Normalised to UTC and
    /// truncated to milliseconds so that the in-memory value, the DATETIME2(3)
    /// column and the Redis sorted-set score all agree exactly.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Sorted-set score for the Redis hot window (ADR-005).</summary>
    public long OccurredAtEpochMs => OccurredAt.ToUnixTimeMilliseconds();
}
