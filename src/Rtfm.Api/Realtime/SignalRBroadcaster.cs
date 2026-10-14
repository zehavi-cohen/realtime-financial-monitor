using Microsoft.AspNetCore.SignalR;
using Rtfm.Api.Contracts;
using Rtfm.Core;

namespace Rtfm.Api.Realtime;

/// <summary>
/// The <see cref="IBroadcaster"/> implementation. The only place in the system that
/// knows the real-time transport is SignalR.
/// </summary>
/// <remarks>
/// With the Redis backplane, this call publishes to a Redis channel; every replica
/// receives it and forwards it to its own local connections. No replica addresses
/// another, which is what makes the replica count irrelevant to the application
/// (ADR-008).
/// </remarks>
public sealed class SignalRBroadcaster : IBroadcaster
{
    private readonly IHubContext<TransactionsHub, ITransactionsClient> _hub;
    private readonly ILogger<SignalRBroadcaster> _logger;

    public SignalRBroadcaster(IHubContext<TransactionsHub, ITransactionsClient> hub, ILogger<SignalRBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Deliberately not passing the request's CancellationToken: this is a
        // broadcast to every dashboard, and the producer hanging up its HTTP
        // connection is no reason to stop delivering to agents watching screens.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _hub.Clients
                .Group(TransactionsHub.BroadcastGroup)
                .ReceiveTransaction(TransactionDto.From(transaction));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Delivery is at-most-once by design. The transaction is already durable
            // by the time this runs (ADR-002), so a backplane failure must degrade
            // real-time delivery, not fail an ingestion that has already succeeded -
            // telling the producer it failed would invite a retry of something that
            // worked. Dashboards recover their view on reconnect, when the snapshot
            // is re-sent. Recorded in the README under Known limitations.
            _logger.LogError(
                ex,
                "Broadcast failed for {TransactionId}; the transaction is persisted and real-time delivery is degraded.",
                transaction.TransactionId);
        }
    }
}
