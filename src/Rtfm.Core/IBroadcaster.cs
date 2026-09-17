namespace Rtfm.Core;

/// <summary>
/// The outbound real-time port. Implemented in the API layer over SignalR; kept
/// as an interface here so the ingestion rule "broadcast on Created and Updated,
/// never on Ignored" can be asserted without a hub, a socket, or a Redis backplane.
/// </summary>
public interface IBroadcaster
{
    /// <summary>
    /// Pushes a transaction to every connected dashboard.
    /// </summary>
    /// <remarks>
    /// Delivery is at-most-once and best-effort by design: the transaction is
    /// already durable by the time this is called (ADR-002), so an implementation
    /// that cannot reach its transport must degrade rather than fail the ingestion.
    /// </remarks>
    Task BroadcastAsync(Transaction transaction, CancellationToken cancellationToken);
}
