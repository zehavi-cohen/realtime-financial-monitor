using System.Text.Json.Serialization;
using Rtfm.Core;

namespace Rtfm.Api.Contracts;

/// <summary>
/// The wire request. Every field is nullable and the timestamp is a string,
/// because this type must be able to represent input that is wrong.
/// </summary>
/// <remarks>
/// Binding <c>timestamp</c> as <see cref="DateTimeOffset"/> would make a malformed
/// value a deserialiser fault, which surfaces as an opaque 400 that does not name
/// the offending field - exactly what S10 requires the API not to do.
/// </remarks>
public sealed record CreateTransactionRequest
{
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    public TransactionSubmission ToSubmission() =>
        new(TransactionId, Amount, Currency, Status, Timestamp);
}

/// <summary>
/// The wire representation of a transaction, used by both the REST response and
/// the hub. Explicit and separate from the domain record: the domain type is free
/// to change shape, the contract is not (S10).
/// </summary>
public sealed record TransactionDto
{
    [JsonPropertyName("transactionId")]
    public required Guid TransactionId { get; init; }

    [JsonPropertyName("amount")]
    public required decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public static TransactionDto From(Transaction transaction) => new()
    {
        TransactionId = transaction.TransactionId,
        Amount = transaction.Amount,
        Currency = transaction.Currency,
        Status = transaction.Status.ToString(),
        Timestamp = transaction.OccurredAt,
    };
}

/// <summary>
/// The ingestion response. <see cref="Result"/> is on the body rather than
/// inferred from the status code because the producer often wants to know that its
/// retry was a no-op, and a 200 alone does not say that.
/// </summary>
public sealed record IngestionResponse
{
    /// <summary>One of: Created, Updated, Ignored.</summary>
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    /// <summary>The identity the assertion was about. Present on every outcome.</summary>
    [JsonPropertyName("transactionId")]
    public required Guid TransactionId { get; init; }

    /// <summary>
    /// The stored transaction - present only on Created and Updated.
    /// </summary>
    /// <remarks>
    /// Omitted for Ignored on purpose. On Created and Updated the submitted values
    /// are exactly the values the MERGE wrote, so echoing them is accurate. On
    /// Ignored nothing was written, and echoing the submission back under the name
    /// "transaction" would tell a producer that a rejected status change had been
    /// stored. Reading the real stored row would cost a query on the path whose
    /// whole point is that it did no work.
    /// </remarks>
    [JsonPropertyName("transaction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransactionDto? Transaction { get; init; }

    public static IngestionResponse From(Transaction transaction, IngestResult result) => new()
    {
        Result = result.ToString(),
        TransactionId = transaction.TransactionId,
        Transaction = result is IngestResult.Ignored ? null : TransactionDto.From(transaction),
    };
}
