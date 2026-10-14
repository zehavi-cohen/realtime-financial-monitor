namespace Rtfm.Core;

/// <summary>
/// The complete set of states a transaction can be in.
/// </summary>
/// <remarks>
/// Producers and UI copy sometimes say "Success" for the terminal success state.
/// This model defines "Completed", and "Completed" is the only value the API
/// accepts - "Success" is a 400. See README, "Deliberate deviations".
/// </remarks>
public enum TransactionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}
