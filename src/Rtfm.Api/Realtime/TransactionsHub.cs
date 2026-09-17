using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Rtfm.Api.Contracts;
using Rtfm.Core;
using Rtfm.Infrastructure.Options;

namespace Rtfm.Api.Realtime;

/// <summary>
/// The server-to-client contract. A typed hub means the broadcaster and the hub
/// cannot disagree about a method name, and the client's expectations are stated
/// in one place rather than as magic strings at each call site.
/// </summary>
public interface ITransactionsClient
{
    /// <summary>The hot window, sent once to a caller as it connects.</summary>
    Task ReceiveSnapshot(TransactionDto[] transactions);

    /// <summary>One transaction that was just created or moved to a terminal state.</summary>
    Task ReceiveTransaction(TransactionDto transaction);
}

/// <summary>
/// The dashboard's connection point (ADR-008).
/// </summary>
public sealed class TransactionsHub : Hub<ITransactionsClient>
{
    /// <summary>
    /// Every dashboard connection joins this one group. There is no partitioning:
    /// every agent watches the same stream, and a single group keeps the backplane
    /// message shape trivial. Partitioning is named in the README as deferred.
    /// </summary>
    public const string BroadcastGroup = "transactions";

    private readonly ITransactionStore _store;
    private readonly ILogger<TransactionsHub> _logger;
    private readonly int _snapshotSize;

    public TransactionsHub(ITransactionStore store, IOptions<HotWindowOptions> window, ILogger<TransactionsHub> logger)
    {
        _store = store;
        _logger = logger;
        _snapshotSize = window.Value.Size;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;

        // ORDER IS LOAD-BEARING (ADR-008).
        //
        // Subscribe first, snapshot second. The two steps cannot be made one atomic
        // unit across five replicas and a Redis backplane, so one of them has to
        // overlap the other, and the choice is between duplicates and gaps.
        //
        // Snapshot-then-subscribe loses every transaction broadcast in the gap, and
        // loses it silently: the dashboard shows a window that is simply missing
        // rows, with nothing to indicate it. Subscribe-then-snapshot can deliver the
        // same transaction twice, which the client neutralises for free because its
        // state is a Map keyed on transactionId (S5.2).
        //
        // Do not "fix" this by synchronising the two streams server-side: every
        // mechanism for that adds latency to all traffic to serve one edge case.
        await Groups.AddToGroupAsync(connectionId, BroadcastGroup, Context.ConnectionAborted);

        var recent = await _store.GetRecentAsync(_snapshotSize, Context.ConnectionAborted);
        var snapshot = recent.Select(TransactionDto.From).ToArray();

        await Clients.Caller.ReceiveSnapshot(snapshot);

        _logger.LogInformation(
            "Dashboard {ConnectionId} connected and received a snapshot of {Count} transactions.",
            connectionId,
            snapshot.Length);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("Dashboard {ConnectionId} disconnected.", Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception, "Dashboard {ConnectionId} disconnected with an error.", Context.ConnectionId);
        }

        // No explicit group removal: SignalR drops a closed connection from its
        // groups itself, and calling RemoveFromGroupAsync on a dead connection is
        // a round trip that can only fail.
        return base.OnDisconnectedAsync(exception);
    }
}
