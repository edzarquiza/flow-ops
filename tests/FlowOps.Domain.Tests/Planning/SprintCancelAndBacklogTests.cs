using FlowOps.Domain;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Planning;

/// <summary>ADR-0030: sprint cancellation and the return-to-backlog planning operation.</summary>
public class SprintCancelAndBacklogTests
{
    private static Sprint NewSprint() => Sprint.Create(7, "Week", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27), Now);

    private static Ticket ProjectTicket() => Ticket.Create(
        "Migrate reporting database", "Move the reporting database to the new cluster.",
        WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, projectId: 7, 1440, Now);

    [Fact]
    public void Cancel_PlannedSprint_BecomesCancelledAndStopsAcceptingTickets()
    {
        var sprint = NewSprint();
        sprint.Cancel(Now.AddHours(1));

        Assert.Equal(SprintStatus.Cancelled, sprint.Status);
        Assert.Equal(Now.AddHours(1), sprint.CancelledAt);
        Assert.False(sprint.AcceptsTickets);
    }

    [Fact] // SPRINT-INV-07: an active sprint is completed, a completed or cancelled one is history.
    public void Cancel_FromAnyOtherState_IsRejected()
    {
        var active = NewSprint(); active.Activate(Now);
        var completed = NewSprint(); completed.Activate(Now); completed.Complete(Now);
        var cancelled = NewSprint(); cancelled.Cancel(Now);

        foreach (var sprint in new[] { active, completed, cancelled })
        {
            Assert.Equal("SPRINT-INV-07", Assert.Throws<DomainRuleException>(() => sprint.Cancel(Now)).RuleCode);
        }
    }

    [Fact]
    public void Cancelled_CannotBeStartedOrCompleted()
    {
        var sprint = NewSprint(); sprint.Cancel(Now);
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => sprint.Activate(Now)).RuleCode);
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => sprint.Complete(Now)).RuleCode);
    }

    [Fact]
    public void MoveToSprint_CancelledSprint_IsRejected()
    {
        var sprint = NewSprint(); sprint.Cancel(Now);
        var ticket = ProjectTicket();

        var ex = Assert.Throws<DomainRuleException>(() => ticket.MoveToSprint(11, sprint.AcceptsTickets, ManagerId, Now));
        Assert.Equal("SPRINT-INV-04", ex.RuleCode);
    }

    [Fact]
    public void ReturnToSprintBacklog_AfterPull_RestoresBacklog_WithoutTouchingStatus()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);
        ticket.PullFromSprintBacklog(ManagerId, Now);
        var events = ticket.Events.Count;

        ticket.ReturnToSprintBacklog(ManagerId, Now);

        Assert.True(ticket.SprintBacklog);
        Assert.Equal(Status.Open, ticket.Status);
        Assert.Equal(events + 1, ticket.Events.Count); // TICKET-INV-09
        Assert.Equal(TicketEventType.SprintChanged, ticket.Events.Last().EventType);
    }

    [Fact]
    public void ReturnToSprintBacklog_AlreadyInBacklog_OrNoSprint_OrStarted_IsRejected()
    {
        var inBacklog = ProjectTicket(); inBacklog.MoveToSprint(11, true, ManagerId, Now);
        Assert.Equal("TICKET-INV-13", Assert.Throws<DomainRuleException>(() => inBacklog.ReturnToSprintBacklog(ManagerId, Now)).RuleCode);

        var noSprint = ProjectTicket();
        Assert.Equal("TICKET-INV-13", Assert.Throws<DomainRuleException>(() => noSprint.ReturnToSprintBacklog(ManagerId, Now)).RuleCode);

        var started = ProjectTicket();
        started.MoveToSprint(11, true, ManagerId, Now);
        started.PullFromSprintBacklog(ManagerId, Now);
        started.Assign(AssigneeId, true, ManagerId, Now);
        started.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now);
        Assert.Equal("TICKET-INV-13", Assert.Throws<DomainRuleException>(() => started.ReturnToSprintBacklog(ManagerId, Now)).RuleCode);
    }

    [Fact]
    public void Snapshot_WasDone_IsTerminalStatusOnly()
    {
        Assert.True(new SprintTicketSnapshot(1, 1, Status.Resolved).WasDone);
        Assert.True(new SprintTicketSnapshot(1, 1, Status.Closed).WasDone);
        Assert.False(new SprintTicketSnapshot(1, 1, Status.InProgress).WasDone);
        Assert.False(new SprintTicketSnapshot(1, 1, Status.Pending).WasDone);
    }
}
