namespace Rtfm.Core;

/// <summary>
/// The result of validating a <see cref="TransactionSubmission"/>: either a
/// domain <see cref="Transaction"/>, or the complete list of reasons it was
/// rejected. Never both, never neither.
/// </summary>
public sealed class ValidationOutcome
{
    private static readonly IReadOnlyList<ValidationFailure> NoFailures = Array.Empty<ValidationFailure>();

    private ValidationOutcome(Transaction? value, IReadOnlyList<ValidationFailure> failures)
    {
        Value = value;
        Failures = failures;
    }

    public Transaction? Value { get; }

    public IReadOnlyList<ValidationFailure> Failures { get; }

    public bool IsValid => Failures.Count == 0;

    /// <summary>The validated transaction. Throws if the outcome is a failure.</summary>
    public Transaction Transaction =>
        Value ?? throw new InvalidOperationException("Validation failed; there is no transaction to read.");

    public static ValidationOutcome Success(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new ValidationOutcome(transaction, NoFailures);
    }

    public static ValidationOutcome Failed(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("A failed outcome must carry at least one failure.", nameof(failures));
        }

        return new ValidationOutcome(value: null, failures);
    }
}
