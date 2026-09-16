using FlowOps.Domain.Accounts;
using Xunit;

namespace FlowOps.Domain.Tests.Accounts;

/// <summary>Phase 24A / ADR-0024 (extended by ADR-0025 for Reject)'s full state x transition truth
/// table, unit-tested exhaustively — the same discipline CLAUDE.md §15 requires of every other
/// Domain access policy.</summary>
public class AccountLifecyclePolicyTests
{
    [Theory]
    [InlineData(AccountStatus.Pending, true)]
    [InlineData(AccountStatus.Active, false)]
    [InlineData(AccountStatus.Inactive, false)]
    [InlineData(AccountStatus.Rejected, false)]
    public void CanApprove_OnlyFromPending(AccountStatus current, bool expected) =>
        Assert.Equal(expected, AccountLifecyclePolicy.CanApprove(current));

    [Theory]
    [InlineData(AccountStatus.Pending, true)]
    [InlineData(AccountStatus.Active, false)]
    [InlineData(AccountStatus.Inactive, false)]
    [InlineData(AccountStatus.Rejected, false)] // idempotency is handled by the caller, not the policy — this only asks "is this a legal first transition."
    public void CanReject_OnlyFromPending(AccountStatus current, bool expected) =>
        Assert.Equal(expected, AccountLifecyclePolicy.CanReject(current));

    [Theory]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.Pending, false)]
    [InlineData(AccountStatus.Inactive, false)]
    [InlineData(AccountStatus.Rejected, false)]
    public void CanDeactivate_OnlyFromActive(AccountStatus current, bool expected) =>
        Assert.Equal(expected, AccountLifecyclePolicy.CanDeactivate(current));

    [Theory]
    [InlineData(AccountStatus.Inactive, true)]
    [InlineData(AccountStatus.Pending, false)] // the critical guard: Reactivate must never approve a Pending account.
    [InlineData(AccountStatus.Active, false)]
    [InlineData(AccountStatus.Rejected, false)] // Rejected is terminal — Reactivate must never revive it either.
    public void CanReactivate_OnlyFromInactive_NeverFromPendingOrRejected(AccountStatus current, bool expected) =>
        Assert.Equal(expected, AccountLifecyclePolicy.CanReactivate(current));

    [Theory] // Rejected has no outgoing transition at all — it is terminal by design (ADR-0025).
    [InlineData(AccountStatus.Rejected)]
    public void Rejected_HasNoLegalOutgoingTransition(AccountStatus current)
    {
        Assert.False(AccountLifecyclePolicy.CanApprove(current));
        Assert.False(AccountLifecyclePolicy.CanReject(current));
        Assert.False(AccountLifecyclePolicy.CanDeactivate(current));
        Assert.False(AccountLifecyclePolicy.CanReactivate(current));
    }
}
