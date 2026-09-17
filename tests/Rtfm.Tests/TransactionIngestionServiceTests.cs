using Rtfm.Core;
using Rtfm.Infrastructure.InMemory;

namespace Rtfm.Tests;

/// <summary>
/// The ingestion pipeline: what gets persisted, what gets broadcast, and what does
/// neither. The broadcast port is an interface precisely so these can be asserted
/// with no hub, no socket and no backplane.
/// </summary>
public sealed class TransactionIngestionServiceTests
{
    // ---- broadcast rule ----------------------------------------------------

    [Theory]
    [InlineData(IngestResult.Created)]
    [InlineData(IngestResult.Updated)]
    public async Task IngestAsync_StoreReportsAChange_Broadcasts(IngestResult result)
    {
        var store = new StubTransactionStore(result);
        var broadcaster = new RecordingBroadcaster();
        var service = new TransactionIngestionService(store, broadcaster);

        var outcome = await service.IngestAsync(TestData.Submission(), CancellationToken.None);

        outcome.Result.Should().Be(result);
        broadcaster.Broadcasts.Should().ContainSingle();
    }

    [Fact]
    public async Task IngestAsync_StoreReportsIgnored_DoesNotBroadcast()
    {
        // A late duplicate must not put a stale status back on every dashboard.
        var store = new StubTransactionStore(IngestResult.Ignored);
        var broadcaster = new RecordingBroadcaster();
        var service = new TransactionIngestionService(store, broadcaster);

        await service.IngestAsync(TestData.Submission(), CancellationToken.None);

        broadcaster.Broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_BroadcastsTheStoredTransaction()
    {
        var id = Guid.NewGuid();
        var store = new StubTransactionStore(IngestResult.Created);
        var broadcaster = new RecordingBroadcaster();
        var service = new TransactionIngestionService(store, broadcaster);

        await service.IngestAsync(
            TestData.Submission(transactionId: id.ToString(), amount: 42.5m, status: "Completed"),
            CancellationToken.None);

        broadcaster.Broadcasts.Should().ContainSingle().Which.Should().Match<Transaction>(t =>
            t.TransactionId == id && t.Amount == 42.5m && t.Status == TransactionStatus.Completed);
    }

    // ---- rejection ---------------------------------------------------------

    [Fact]
    public async Task IngestAsync_InvalidSubmission_TouchesNeitherStoreNorBroadcaster()
    {
        var store = new StubTransactionStore(IngestResult.Created);
        var broadcaster = new RecordingBroadcaster();
        var service = new TransactionIngestionService(store, broadcaster);

        var outcome = await service.IngestAsync(TestData.Submission(amount: -5m), CancellationToken.None);

        outcome.IsAccepted.Should().BeFalse();
        outcome.Failures.Should().ContainSingle()
            .Which.Field.Should().Be(TransactionValidator.FieldNames.Amount);
        store.Upserts.Should().BeEmpty();
        broadcaster.Broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_InvalidSubmission_HasNoTransactionToRead()
    {
        var service = new TransactionIngestionService(new StubTransactionStore(IngestResult.Created), new RecordingBroadcaster());

        var outcome = await service.IngestAsync(TestData.Submission(status: "Success"), CancellationToken.None);

        var read = () => outcome.Transaction;
        read.Should().Throw<InvalidOperationException>();
    }

    // ---- end to end over a real store --------------------------------------

    [Fact]
    public async Task IngestAsync_LifecycleOverARealStore_BroadcastsCreationAndTheStatusChangeOnly()
    {
        using var store = new InMemoryTransactionStore(windowSize: 100);
        var broadcaster = new RecordingBroadcaster();
        var service = new TransactionIngestionService(store, broadcaster);
        var id = Guid.NewGuid().ToString();

        await service.IngestAsync(TestData.Submission(transactionId: id, status: "Pending"), CancellationToken.None);
        await service.IngestAsync(TestData.Submission(transactionId: id, status: "Completed"), CancellationToken.None);
        await service.IngestAsync(TestData.Submission(transactionId: id, status: "Completed"), CancellationToken.None);
        await service.IngestAsync(TestData.Submission(transactionId: id, status: "Pending"), CancellationToken.None);

        // Created, then Updated. The re-send of Completed and the regression to
        // Pending are both Ignored, so neither reaches a dashboard.
        broadcaster.Broadcasts.Select(t => t.Status).Should().Equal(
            TransactionStatus.Pending,
            TransactionStatus.Completed);
    }

    [Fact]
    public async Task GetRecentAsync_ZeroLimit_Throws()
    {
        var service = new TransactionIngestionService(new StubTransactionStore(IngestResult.Created), new RecordingBroadcaster());

        var read = async () => await service.GetRecentAsync(0, CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
