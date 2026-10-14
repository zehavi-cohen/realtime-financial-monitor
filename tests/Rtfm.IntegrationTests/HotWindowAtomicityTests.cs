using Rtfm.Core;
using Rtfm.Infrastructure.Redis;
using Rtfm.Tests;
using StackExchange.Redis;

namespace Rtfm.IntegrationTests;

/// <summary>
/// The two things only real infrastructure can show: that the Lua script is atomic
/// under parallel clients, and that a flushed window rebuilds itself from the
/// system of record.
/// </summary>
/// <remarks>
/// Neither is expressible in the contract suite. The contract says what a store
/// does; these say that the mechanism chosen to do it actually holds under the
/// conditions it was chosen for.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class HotWindowAtomicityTests
{
    private readonly InfrastructureFixture _fixture;

    public HotWindowAtomicityTests(InfrastructureFixture fixture) => _fixture = fixture;

    [RequiresInfrastructureFact]
    public async Task ProjectAsync_UnderParallelClients_LeavesNoPayloadOrphanedFromTheIndex()
    {
        // The failure this guards against: with the ZRANGE and the HDEL issued as
        // separate round trips, another client's writes interleave between them,
        // and the payload hash keeps entries the index can no longer reach. It
        // grows without bound and nothing notices until Redis runs out of memory.
        const int windowSize = 50;
        const int writes = 600;

        await using var context = _fixture.CreateStoreContextAsync(windowSize);
        await context.ResetAsync();

        var transactions = Enumerable.Range(0, writes)
            .Select(i => TestData.Transaction(occurredAt: TestData.Epoch.AddMilliseconds(i)))
            .ToList();

        await Task.WhenAll(transactions.Select(t => context.Window.ProjectAsync(t, CancellationToken.None)));

        var redis = await context.GetRedisAsync();
        var indexed = await redis.SortedSetLengthAsync(RedisHotWindow.RecentKey);
        var payloads = await redis.HashLengthAsync(RedisHotWindow.PayloadKey);

        indexed.Should().Be(windowSize, "the script trims the index to the window size");
        payloads.Should().Be(windowSize, "and evicts the matching payloads in the same atomic unit");

        // Stronger than the counts: the two keys must agree member for member.
        var members = await redis.SortedSetRangeByRankAsync(RedisHotWindow.RecentKey, 0, -1);
        var values = await redis.HashGetAsync(RedisHotWindow.PayloadKey, members);
        values.Should().NotContain(value => value.IsNullOrEmpty, "every indexed id must still have its payload");
    }

    [RequiresInfrastructureFact]
    public async Task ProjectAsync_SetsATtlOnBothKeys()
    {
        await using var context = _fixture.CreateStoreContextAsync();
        await context.ResetAsync();

        await context.Window.ProjectAsync(TestData.Transaction(), CancellationToken.None);

        var redis = await context.GetRedisAsync();
        (await redis.KeyTimeToLiveAsync(RedisHotWindow.RecentKey)).Should().NotBeNull();
        (await redis.KeyTimeToLiveAsync(RedisHotWindow.PayloadKey)).Should().NotBeNull();
    }

    [RequiresInfrastructureFact]
    public async Task GetRecentAsync_AfterARedisFlush_RebuildsTheWindowFromSqlServer()
    {
        await using var context = _fixture.CreateStoreContextAsync();
        await context.ResetAsync();

        var transactions = Enumerable.Range(0, 25)
            .Select(i => TestData.Transaction(occurredAt: TestData.Epoch.AddSeconds(i)))
            .ToList();

        foreach (var transaction in transactions)
        {
            await context.Store.UpsertAsync(transaction, CancellationToken.None);
        }

        var redis = await context.GetRedisAsync();
        await redis.KeyDeleteAsync([RedisHotWindow.RecentKey, RedisHotWindow.PayloadKey]);

        var recovered = await context.Store.GetRecentAsync(100, CancellationToken.None);

        recovered.Select(t => t.TransactionId).Should().BeEquivalentTo(transactions.Select(t => t.TransactionId));
        recovered.Select(t => t.OccurredAt).Should().BeInDescendingOrder();

        // And the window is warm again, not rebuilt on every subsequent read.
        (await redis.SortedSetLengthAsync(RedisHotWindow.RecentKey)).Should().Be(transactions.Count);
        (await redis.HashLengthAsync(RedisHotWindow.PayloadKey)).Should().Be(transactions.Count);
    }

    [RequiresInfrastructureFact]
    public async Task GetRecentAsync_ConcurrentReadersOnAColdWindow_RebuildOnceUnderTheDistributedLock()
    {
        const int readers = 10;

        await using var context = _fixture.CreateStoreContextAsync();
        await context.ResetAsync();

        foreach (var transaction in Enumerable.Range(0, 20).Select(i => TestData.Transaction(occurredAt: TestData.Epoch.AddSeconds(i))))
        {
            await context.Store.UpsertAsync(transaction, CancellationToken.None);
        }

        var redis = await context.GetRedisAsync();
        await redis.KeyDeleteAsync([RedisHotWindow.RecentKey, RedisHotWindow.PayloadKey, RedisHotWindow.RebuildLockKey]);

        var snapshots = await Task.WhenAll(Enumerable.Range(0, readers).Select(_ =>
            context.Store.GetRecentAsync(100, CancellationToken.None)));

        // Every reader is served, and all of them agree.
        snapshots.Should().AllSatisfy(snapshot => snapshot.Should().HaveCount(20));
        snapshots.Select(s => string.Join(",", s.Select(t => t.TransactionId)))
            .Distinct()
            .Should().ContainSingle("every reader must see the same window");

        // The guard was taken, which is what stopped this from being ten rebuilds.
        (await redis.KeyExistsAsync(RedisHotWindow.RebuildLockKey)).Should().BeTrue();
    }

    [RequiresInfrastructureFact]
    public async Task UpsertAsync_StatusChange_MovesTheIndexEntryRatherThanAddingASecond()
    {
        await using var context = _fixture.CreateStoreContextAsync();
        await context.ResetAsync();

        var id = Guid.NewGuid();
        await context.Store.UpsertAsync(TestData.Transaction(id, occurredAt: TestData.Epoch), CancellationToken.None);
        await context.Store.UpsertAsync(
            TestData.Transaction(id, status: TransactionStatus.Completed, occurredAt: TestData.Epoch.AddMinutes(1)),
            CancellationToken.None);

        var redis = await context.GetRedisAsync();

        // ZADD on an existing member updates the score in place; this is the reason
        // the index is a sorted set (ADR-005).
        (await redis.SortedSetLengthAsync(RedisHotWindow.RecentKey)).Should().Be(1);
        (await redis.SortedSetScoreAsync(RedisHotWindow.RecentKey, id.ToString()))
            .Should().Be(TestData.Epoch.AddMinutes(1).ToUnixTimeMilliseconds());
    }
}
