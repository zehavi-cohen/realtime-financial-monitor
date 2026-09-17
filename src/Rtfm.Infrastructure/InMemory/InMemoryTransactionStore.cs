using System.Collections.Concurrent;
using Rtfm.Core;

namespace Rtfm.Infrastructure.InMemory;

/// <summary>
/// A complete, thread-safe <see cref="ITransactionStore"/> held in process memory.
/// </summary>
/// <remarks>
/// This is a first-class implementation, not a stub (ADR-007). It satisfies the
/// brief's literal requirement - RAM storage, thread-safe, no race conditions -
/// it is what the concurrency tests exercise, and it is what makes
/// <c>dotnet test</c> run with no infrastructure at all.
///
/// Structure mirrors the Redis hot window deliberately, so the contract tests mean
/// the same thing against both stores:
///   <c>_payloads</c> is the analogue of the <c>tx:payload</c> hash,
///   <c>_window</c> is the analogue of the <c>tx:recent</c> sorted set.
///
/// Concurrency: <see cref="ReaderWriterLockSlim"/> makes an upsert - read current
/// status, decide the transition, replace the payload, re-index, trim - a single
/// atomic unit. The lifecycle check and the write cannot be split by another
/// thread, which is the in-memory equivalent of MERGE ... WITH (HOLDLOCK).
/// A ConcurrentDictionary alone would not give that: its per-key atomicity does
/// not extend across the index update and the trim.
/// </remarks>
public sealed class InMemoryTransactionStore : ITransactionStore, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Transaction> _payloads = new();
    private readonly SortedSet<WindowEntry> _window = new(WindowEntry.NewestFirst);
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly int _windowSize;

    private bool _disposed;

    public InMemoryTransactionStore(int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        _windowSize = windowSize;
    }

    public Task<IngestResult> UpsertAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        _gate.EnterWriteLock();
        try
        {
            if (_payloads.TryGetValue(transaction.TransactionId, out var existing))
            {
                if (!TransactionLifecycle.CanTransition(existing.Status, transaction.Status))
                {
                    return Task.FromResult(IngestResult.Ignored);
                }

                // The score may have moved, so the old index entry has to go before
                // the new one lands - the sorted-set analogue of ZADD replacing a
                // member's score in place rather than adding a second entry.
                _window.Remove(WindowEntry.For(existing));
                Apply(transaction);
                return Task.FromResult(IngestResult.Updated);
            }

            Apply(transaction);
            return Task.FromResult(IngestResult.Created);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        _gate.EnterReadLock();
        try
        {
            var take = Math.Min(limit, _window.Count);
            var results = new List<Transaction>(take);

            foreach (var entry in _window)
            {
                if (results.Count == take)
                {
                    break;
                }

                if (_payloads.TryGetValue(entry.TransactionId, out var transaction))
                {
                    results.Add(transaction);
                }
            }

            return Task.FromResult<IReadOnlyList<Transaction>>(results);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>Write the payload, index it, and trim. Caller holds the write lock.</summary>
    private void Apply(Transaction transaction)
    {
        _payloads[transaction.TransactionId] = transaction;
        _window.Add(WindowEntry.For(transaction));
        Trim();
    }

    /// <summary>
    /// Evict oldest-first until the window is within bounds, dropping the payload
    /// with the index entry. Same semantics as ZREMRANGEBYRANK plus HDEL in the
    /// Lua script (ADR-005): once a transaction leaves the window it leaves both keys.
    /// </summary>
    private void Trim()
    {
        while (_window.Count > _windowSize)
        {
            // Max under a newest-first comparer is the oldest entry.
            var oldest = _window.Max;
            _window.Remove(oldest);
            _payloads.TryRemove(oldest.TransactionId, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// An index entry: the sort key plus the id it points at. The id is part of the
    /// key so that two transactions with an identical OccurredAt are both retained
    /// - a SortedSet keyed on the timestamp alone would silently drop one.
    /// </summary>
    private readonly record struct WindowEntry(long OccurredAtEpochMs, Guid TransactionId)
    {
        public static WindowEntry For(Transaction transaction) =>
            new(transaction.OccurredAtEpochMs, transaction.TransactionId);

        public static IComparer<WindowEntry> NewestFirst { get; } = new NewestFirstComparer();

        private sealed class NewestFirstComparer : IComparer<WindowEntry>
        {
            public int Compare(WindowEntry x, WindowEntry y)
            {
                var byTime = y.OccurredAtEpochMs.CompareTo(x.OccurredAtEpochMs);
                return byTime != 0 ? byTime : x.TransactionId.CompareTo(y.TransactionId);
            }
        }
    }
}
