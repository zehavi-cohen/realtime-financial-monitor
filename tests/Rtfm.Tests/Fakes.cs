using System.Collections.Concurrent;
using Rtfm.Core;

namespace Rtfm.Tests;

/// <summary>
/// Records what was broadcast. Hand-written rather than mocked: the assertions are
/// about a sequence of calls, which reads better as a list than as a mock's
/// verification DSL, and it keeps the test project down to one assertion library.
/// </summary>
public sealed class RecordingBroadcaster : IBroadcaster
{
    private readonly ConcurrentQueue<Transaction> _broadcasts = new();

    public IReadOnlyCollection<Transaction> Broadcasts => _broadcasts;

    public Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        _broadcasts.Enqueue(transaction);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A store that returns whatever the test told it to, and records what it was
/// asked to do. Lets the broadcast rule be asserted for each
/// <see cref="IngestResult"/> without contriving the state that produces it.
/// </summary>
public sealed class StubTransactionStore : ITransactionStore
{
    private readonly ConcurrentQueue<Transaction> _upserts = new();

    public StubTransactionStore(IngestResult result) => Result = result;

    public IngestResult Result { get; set; }

    public IReadOnlyCollection<Transaction> Upserts => _upserts;

    public IReadOnlyList<Transaction> Recent { get; set; } = [];

    public Task<IngestResult> UpsertAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        _upserts.Enqueue(transaction);
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Transaction>>(Recent.Take(limit).ToList());
}
