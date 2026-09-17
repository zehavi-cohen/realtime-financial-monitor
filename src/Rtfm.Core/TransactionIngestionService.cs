namespace Rtfm.Core;

/// <summary>
/// The ingestion pipeline. This type owns the order in ADR-002 -
/// validate, persist, project, broadcast - and nothing else does.
/// </summary>
/// <remarks>
/// The HTTP endpoint translates a request into a <see cref="TransactionSubmission"/>
/// and an <see cref="IngestionOutcome"/> into a status code; it holds no rules of
/// its own. That is what makes this pipeline testable with no web host.
/// </remarks>
public sealed class TransactionIngestionService
{
    private readonly ITransactionStore _store;
    private readonly IBroadcaster _broadcaster;

    public TransactionIngestionService(ITransactionStore store, IBroadcaster broadcaster)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    public async Task<IngestionOutcome> IngestAsync(TransactionSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var validation = TransactionValidator.Validate(submission);
        if (!validation.IsValid)
        {
            return IngestionOutcome.Rejected(validation.Failures);
        }

        var transaction = validation.Transaction;

        // Persist before broadcasting, always. An agent seeing a transaction on
        // screen that does not survive a restart is a correctness failure in a
        // financial monitoring context, not a latency trade-off (ADR-002).
        var result = await _store.UpsertAsync(transaction, cancellationToken);

        // Ignored means the message asserted nothing new - a duplicate, or an
        // illegal transition. Broadcasting it would put a stale status back on
        // every dashboard.
        if (result is IngestResult.Created or IngestResult.Updated)
        {
            await _broadcaster.BroadcastAsync(transaction, cancellationToken);
        }

        return IngestionOutcome.Accepted(transaction, result);
    }

    public Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return _store.GetRecentAsync(limit, cancellationToken);
    }
}
