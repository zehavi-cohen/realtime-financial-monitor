namespace Rtfm.Core;

/// <summary>
/// The result of a full ingestion attempt: rejected with field errors, or accepted
/// with the transaction as it was stored and what the store did with it.
/// </summary>
public sealed class IngestionOutcome
{
    private static readonly IReadOnlyList<ValidationFailure> NoFailures = Array.Empty<ValidationFailure>();

    private IngestionOutcome(Transaction? transaction, IngestResult result, IReadOnlyList<ValidationFailure> failures)
    {
        Value = transaction;
        Result = result;
        Failures = failures;
    }

    public Transaction? Value { get; }

    public IngestResult Result { get; }

    public IReadOnlyList<ValidationFailure> Failures { get; }

    public bool IsAccepted => Failures.Count == 0;

    /// <summary>The stored transaction. Throws if the submission was rejected.</summary>
    public Transaction Transaction =>
        Value ?? throw new InvalidOperationException("The submission was rejected; there is no transaction to read.");

    public static IngestionOutcome Accepted(Transaction transaction, IngestResult result)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new IngestionOutcome(transaction, result, NoFailures);
    }

    public static IngestionOutcome Rejected(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("A rejected outcome must carry at least one failure.", nameof(failures));
        }

        return new IngestionOutcome(transaction: null, IngestResult.Ignored, failures);
    }
}
