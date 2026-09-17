using Rtfm.Core;

namespace Rtfm.Tests;

/// <summary>
/// Builders for test data. Every test states only the fields it cares about; the
/// rest come from here, so a test's subject is visible in its first two lines.
/// </summary>
public static class TestData
{
    /// <summary>A fixed instant, so nothing in the suite depends on the clock.</summary>
    public static readonly DateTimeOffset Epoch = new(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public static Transaction Transaction(
        Guid? id = null,
        decimal amount = 1500.50m,
        string currency = "USD",
        TransactionStatus status = TransactionStatus.Pending,
        DateTimeOffset? occurredAt = null) => new()
        {
            TransactionId = id ?? Guid.NewGuid(),
            Amount = amount,
            Currency = currency,
            Status = status,
            OccurredAt = occurredAt ?? Epoch,
        };

    /// <summary>
    /// Default marker for <see cref="Submission"/>'s id. A plain null default would
    /// make "give me a fresh id" and "send an explicitly null id" the same call,
    /// and the second is a case that has to be testable.
    /// </summary>
    public const string AutoId = "<auto>";

    public static TransactionSubmission Submission(
        string? transactionId = AutoId,
        decimal? amount = 1500.50m,
        string? currency = "USD",
        string? status = "Pending",
        string? timestamp = "2024-01-15T10:00:00Z") =>
        new(
            transactionId == AutoId ? Guid.NewGuid().ToString() : transactionId,
            amount,
            currency,
            status,
            timestamp);
}
