namespace Rtfm.Core;

/// <summary>
/// The complete set of states a transaction can be in.
/// </summary>
/// <remarks>
/// The brief's UI section mentions "Success"; the data model defines "Completed".
/// "Completed" is authoritative here - see README, "Deviations from the brief".
/// </remarks>
public enum TransactionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}
