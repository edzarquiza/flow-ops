using FlowOps.Domain.Attention;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Attention;

/// <summary>
/// Phase 30 (ADR-0035): <see cref="AttentionSuggestion"/> only reads signal codes and one
/// already-known fact (<see cref="Status"/>) — these tests confirm the priority order and the
/// Pending/InProgress wording split for <see cref="AttentionSignalCode.Stalled"/>, never that it
/// re-derives whether a signal applies (that remains <c>AttentionPolicy</c>'s job alone).
/// </summary>
public class AttentionSuggestionTests
{
    [Fact]
    public void SuggestNextStep_NoSignals_ReturnsNull()
    {
        var result = AttentionSuggestion.SuggestNextStep(Array.Empty<AttentionSignalCode>(), Status.InProgress);
        Assert.Null(result);
    }

    [Fact]
    public void SuggestNextStep_UnassignedUrgent_TakesPriorityOverEverythingElse()
    {
        var result = AttentionSuggestion.SuggestNextStep(
            [AttentionSignalCode.UnassignedUrgent, AttentionSignalCode.SlaBreached, AttentionSignalCode.Stalled],
            Status.Open);

        Assert.Equal("Assign an owner.", result);
    }

    [Theory]
    [InlineData(AttentionSignalCode.SlaBreached)]
    [InlineData(AttentionSignalCode.Overdue)]
    public void SuggestNextStep_SlaBreachedOrOverdue_ReviewCurrentStatus(AttentionSignalCode code)
    {
        var result = AttentionSuggestion.SuggestNextStep([code], Status.InProgress);
        Assert.Equal("Review the current status and update the ticket's next action.", result);
    }

    [Fact]
    public void SuggestNextStep_SlaBreachedOrOverdue_OutranksStalledAndSlaAtRisk()
    {
        var result = AttentionSuggestion.SuggestNextStep(
            [AttentionSignalCode.Stalled, AttentionSignalCode.SlaAtRisk, AttentionSignalCode.Overdue],
            Status.Pending);

        Assert.Equal("Review the current status and update the ticket's next action.", result);
    }

    [Fact]
    public void SuggestNextStep_StalledWhilePending_SuggestsReviewingThePendingReason()
    {
        var result = AttentionSuggestion.SuggestNextStep([AttentionSignalCode.Stalled], Status.Pending);
        Assert.Equal("Review the pending reason and determine whether work can resume.", result);
    }

    [Theory]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Open)]
    public void SuggestNextStep_StalledWhileNotPending_SuggestsCheckingActiveWork(Status status)
    {
        var result = AttentionSuggestion.SuggestNextStep([AttentionSignalCode.Stalled], status);
        Assert.Equal("Check whether the ticket is still actively being worked.", result);
    }

    [Fact]
    public void SuggestNextStep_Stalled_OutranksSlaAtRisk()
    {
        var result = AttentionSuggestion.SuggestNextStep(
            [AttentionSignalCode.SlaAtRisk, AttentionSignalCode.Stalled],
            Status.InProgress);

        Assert.Equal("Check whether the ticket is still actively being worked.", result);
    }

    [Fact]
    public void SuggestNextStep_SlaAtRiskAlone_SuggestsReviewingRemainingWorkAndOwner()
    {
        var result = AttentionSuggestion.SuggestNextStep([AttentionSignalCode.SlaAtRisk], Status.InProgress);
        Assert.Equal("Review remaining work and confirm the ticket has an active owner.", result);
    }

    [Theory]
    [InlineData(AttentionSignalCode.Aging)]
    [InlineData(AttentionSignalCode.Churn)]
    [InlineData(AttentionSignalCode.Reopened)]
    public void SuggestNextStep_NoMoreSpecificSignal_FallsBackToGenericReviewHistory(AttentionSignalCode code)
    {
        var result = AttentionSuggestion.SuggestNextStep([code], Status.InProgress);
        Assert.Equal("Review this ticket's history and confirm it still has a clear owner and plan.", result);
    }

    [Fact]
    public void SuggestNextStep_SignalObjectOverload_DelegatesToCodesOverload()
    {
        var signals = new List<AttentionSignal>
        {
            new(AttentionSignalCode.UnassignedUrgent, AttentionSeverity.Critical, "Unassigned and urgent", DateTimeOffset.UtcNow),
        };

        var result = AttentionSuggestion.SuggestNextStep(signals, Status.Open);
        Assert.Equal("Assign an owner.", result);
    }
}
