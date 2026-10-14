using Rtfm.Infrastructure.InMemory;

namespace Rtfm.Tests.Contracts;

/// <summary>
/// Runs the full <see cref="Rtfm.Core.ITransactionStore"/> contract against the
/// in-memory implementation. No infrastructure, so this runs under plain
/// <c>dotnet test</c> - which is what makes the contract cheap enough to keep green.
/// </summary>
public sealed class InMemoryTransactionStoreContractTests : TransactionStoreContractTests
{
    protected override Task<StoreHandle> CreateStoreAsync(int windowSize, CancellationToken cancellationToken)
    {
        var store = new InMemoryTransactionStore(windowSize);
        return Task.FromResult(new StoreHandle(store, () =>
        {
            store.Dispose();
            return ValueTask.CompletedTask;
        }));
    }
}
