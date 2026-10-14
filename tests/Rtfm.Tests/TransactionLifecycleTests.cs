using Rtfm.Core;

namespace Rtfm.Tests;

/// <summary>
/// The state machine on its own. These assertions are duplicated in behaviour by
/// the contract suite, but stated here they document the rule directly - and if
/// the MERGE predicate and this type ever drift apart, the contract suite fails
/// for the SQL store while these still pass, which localises the bug immediately.
/// </summary>
public sealed class TransactionLifecycleTests
{
    [Theory]
    [InlineData(TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed)]
    public void CanTransition_FromPendingToTerminal_IsPermitted(TransactionStatus next)
    {
        TransactionLifecycle.CanTransition(TransactionStatus.Pending, next).Should().BeTrue();
    }

    [Fact]
    public void CanTransition_PendingToPending_IsNotATransition()
    {
        TransactionLifecycle.CanTransition(TransactionStatus.Pending, TransactionStatus.Pending).Should().BeFalse();
    }

    [Theory]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Failed)]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Failed)]
    public void CanTransition_FromATerminalState_IsNeverPermitted(TransactionStatus current, TransactionStatus next)
    {
        TransactionLifecycle.CanTransition(current, next).Should().BeFalse();
    }
}
