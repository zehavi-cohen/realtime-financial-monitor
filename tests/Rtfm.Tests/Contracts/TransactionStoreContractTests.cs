using Rtfm.Core;

namespace Rtfm.Tests.Contracts;

/// <summary>
/// The behavioural contract of <see cref="ITransactionStore"/>.
/// </summary>
/// <remarks>
/// Both implementations run this suite unchanged: the in-memory store here, the
/// SQL Server + Redis store in the integration project. That is the point of the
/// abstraction - the in-memory store is only a valid stand-in if it is the same
/// thing behaviourally, and the only way to know is to assert it.
///
/// These tests contain no SQL, no Redis, no locks and no implementation names.
/// Anything they cannot express is, by definition, not part of the contract.
/// </remarks>
public abstract class TransactionStoreContractTests
{
    /// <summary>Large enough that window trimming never interferes with a test about something else.</summary>
    protected const int DefaultWindowSize = 1_000;

    /// <summary>
    /// Produces a store with an empty window. Implementations are responsible for
    /// isolation - the SQL/Redis one clears its state here.
    /// </summary>
    protected abstract Task<StoreHandle> CreateStoreAsync(int windowSize, CancellationToken cancellationToken);

    // ---- identity and lifecycle -------------------------------------------

    [Fact]
    public async Task UpsertAsync_UnknownTransaction_ReturnsCreated()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);

        var result = await handle.Store.UpsertAsync(TestData.Transaction(), CancellationToken.None);

        result.Should().Be(IngestResult.Created);
    }

    [Theory]
    [InlineData(TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed)]
    public async Task UpsertAsync_PendingToTerminal_ReturnsUpdated(TransactionStatus terminal)
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(TestData.Transaction(id), CancellationToken.None);

        var result = await handle.Store.UpsertAsync(
            TestData.Transaction(id, status: terminal),
            CancellationToken.None);

        result.Should().Be(IngestResult.Updated);
    }

    [Fact]
    public async Task UpsertAsync_IdenticalResend_ReturnsIgnored()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var transaction = TestData.Transaction();
        await handle.Store.UpsertAsync(transaction, CancellationToken.None);

        var result = await handle.Store.UpsertAsync(transaction, CancellationToken.None);

        result.Should().Be(IngestResult.Ignored);
    }

    [Fact]
    public async Task UpsertAsync_CompletedToPending_ReturnsIgnored()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(TestData.Transaction(id, status: TransactionStatus.Completed), CancellationToken.None);

        var result = await handle.Store.UpsertAsync(
            TestData.Transaction(id, status: TransactionStatus.Pending),
            CancellationToken.None);

        result.Should().Be(IngestResult.Ignored);
    }

    [Fact]
    public async Task UpsertAsync_CompletedToFailed_ReturnsIgnored()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(TestData.Transaction(id, status: TransactionStatus.Completed), CancellationToken.None);

        // A terminal transaction never regresses and never moves sideways.
        var result = await handle.Store.UpsertAsync(
            TestData.Transaction(id, status: TransactionStatus.Failed),
            CancellationToken.None);

        result.Should().Be(IngestResult.Ignored);
    }

    [Fact]
    public async Task UpsertAsync_CompletedToPending_LeavesTheStoredTransactionUnchanged()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(
            TestData.Transaction(id, amount: 100m, status: TransactionStatus.Completed),
            CancellationToken.None);

        await handle.Store.UpsertAsync(
            TestData.Transaction(id, amount: 999m, status: TransactionStatus.Pending),
            CancellationToken.None);

        var stored = await handle.Store.GetRecentAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.Should().Match<Transaction>(t => t.Status == TransactionStatus.Completed && t.Amount == 100m);
    }

    [Fact]
    public async Task UpsertAsync_PendingToCompleted_ReplacesTheRowRatherThanAddingOne()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(TestData.Transaction(id, amount: 100m), CancellationToken.None);

        await handle.Store.UpsertAsync(
            TestData.Transaction(id, amount: 250m, status: TransactionStatus.Completed),
            CancellationToken.None);

        var stored = await handle.Store.GetRecentAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.Should().Match<Transaction>(t => t.Status == TransactionStatus.Completed && t.Amount == 250m);
    }

    // ---- reads -------------------------------------------------------------

    [Fact]
    public async Task GetRecentAsync_EmptyStore_ReturnsNothing()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);

        var recent = await handle.Store.GetRecentAsync(10, CancellationToken.None);

        recent.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirstByOccurredAt()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);

        // Inserted oldest-last, so arrival order and timestamp order disagree.
        // Ordering must follow the producer's timestamp (ADR-005).
        var newest = TestData.Transaction(occurredAt: TestData.Epoch.AddMinutes(10));
        var middle = TestData.Transaction(occurredAt: TestData.Epoch.AddMinutes(5));
        var oldest = TestData.Transaction(occurredAt: TestData.Epoch);

        await handle.Store.UpsertAsync(middle, CancellationToken.None);
        await handle.Store.UpsertAsync(newest, CancellationToken.None);
        await handle.Store.UpsertAsync(oldest, CancellationToken.None);

        var recent = await handle.Store.GetRecentAsync(10, CancellationToken.None);

        recent.Select(t => t.TransactionId).Should().ContainInOrder(
            newest.TransactionId,
            middle.TransactionId,
            oldest.TransactionId);
    }

    [Fact]
    public async Task GetRecentAsync_LimitBelowStoredCount_ReturnsTheNewestLimit()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        for (var i = 0; i < 20; i++)
        {
            await handle.Store.UpsertAsync(
                TestData.Transaction(occurredAt: TestData.Epoch.AddSeconds(i)),
                CancellationToken.None);
        }

        var recent = await handle.Store.GetRecentAsync(5, CancellationToken.None);

        recent.Should().HaveCount(5);
        recent.Select(t => t.OccurredAt).Should().BeInDescendingOrder();
        recent[0].OccurredAt.Should().Be(TestData.Epoch.AddSeconds(19));
    }

    // ---- window bound ------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_PastTheWindowSize_KeepsExactlyTheNewestNByOccurredAt()
    {
        const int windowSize = 10;
        const int inserted = 40;
        await using var handle = await CreateStoreAsync(windowSize, CancellationToken.None);

        // Shuffled insert order: if eviction followed arrival order rather than
        // OccurredAt, this test would fail, and that is the whole point of it.
        var timestamps = Enumerable.Range(0, inserted)
            .Select(i => TestData.Epoch.AddSeconds(i))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        foreach (var timestamp in timestamps)
        {
            await handle.Store.UpsertAsync(TestData.Transaction(occurredAt: timestamp), CancellationToken.None);
        }

        var recent = await handle.Store.GetRecentAsync(windowSize * 2, CancellationToken.None);

        recent.Should().HaveCount(windowSize);
        recent.Select(t => t.OccurredAt).Should().BeEquivalentTo(
            Enumerable.Range(inserted - windowSize, windowSize)
                .Select(i => TestData.Epoch.AddSeconds(i))
                .OrderByDescending(t => t),
            options => options.WithStrictOrdering());
    }

    // ---- concurrency -------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_ManyDistinctTransactionsInParallel_AllArePersisted()
    {
        const int count = 500;
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);

        var transactions = Enumerable.Range(0, count)
            .Select(i => TestData.Transaction(occurredAt: TestData.Epoch.AddMilliseconds(i)))
            .ToList();

        var results = await Task.WhenAll(transactions.Select(t =>
            handle.Store.UpsertAsync(t, CancellationToken.None)));

        results.Should().AllBeEquivalentTo(IngestResult.Created);

        var recent = await handle.Store.GetRecentAsync(DefaultWindowSize, CancellationToken.None);
        recent.Select(t => t.TransactionId).Should().BeEquivalentTo(transactions.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task UpsertAsync_SameTransactionInParallel_AppliesExactlyOnce()
    {
        const int attempts = 300;
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var transaction = TestData.Transaction();

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            handle.Store.UpsertAsync(transaction, CancellationToken.None)));

        // Exactly one writer creates the row; the other 299 see it already there and
        // find nothing to assert. Any other split means two writers interleaved.
        results.Count(r => r == IngestResult.Created).Should().Be(1);
        results.Count(r => r == IngestResult.Ignored).Should().Be(attempts - 1);

        var recent = await handle.Store.GetRecentAsync(DefaultWindowSize, CancellationToken.None);
        recent.Should().ContainSingle().Which.TransactionId.Should().Be(transaction.TransactionId);
    }

    [Fact]
    public async Task UpsertAsync_ParallelStatusChangesForOneTransaction_ApplyExactlyOneAndLeaveALegalState()
    {
        const int attempts = 300;
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        var id = Guid.NewGuid();
        await handle.Store.UpsertAsync(TestData.Transaction(id), CancellationToken.None);

        // Half claim Completed, half claim Failed, all at once. Exactly one may win,
        // and the loser must not partially apply on top of the winner.
        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(i =>
            handle.Store.UpsertAsync(
                TestData.Transaction(id, status: i % 2 == 0 ? TransactionStatus.Completed : TransactionStatus.Failed),
                CancellationToken.None)));

        results.Count(r => r == IngestResult.Updated).Should().Be(1);
        results.Count(r => r == IngestResult.Ignored).Should().Be(attempts - 1);

        var recent = await handle.Store.GetRecentAsync(DefaultWindowSize, CancellationToken.None);
        recent.Should().ContainSingle()
            .Which.Status.Should().BeOneOf(TransactionStatus.Completed, TransactionStatus.Failed);
    }

    [Fact]
    public async Task GetRecentAsync_DuringSustainedWrites_NeverThrowsAndNeverSeesAPartialUpdate()
    {
        await using var handle = await CreateStoreAsync(DefaultWindowSize, CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Every transaction carries an invariant between its own fields: the amount
        // is derived from the id. A reader that ever sees a row whose amount does
        // not match its id has observed a torn write.
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var id = Guid.NewGuid();
                await handle.Store.UpsertAsync(WithDerivedAmount(id, TransactionStatus.Pending), CancellationToken.None);
                await handle.Store.UpsertAsync(WithDerivedAmount(id, TransactionStatus.Completed), CancellationToken.None);
            }
        });

        var reads = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = await handle.Store.GetRecentAsync(200, CancellationToken.None);
                reads++;

                snapshot.Select(t => t.TransactionId).Should().OnlyHaveUniqueItems();
                foreach (var transaction in snapshot)
                {
                    transaction.Amount.Should().Be(DerivedAmount(transaction.TransactionId));
                }
            }
        });

        var run = async () => await Task.WhenAll(writer, reader);

        await run.Should().NotThrowAsync();
        reads.Should().BeGreaterThan(0, "the reader must actually have run");
    }

    private static Transaction WithDerivedAmount(Guid id, TransactionStatus status) =>
        TestData.Transaction(id, amount: DerivedAmount(id), status: status, occurredAt: TestData.Epoch.AddMilliseconds(id.GetHashCode() & 0xFFFF));

    private static decimal DerivedAmount(Guid id) => Math.Abs(id.GetHashCode() % 10_000) + 0.25m;

    /// <summary>
    /// A store plus whatever it needs torn down. Keeps the contract tests free of
    /// any knowledge of how a particular implementation is created or cleaned up.
    /// </summary>
    protected sealed class StoreHandle : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose;

        public StoreHandle(ITransactionStore store, Func<ValueTask>? dispose = null)
        {
            Store = store;
            _dispose = dispose ?? (() => ValueTask.CompletedTask);
        }

        public ITransactionStore Store { get; }

        public ValueTask DisposeAsync() => _dispose();
    }
}
