using Rtfm.Core;

namespace Rtfm.Tests;

/// <summary>
/// Validation is the system's only defence against a producer, so every rule gets
/// its own test and every test asserts one behaviour.
/// </summary>
public sealed class TransactionValidatorTests
{
    [Fact]
    public void Validate_WellFormedSubmission_ReturnsTransaction()
    {
        var id = Guid.NewGuid();

        var outcome = TransactionValidator.Validate(TestData.Submission(
            transactionId: id.ToString(),
            amount: 1500.50m,
            currency: "USD",
            status: "Completed",
            timestamp: "2024-01-15T10:00:00Z"));

        outcome.IsValid.Should().BeTrue();
        outcome.Transaction.Should().BeEquivalentTo(new Transaction
        {
            TransactionId = id,
            Amount = 1500.50m,
            Currency = "USD",
            Status = TransactionStatus.Completed,
            OccurredAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        });
    }

    // ---- amount ------------------------------------------------------------

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1500.50)]
    public void Validate_NegativeAmount_IsRejected(decimal amount)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: amount));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Amount);
    }

    [Fact]
    public void Validate_ZeroAmount_IsRejected()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: 0m));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Amount);
    }

    [Fact]
    public void Validate_MissingAmount_IsRejected()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: null));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Amount);
    }

    [Fact]
    public void Validate_AmountBeyondColumnScale_IsRejected()
    {
        // DECIMAL(19,4). Accepting this would silently round money.
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: 10.000005m));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Amount);
    }

    [Fact]
    public void Validate_AmountAtColumnScale_IsAccepted()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: 10.0001m));

        outcome.IsValid.Should().BeTrue();
    }

    // ---- status ------------------------------------------------------------

    [Theory]
    [InlineData("Pending")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public void Validate_KnownStatus_IsAccepted(string status)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(status: status));

        outcome.IsValid.Should().BeTrue();
        outcome.Transaction.Status.ToString().Should().Be(status);
    }

    [Theory]
    [InlineData("Success")]      // the brief's UI wording; not part of the data model
    [InlineData("completed")]    // exact casing is part of the contract
    [InlineData("1")]            // Enum.TryParse would have accepted this
    [InlineData("")]
    [InlineData(null)]
    public void Validate_UnknownStatus_IsRejected(string? status)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(status: status));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Status);
    }

    // ---- transactionId -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_EmptyTransactionId_IsRejected(string? id)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(transactionId: id));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.TransactionId);
    }

    [Fact]
    public void Validate_NonGuidTransactionId_IsRejected()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(transactionId: "not-a-guid"));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.TransactionId);
    }

    [Fact]
    public void Validate_EmptyGuidTransactionId_IsRejected()
    {
        // Guid.Empty parses, but as an identity it means "unset", and accepting it
        // would collapse every such producer bug onto one row.
        var outcome = TransactionValidator.Validate(TestData.Submission(transactionId: Guid.Empty.ToString()));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.TransactionId);
    }

    // ---- timestamp ---------------------------------------------------------

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2024-13-45T99:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_MalformedTimestamp_IsRejected(string? timestamp)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(timestamp: timestamp));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Timestamp);
    }

    [Fact]
    public void Validate_TimestampWithOffset_IsNormalisedToUtc()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(timestamp: "2024-01-15T12:00:00+02:00"));

        outcome.IsValid.Should().BeTrue();
        outcome.Transaction.OccurredAt.Should().Be(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Validate_SubMillisecondTimestamp_IsTruncatedToStorageResolution()
    {
        // DATETIME2(3) and the Redis score are both millisecond-resolution. Without
        // this, a transaction would not compare equal to itself after a round trip.
        var outcome = TransactionValidator.Validate(TestData.Submission(timestamp: "2024-01-15T10:00:00.1239999Z"));

        outcome.IsValid.Should().BeTrue();
        outcome.Transaction.OccurredAt.Should().Be(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, 123, TimeSpan.Zero));
    }

    // ---- currency ----------------------------------------------------------

    [Theory]
    [InlineData("US")]       // too short for CHAR(3)
    [InlineData("USDD")]     // too long
    [InlineData("usd")]      // wrong case
    [InlineData("U$D")]      // not letters
    [InlineData("")]
    [InlineData(null)]
    public void Validate_UnknownCurrencyFormat_IsRejected(string? currency)
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(currency: currency));

        outcome.Should().BeRejectedOn(TransactionValidator.FieldNames.Currency);
    }

    // ---- reporting ---------------------------------------------------------

    [Fact]
    public void Validate_SeveralBadFields_ReportsAllOfThem()
    {
        // A producer should not need four round trips to learn about four mistakes.
        var outcome = TransactionValidator.Validate(new TransactionSubmission(
            TransactionId: "nope",
            Amount: -1m,
            Currency: "eur",
            Status: "Success",
            Timestamp: "soon"));

        outcome.IsValid.Should().BeFalse();
        outcome.Failures.Select(f => f.Field).Should().BeEquivalentTo(
        [
            TransactionValidator.FieldNames.TransactionId,
            TransactionValidator.FieldNames.Amount,
            TransactionValidator.FieldNames.Currency,
            TransactionValidator.FieldNames.Status,
            TransactionValidator.FieldNames.Timestamp,
        ]);
    }

    [Fact]
    public void Transaction_OnAFailedOutcome_Throws()
    {
        var outcome = TransactionValidator.Validate(TestData.Submission(amount: -1m));

        var read = () => outcome.Transaction;

        read.Should().Throw<InvalidOperationException>();
    }
}
