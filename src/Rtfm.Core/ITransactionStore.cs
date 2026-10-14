namespace Rtfm.Core;

/// <summary>
/// The only way the application reaches durable state (ADR-007).
/// </summary>
/// <remarks>
/// Two implementations satisfy this interface and both are verified by the same
/// contract test suite: an in-memory store used by unit tests and by
/// <c>Storage=InMemory</c>, and a SQL Server + Redis store used in deployment.
/// Nothing above this interface knows which one is active.
/// </remarks>
public interface ITransactionStore
{
    /// <summary>
    /// Applies a producer's assertion about a transaction.
    /// </summary>
    /// <remarks>
    /// This is an upsert keyed on <see cref="Transaction.TransactionId"/>, not an
    /// insert: a repeated id is a status change, not a duplicate to reject. The
    /// returned <see cref="IngestResult"/> tells the caller whether anything
    /// actually changed, which is what decides whether to broadcast. Implementations
    /// must apply the <see cref="TransactionLifecycle"/> rule atomically - two
    /// concurrent calls for the same id must not interleave destructively.
    /// </remarks>
    Task<IngestResult> UpsertAsync(Transaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent transactions by <see cref="Transaction.OccurredAt"/>, newest first.
    /// </summary>
    /// <remarks>
    /// Must never observe a partially applied upsert, and must never throw because
    /// a write is in progress. Ordering follows the producer's timestamp rather
    /// than arrival order.
    /// </remarks>
    Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken cancellationToken);
}
