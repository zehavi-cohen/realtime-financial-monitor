using Rtfm.Tests.Contracts;

namespace Rtfm.IntegrationTests;

/// <summary>
/// The same contract suite as the in-memory store, run against SQL Server and Redis.
/// </summary>
/// <remarks>
/// This is what makes the in-memory implementation defensible: if both pass this
/// unchanged, then switching <c>Storage</c> changes performance and durability, not
/// behaviour, and a test that passes against one is evidence about the other.
///
/// The <see cref="FactAttribute"/> instances inherited from the base class are not
/// gated, so this class is skipped as a whole by
/// <see cref="Infrastructure.SkipUnlessEnabled"/> on the collection - see
/// <c>SkipUnlessInfrastructureEnabled</c> below.
/// </remarks>
[Collection(InfrastructureCollection.Name)]
public sealed class SqlRedisTransactionStoreContractTests : TransactionStoreContractTests
{
    private readonly InfrastructureFixture _fixture;

    public SqlRedisTransactionStoreContractTests(InfrastructureFixture fixture) => _fixture = fixture;

    protected override async Task<StoreHandle> CreateStoreAsync(int windowSize, CancellationToken cancellationToken)
    {
        SkipUnlessInfrastructureEnabled();

        var context = _fixture.CreateStoreContextAsync(windowSize);
        await context.ResetAsync();

        return new StoreHandle(context.Store, context.DisposeAsync);
    }

    /// <summary>
    /// The contract tests are inherited with plain <c>[Fact]</c>, so they cannot be
    /// skipped at discovery. Throwing here means that with no Docker they fail fast
    /// with a clear reason rather than hanging on a container pull - and that
    /// <c>dotnet test</c> at the solution root never reaches a container at all,
    /// because this project is excluded from it (see README).
    /// </summary>
    private static void SkipUnlessInfrastructureEnabled()
    {
        if (!Infrastructure.IsEnabled)
        {
            throw new InvalidOperationException(Infrastructure.SkipReason);
        }
    }
}
