using FlowOps.Application.Planning;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Application.Tests.Planning;

/// <summary>ADR-0030: a board drag is made only of operations that already exist as explicit
/// actions. The planner invents no transition; anything the existing workflow does not allow is a
/// rejection with a message.</summary>
public class BoardMovePlannerTests
{
    private static BoardStep[] Steps(Status s, bool backlog, BoardColumnKey t)
    {
        var plan = BoardMovePlanner.Plan(s, backlog, t);
        Assert.True(plan.IsAllowed, plan.Rejection);
        return [.. plan.Steps];
    }

    [Fact]
    public void SameColumn_IsANoOp() => Assert.Empty(Steps(Status.InProgress, false, BoardColumnKey.InProgress));

    [Fact]
    public void Backlog_To_InProgress_ClearsTheFlagAssignsAndStarts() =>
        Assert.Equal([BoardStep.ClearBacklogFlagIfPermitted, BoardStep.AssignToCaller, BoardStep.StartWork], Steps(Status.Open, true, BoardColumnKey.InProgress));

    [Fact]
    public void Open_To_InProgress_AssignsAndStarts() =>
        Assert.Equal([BoardStep.AssignToCaller, BoardStep.StartWork], Steps(Status.Open, false, BoardColumnKey.InProgress));

    [Fact]
    public void Assigned_To_InProgress_JustStartsWork() =>
        Assert.Equal([BoardStep.StartWork], Steps(Status.Assigned, false, BoardColumnKey.InProgress));

    [Fact]
    public void Backlog_To_Open_PullsFromBacklog() =>
        Assert.Equal([BoardStep.PullFromBacklog], Steps(Status.Open, true, BoardColumnKey.Open));

    [Fact]
    public void Open_To_Backlog_ReturnsToBacklog() =>
        Assert.Equal([BoardStep.ReturnToBacklog], Steps(Status.Assigned, false, BoardColumnKey.Backlog));

    [Fact]
    public void InProgress_To_Pending_PutsOnHold() =>
        Assert.Equal([BoardStep.PutOnHold], Steps(Status.InProgress, false, BoardColumnKey.Pending));

    [Fact]
    public void Pending_To_InProgress_Resumes() =>
        Assert.Equal([BoardStep.Resume, BoardStep.StartWorkIfAssigned], Steps(Status.Pending, false, BoardColumnKey.InProgress));

    [Theory]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Pending)]
    public void Started_To_Done_Resolves(Status status) =>
        Assert.Equal([BoardStep.Resolve], Steps(status, false, BoardColumnKey.Done));

    [Theory]
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void Done_To_Open_Reopens(Status status) =>
        Assert.Equal([BoardStep.Reopen], Steps(status, false, BoardColumnKey.Open));

    [Theory] // Each of these would need a workflow transition that does not exist — rejected, not invented.
    [InlineData(Status.InProgress, false, BoardColumnKey.Backlog)]
    [InlineData(Status.InProgress, false, BoardColumnKey.Open)]
    [InlineData(Status.Pending, false, BoardColumnKey.Open)]
    [InlineData(Status.Pending, false, BoardColumnKey.Backlog)]
    [InlineData(Status.Open, false, BoardColumnKey.Pending)]
    [InlineData(Status.Open, true, BoardColumnKey.Done)]
    [InlineData(Status.Assigned, false, BoardColumnKey.Done)]
    [InlineData(Status.Resolved, false, BoardColumnKey.InProgress)]
    [InlineData(Status.Closed, false, BoardColumnKey.Pending)]
    [InlineData(Status.Resolved, false, BoardColumnKey.Backlog)]
    public void UnsupportedMoves_AreRejectedWithAMessage(Status status, bool backlog, BoardColumnKey target)
    {
        var plan = BoardMovePlanner.Plan(status, backlog, target);
        Assert.False(plan.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(plan.Rejection));
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void NeedsInput_OnlyForHoldResolveAndReopen()
    {
        Assert.True(BoardMovePlanner.Plan(Status.InProgress, false, BoardColumnKey.Pending).NeedsInput);
        Assert.True(BoardMovePlanner.Plan(Status.InProgress, false, BoardColumnKey.Done).NeedsInput);
        Assert.True(BoardMovePlanner.Plan(Status.Resolved, false, BoardColumnKey.Open).NeedsInput);
        Assert.False(BoardMovePlanner.Plan(Status.Open, false, BoardColumnKey.InProgress).NeedsInput);
        Assert.False(BoardMovePlanner.Plan(Status.Open, true, BoardColumnKey.Open).NeedsInput);
    }


    [Fact] // The page's highlighting and the server's plan can never disagree: same function.
    public void AllowedTargets_AgreesWithPlan()
    {
        foreach (var status in Enum.GetValues<Status>())
        {
            foreach (var backlog in new[] { false, true })
            {
                var allowed = BoardMovePlanner.AllowedTargets(status, backlog);
                foreach (var target in Enum.GetValues<BoardColumnKey>())
                {
                    var expected = BoardColumns.For(status, backlog) != target && BoardMovePlanner.Plan(status, backlog, target).IsAllowed;
                    Assert.Equal(expected, allowed.Contains(target));
                }
            }
        }
    }
}
