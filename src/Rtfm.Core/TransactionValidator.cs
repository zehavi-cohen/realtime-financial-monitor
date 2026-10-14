using System.Globalization;

namespace Rtfm.Core;

/// <summary>
/// Turns a producer's assertion into a domain <see cref="Transaction"/>, or into
/// a complete list of field errors. Every field is checked on every call: a
/// producer fixing one field at a time across four round trips is a bad contract.
/// </summary>
public static class TransactionValidator
{
    /// <summary>Matches the CHAR(3) currency column. Format only - membership of ISO 4217 is not checked.</summary>
    private const int CurrencyLength = 3;

    /// <summary>Matches the scale of DECIMAL(19,4). More precision than the column holds is rejected, not rounded.</summary>
    private const int MaxAmountScale = 4;

    /// <summary>Matches the precision of DECIMAL(19,4): 15 integral digits.</summary>
    private const decimal MaxAmountExclusive = 1_000_000_000_000_000m;

    public static ValidationOutcome Validate(TransactionSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var failures = new List<ValidationFailure>();

        var transactionId = ValidateTransactionId(submission.TransactionId, failures);
        var amount = ValidateAmount(submission.Amount, failures);
        var currency = ValidateCurrency(submission.Currency, failures);
        var status = ValidateStatus(submission.Status, failures);
        var occurredAt = ValidateTimestamp(submission.Timestamp, failures);

        if (failures.Count > 0)
        {
            return ValidationOutcome.Failed(failures);
        }

        return ValidationOutcome.Success(new Transaction
        {
            TransactionId = transactionId!.Value,
            Amount = amount!.Value,
            Currency = currency!,
            Status = status!.Value,
            OccurredAt = occurredAt!.Value,
        });
    }

    private static Guid? ValidateTransactionId(string? raw, List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            failures.Add(new ValidationFailure(FieldNames.TransactionId, "transactionId is required."));
            return null;
        }

        if (!Guid.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            failures.Add(new ValidationFailure(FieldNames.TransactionId, "transactionId must be a GUID."));
            return null;
        }

        if (parsed == Guid.Empty)
        {
            failures.Add(new ValidationFailure(FieldNames.TransactionId, "transactionId must not be the empty GUID."));
            return null;
        }

        return parsed;
    }

    private static decimal? ValidateAmount(decimal? raw, List<ValidationFailure> failures)
    {
        if (raw is null)
        {
            failures.Add(new ValidationFailure(FieldNames.Amount, "amount is required."));
            return null;
        }

        var amount = raw.Value;

        if (amount <= 0m)
        {
            failures.Add(new ValidationFailure(FieldNames.Amount, "amount must be greater than zero."));
            return null;
        }

        if (amount >= MaxAmountExclusive)
        {
            failures.Add(new ValidationFailure(FieldNames.Amount, "amount exceeds the supported precision of 15 integral digits."));
            return null;
        }

        // Scale lives in bits 16-23 of the flags word of decimal's bit representation.
        // Rejecting rather than rounding: silently truncating money is how a system
        // loses a fraction of a cent per transaction and nobody notices for a year.
        var scale = (decimal.GetBits(amount)[3] >> 16) & 0xFF;
        if (scale > MaxAmountScale)
        {
            failures.Add(new ValidationFailure(FieldNames.Amount, $"amount must have no more than {MaxAmountScale} decimal places."));
            return null;
        }

        return amount;
    }

    private static string? ValidateCurrency(string? raw, List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            failures.Add(new ValidationFailure(FieldNames.Currency, "currency is required."));
            return null;
        }

        if (raw.Length != CurrencyLength || !IsAllUpperAscii(raw))
        {
            failures.Add(new ValidationFailure(FieldNames.Currency, "currency must be three upper-case letters, for example USD."));
            return null;
        }

        return raw;
    }

    private static bool IsAllUpperAscii(string value)
    {
        foreach (var c in value)
        {
            if (c < 'A' || c > 'Z')
            {
                return false;
            }
        }

        return true;
    }

    private static TransactionStatus? ValidateStatus(string? raw, List<ValidationFailure> failures)
    {
        // An explicit switch rather than Enum.TryParse: TryParse also accepts the
        // numeric forms ("0", "1", "2") and, with ignoreCase, any casing. The wire
        // contract is exactly these three strings.
        TransactionStatus? status = raw switch
        {
            "Pending" => TransactionStatus.Pending,
            "Completed" => TransactionStatus.Completed,
            "Failed" => TransactionStatus.Failed,
            _ => null,
        };

        if (status is null)
        {
            failures.Add(new ValidationFailure(FieldNames.Status, "status must be one of: Pending, Completed, Failed."));
        }

        return status;
    }

    private static DateTimeOffset? ValidateTimestamp(string? raw, List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            failures.Add(new ValidationFailure(FieldNames.Timestamp, "timestamp is required."));
            return null;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            failures.Add(new ValidationFailure(FieldNames.Timestamp, "timestamp must be an ISO-8601 instant, for example 2024-01-15T10:00:00Z."));
            return null;
        }

        return Normalise(parsed);
    }

    /// <summary>
    /// Normalise to UTC at millisecond resolution, once, here.
    /// DATETIME2(3) and the Redis sorted-set score are both millisecond-resolution
    /// UTC. If the domain object kept sub-millisecond precision, a value would not
    /// compare equal to itself after a round trip through either store.
    /// </summary>
    private static DateTimeOffset Normalise(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>
    /// Field names as they appear on the wire, so a problem-details response points
    /// at something the producer actually sent.
    /// </summary>
    public static class FieldNames
    {
        public const string TransactionId = "transactionId";
        public const string Amount = "amount";
        public const string Currency = "currency";
        public const string Status = "status";
        public const string Timestamp = "timestamp";
    }
}
