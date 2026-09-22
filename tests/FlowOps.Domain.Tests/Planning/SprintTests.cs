using FlowOps.Domain;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Planning;

/// <summary>ADR-0029: sprint lifecycle and the ticket sprint-membership rules. Sprint membership
/// must never touch workflow status, SLA, assignment, or dates.</summary>
public class SprintTests
{
    private static readonly DateOnly Mon = new(2026, 9, 21);
    private static readonly DateOnly Sun = new(2026, 9, 27);

    private static Sprint NewSprint() => Sprint.Create(7, "Week of Sep 21", Mon, Sun, Now);

    private static Ticket ProjectTicket() => Ticket.Create(
        "Migrate reporting database", "Move the reporting database to the new cluster.",
        WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, projectId: 7, 1440, Now);

    // ---- Sprint ----

    [Fact]
    public void Create_ValidSprint_StartsPlanned()
    {
        var sprint = NewSprint();
        Assert.Equal(SprintStatus.Planned, sprint.Status);
        Assert.Equal("Week of Sep 21", sprint.Name);
        Assert.True(sprint.AcceptsTickets);
    }

    [Fact]
    public void Create_SingleDaySprint_IsValid() => Assert.NotNull(Sprint.Create(7, "One day", Mon, Mon, Now));

    [Fact] // SPRINT-INV-02
    public void Create_StartAfterEnd_IsRejected()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Sprint.Create(7, "Backwards", Sun, Mon, Now));
        Assert.Equal("SPRINT-INV-02", ex.RuleCode);
    }

    [Theory] // SPRINT-INV-01
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_IsRejected(string name)
    {
        var ex = Assert.Throws<DomainRuleException>(() => Sprint.Create(7, name, Mon, Sun, Now));
        Assert.Equal("SPRINT-INV-01", ex.RuleCode);
    }

    [Fact]
    public void Create_NameTooLong_IsRejected() =>
        Assert.Equal("SPRINT-INV-01", Assert.Throws<DomainRuleException>(() => Sprint.Create(7, new string('x', 81), Mon, Sun, Now)).RuleCode);

    [Fact]
    public void Lifecycle_PlannedToActiveToCompleted()
    {
        var sprint = NewSprint();
        sprint.Activate(Now);
        Assert.Equal(SprintStatus.Active, sprint.Status);
        Assert.Equal(Now, sprint.ActivatedAt);

        sprint.Complete(Now.AddDays(7));
        Assert.Equal(SprintStatus.Completed, sprint.Status);
        Assert.Equal(Now.AddDays(7), sprint.CompletedAt);
        Assert.False(sprint.AcceptsTickets);
    }

    [Fact] // SPRINT-INV-03: no skipping, no going back, no double-complete.
    public void Lifecycle_IllegalTransitions_AreRejected()
    {
        var planned = NewSprint();
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => planned.Complete(Now)).RuleCode);

        var active = NewSprint();
        active.Activate(Now);
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => active.Activate(Now)).RuleCode);

        active.Complete(Now);
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => active.Complete(Now)).RuleCode);
        Assert.Equal("SPRINT-INV-03", Assert.Throws<DomainRuleException>(() => active.Activate(Now)).RuleCode);
    }

    [Theory]
    [InlineData(2026, 9, 27, 2026, 10, 3, true)]   // shares the last day
    [InlineData(2026, 9, 28, 2026, 10, 4, false)]  // adjacent week
    [InlineData(2026, 9, 10, 2026, 9, 21, true)]   // shares the first day
    [InlineData(2026, 9, 22, 2026, 9, 23, true)]   // contained
    public void Overlaps_ChecksInclusiveRanges(int sy, int sm, int sd, int ey, int em, int ed, bool expected) =>
        Assert.Equal(expected, NewSprint().Overlaps(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed)));

    // ---- Ticket membership ----

    [Fact]
    public void MoveToSprint_SetsMembershipAndBacklog_WithoutTouchingWorkflow()
    {
        var ticket = ProjectTicket();
        var slaDue = ticket.SlaDueAt;
        var events = ticket.Events.Count;

        ticket.MoveToSprint(11, sprintAcceptsTickets: true, ManagerId, Now.AddHours(1));

        Assert.Equal(11, ticket.SprintId);
        Assert.True(ticket.SprintBacklog);
        Assert.Equal(Status.Open, ticket.Status);
        Assert.Equal(slaDue, ticket.SlaDueAt);
        Assert.Null(ticket.AssigneeId);
        Assert.Equal(events + 1, ticket.Events.Count); // TICKET-INV-09: exactly one event
        var last = ticket.Events.Last();
        Assert.Equal(TicketEventType.SprintChanged, last.EventType);
        Assert.Equal("11", last.NewValue);
    }

    [Fact]
    public void MoveToSprint_BetweenSprints_ReplacesMembership_AndRecordsBothIds()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);
        ticket.PullFromSprintBacklog(ManagerId, Now);

        ticket.MoveToSprint(12, true, ManagerId, Now);

        Assert.Equal(12, ticket.SprintId);
        Assert.True(ticket.SprintBacklog); // a fresh sprint starts in its backlog
        var last = ticket.Events.Last();
        Assert.Equal("11", last.OldValue);
        Assert.Equal("12", last.NewValue);
    }

    [Fact]
    public void MoveToSprint_Null_RemovesFromSprint()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);

        ticket.MoveToSprint(null, true, ManagerId, Now);

        Assert.Null(ticket.SprintId);
        Assert.False(ticket.SprintBacklog);
    }

    [Fact]
    public void MoveToSprint_SameSprint_IsANoOpWithNoEvent()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);
        var events = ticket.Events.Count;

        ticket.MoveToSprint(11, true, ManagerId, Now);

        Assert.Equal(events, ticket.Events.Count);
    }

    [Fact] // SPRINT-INV-04
    public void MoveToSprint_CompletedSprint_IsRejected_AndChangesNothing()
    {
        var ticket = ProjectTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.MoveToSprint(11, sprintAcceptsTickets: false, ManagerId, Now));
        Assert.Equal("SPRINT-INV-04", ex.RuleCode);
        Assert.Null(ticket.SprintId);
    }

    [Fact] // Leaving a completed sprint is always allowed — it is how unfinished work is carried over.
    public void MoveToSprint_OutOfCompletedSprint_IntoAPlannedOne_IsAllowed()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);

        ticket.MoveToSprint(12, sprintAcceptsTickets: true, ManagerId, Now);

        Assert.Equal(12, ticket.SprintId);
    }

    [Fact] // TICKET-INV-13
    public void MoveToSprint_TicketWithoutProject_IsRejected()
    {
        var ticket = CreateOpenTicket(); // ProjectId is null
        var ex = Assert.Throws<DomainRuleException>(() => ticket.MoveToSprint(11, true, ManagerId, Now));
        Assert.Equal("TICKET-INV-13", ex.RuleCode);
    }

    [Fact] // TICKET-INV-12: finished work is history.
    public void MoveToSprint_ResolvedTicket_IsRejected()
    {
        var ticket = ProjectTicket();
        ticket.Assign(AssigneeId, true, ManagerId, Now);
        ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now);
        ticket.Resolve(Resolution.Fixed, "Fixed the database migration fully.", AssigneeId, Now);

        var ex = Assert.Throws<DomainRuleException>(() => ticket.MoveToSprint(11, true, ManagerId, Now));
        Assert.Equal("TICKET-INV-12", ex.RuleCode);
    }

    [Fact]
    public void PullFromSprintBacklog_ClearsFlag_LeavesStatus_AppendsOneEvent()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);
        var events = ticket.Events.Count;

        ticket.PullFromSprintBacklog(ManagerId, Now);

        Assert.False(ticket.SprintBacklog);
        Assert.Equal(11, ticket.SprintId);
        Assert.Equal(Status.Open, ticket.Status);
        Assert.Equal(events + 1, ticket.Events.Count);
    }

    [Fact]
    public void PullFromSprintBacklog_NotInBacklog_IsRejected()
    {
        var ticket = ProjectTicket();
        Assert.Equal("TICKET-INV-13", Assert.Throws<DomainRuleException>(() => ticket.PullFromSprintBacklog(ManagerId, Now)).RuleCode);

        ticket.MoveToSprint(11, true, ManagerId, Now);
        ticket.PullFromSprintBacklog(ManagerId, Now);
        Assert.Equal("TICKET-INV-13", Assert.Throws<DomainRuleException>(() => ticket.PullFromSprintBacklog(ManagerId, Now)).RuleCode);
    }

    [Fact] // Workflow keeps working, unchanged, for a ticket that is in a sprint.
    public void Workflow_IsUnaffectedByMembership()
    {
        var ticket = ProjectTicket();
        ticket.MoveToSprint(11, true, ManagerId, Now);

        ticket.Assign(AssigneeId, true, ManagerId, Now);
        ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now);

        Assert.Equal(Status.InProgress, ticket.Status);
        Assert.Equal(11, ticket.SprintId);
    }

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Agent, false)]
    [InlineData(UserRole.Viewer, false)]
    public void PlanningPolicies_FollowRole(UserRole role, bool allowed)
    {
        var user = new CurrentUser(Guid.NewGuid(), 1, role, new HashSet<int> { TeamId }, new HashSet<int> { TeamId });
        var snapshot = new TicketAuthorizationSnapshot(1, TeamId, RequesterId, null, Status.Open);

        Assert.Equal(allowed, PlanningAccessPolicy.CanManageSprints(user));
        Assert.Equal(allowed, TicketAccessPolicy.CanPlan(snapshot, user));
    }

    [Fact] // A Manager of a different team cannot plan this team's ticket.
    public void CanPlan_ManagerOfAnotherTeam_IsDenied()
    {
        var user = new CurrentUser(Guid.NewGuid(), 1, UserRole.Manager, new HashSet<int> { 99 }, new HashSet<int> { 99 });
        var snapshot = new TicketAuthorizationSnapshot(1, TeamId, RequesterId, null, Status.Open);
        Assert.False(TicketAccessPolicy.CanPlan(snapshot, user));
    }
}
