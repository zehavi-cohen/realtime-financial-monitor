namespace Rtfm.Core;

/// <summary>
/// An unvalidated assertion from a producer. Every field is a nullable primitive
/// because the whole point of this type is to carry input that may be wrong -
/// parsing it into <see cref="Transaction"/> is the validator's job, and doing
/// that parse in the JSON deserialiser instead would surface malformed input as
/// an opaque 400 rather than a named field error.
/// </summary>
public sealed record TransactionSubmission(
    string? TransactionId,
    decimal? Amount,
    string? Currency,
    string? Status,
    string? Timestamp);
