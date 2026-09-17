using System.Text.Json;
using System.Text.Json.Serialization;
using Rtfm.Core;

namespace Rtfm.Infrastructure.Redis;

/// <summary>
/// The wire shape of a transaction inside the <c>tx:payload</c> hash.
/// </summary>
/// <remarks>
/// Separate from the domain type on purpose. This is a persisted format: once a
/// value is in Redis, changing the domain record's shape must not silently change
/// how existing entries deserialise. OccurredAt is stored as epoch milliseconds so
/// the payload and the sorted-set score are literally the same number, with no
/// date parsing and no time zone on the read path.
/// </remarks>
internal sealed record HotWindowPayload(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("occurredAtMs")] long OccurredAtMs)
{
    public static HotWindowPayload From(Transaction transaction) => new(
        transaction.TransactionId,
        transaction.Amount,
        transaction.Currency,
        transaction.Status.ToString(),
        transaction.OccurredAtEpochMs);

    public Transaction ToDomain() => new()
    {
        TransactionId = Id,
        Amount = Amount,
        Currency = Currency,
        Status = Enum.Parse<TransactionStatus>(Status),
        OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(OccurredAtMs),
    };

    public string ToJson() => JsonSerializer.Serialize(this, HotWindowJsonContext.Default.HotWindowPayload);

    public static HotWindowPayload? FromJson(string json) =>
        JsonSerializer.Deserialize(json, HotWindowJsonContext.Default.HotWindowPayload);
}

/// <summary>
/// Source-generated serialisation. This runs once per ingested transaction on
/// every replica; reflection-based serialisation is measurable here, and the
/// generated context also makes the type trim- and AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HotWindowPayload))]
internal sealed partial class HotWindowJsonContext : JsonSerializerContext;
