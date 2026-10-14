namespace Rtfm.Core;

/// <summary>
/// The legal state machine for a transaction.
/// </summary>
/// <remarks>
/// This is the in-process mirror of the guard encoded in the SQL MERGE statement
/// (ADR-003). The database is the enforcement point; this type exists so the
/// in-memory store can enforce the identical rule and so the rule can be unit
/// tested without a database. If the two ever disagree, the MERGE wins and this
/// type is the bug.
/// </remarks>
public static class TransactionLifecycle
{
    /// <summary>
    /// Only Pending -> Completed and Pending -> Failed are permitted.
    /// A terminal transaction never regresses, and Pending -> Pending is a no-op
    /// rather than an update, which is what makes an identical re-send free.
    /// </summary>
    public static bool CanTransition(TransactionStatus current, TransactionStatus next) =>
        current == TransactionStatus.Pending && next != TransactionStatus.Pending;
}
