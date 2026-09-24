using FlowOps.Application.Planning;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Planning;

/// <summary>
/// ADR-0030 against real PostgreSQL: sprint cancellation, the completion snapshot (historical
/// membership), carry-forward, the Sprints archive queries, and board moves (the server side of
/// drag-and-drop) — including authorization, cross-organization forged ids, and that a rejected
/// operation leaves the database untouched.
/// </summary>
[Collection("Postgres")]
public sealed class SprintArchiveAndBoardMoveTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly W1Start = new(2026, 9, 14);
    private static readonly DateOnly W2Start = new(2026, 9, 21);
    private static readonly DateOnly W3Start = new(2026, 9, 28);

    private readonly PostgresFixture _fixture;

    public SprintArchiveAndBoardMoveTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(int ProjectId, int TeamId, int CategoryId, CurrentUser Admin, CurrentUser Manager, CurrentUser Agent, CurrentUser Viewer);

    private static TicketTestData.FixedTimeProvider Clock() => new(Now);
    private static SprintService Sprints(FlowOpsDbContext c) => new(c, Clock());
    private static TicketService Tickets(FlowOpsDbContext c) => new(c, Clock(), TestEmail.Sender, TestEmail.Options);
    private static ProjectPlanningQueryService Query(FlowOpsDbContext c) => new(c, Clock());

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var projectId = await TicketTestData.AddProjectAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        var agentId = await TicketTestData.AddUserAsync(context);
        var viewerId = await TicketTestData.AddUserAsync(context);
        foreach (var id in new[] { managerId, agentId, viewerId })
        {
            await TicketTestData.AddTeamMembershipAsync(context, teamId, id);
        }

        return new World(projectId, teamId, categoryId,
            TicketTestData.User(adminId, UserRole.Admin),
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            TicketTestData.User(viewerId, UserRole.Viewer, teamId));
    }

    private static async Task<int> SprintAsync(FlowOpsDbContext c, World w, string name, DateOnly start, bool activate = false)
    {
        var r = await Sprints(c).CreateSprintAsync(w.Admin, w.ProjectId, name, start, start.AddDays(6));
        Assert.True(r.Succeeded, r.Error);
        if (activate)
        {
            Assert.True((await Sprints(c).StartSprintAsync(w.Admin, r.SprintId!.Value)).Succeeded);
        }

        return r.SprintId!.Value;
    }

    private static async Task<int> TicketAsync(FlowOpsDbContext c, World w, string title, int? sprintId = null)
    {
        var (id, _) = await Tickets(c).CreateAsync(
            new CreateTicketRequest(title, "A ticket for sprint archive tests.", WorkType.Incident, Priority.Medium, w.TeamId, w.CategoryId, w.ProjectId), w.Agent);
        if (sprintId is { } s)
        {
            await Tickets(c).MoveToSprintAsync(id, s, w.Manager);
        }

        return id;
    }

    private static Task<Ticket> LoadAsync(FlowOpsDbContext c, int id) => c.Tickets.AsNoTracking().Include(t => t.Events).SingleAsync(t => t.Id == id);

    /// <summary>Sprint 1 completed with one finished and two unfinished tickets; returns their ids.</summary>
    private async Task<(int W1, int Done, int Started, int Backlog)> CompletedSprintAsync(FlowOpsDbContext c, World w)
    {
        var w1 = await SprintAsync(c, w, "Week of Sep 14", W1Start, activate: true);
        var done = await TicketAsync(c, w, "Finished ticket in sprint one", w1);
        var started = await TicketAsync(c, w, "Started ticket in sprint one", w1);
        var backlog = await TicketAsync(c, w, "Backlog ticket in sprint one", w1);

        await Tickets(c).MoveOnBoardAsync(done, BoardColumnKey.InProgress, new BoardMoveInput(), w.Manager);
        await Tickets(c).MoveOnBoardAsync(done, BoardColumnKey.Done, new BoardMoveInput(ResolutionCode: Resolution.Fixed, ResolutionNotes: "Shipped and verified in QA"), w.Manager);
        await Tickets(c).MoveOnBoardAsync(started, BoardColumnKey.InProgress, new BoardMoveInput(), w.Manager);

        Assert.True((await Sprints(c).CompleteSprintAsync(w.Admin, w1)).Succeeded);
        return (w1, done, started, backlog);
    }

    // ---------------- cancellation ----------------

    [Fact]
    public async Task CancelSprint_Planned_ReleasesItsTickets_KeepsTheSprintAsHistory_AndAudits()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var planned = await SprintAsync(c, w, "Planned one", W3Start);
        var ticket = await TicketAsync(c, w, "Planned into a sprint that gets cancelled", planned);

        var result = await Sprints(c).CancelSprintAsync(w.Manager, planned);

        Assert.True(result.Succeeded, result.Error);
        await using var verify = _fixture.CreateContext();
        var sprint = await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == planned);
        Assert.Equal(SprintStatus.Cancelled, sprint.Status);
        Assert.NotNull(sprint.CancelledAt);
        var released = await LoadAsync(verify, ticket);
        Assert.Null(released.SprintId);
        Assert.Equal(Status.Open, released.Status); // membership only — never workflow
        Assert.Equal(2, released.Events.Count(e => e.EventType == TicketEventType.SprintChanged)); // added, released
    }

    [Fact]
    public async Task CancelSprint_ActiveOrCompletedOrAlreadyCancelled_IsRefused_AndChangesNothing()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var active = await SprintAsync(c, w, "Active", W2Start, activate: true);
        var cancelled = await SprintAsync(c, w, "To cancel", W3Start);
        Assert.True((await Sprints(c).CancelSprintAsync(w.Admin, cancelled)).Succeeded);

        Assert.False((await Sprints(c).CancelSprintAsync(w.Admin, active)).Succeeded);
        Assert.False((await Sprints(c).CancelSprintAsync(w.Admin, cancelled)).Succeeded);
        Assert.True((await Sprints(c).CompleteSprintAsync(w.Admin, active)).Succeeded);
        Assert.False((await Sprints(c).CancelSprintAsync(w.Admin, active)).Succeeded);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(SprintStatus.Completed, (await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == active)).Status);
    }

    [Fact]
    public async Task CancelledSprint_AcceptsNoTickets_AndIsNotAMoveTarget()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var cancelled = await SprintAsync(c, w, "Cancelled", W3Start);
        await Sprints(c).CancelSprintAsync(w.Admin, cancelled);
        var ticket = await TicketAsync(c, w, "Cannot join a cancelled sprint");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(c).MoveToSprintAsync(ticket, cancelled, w.Manager));
        Assert.Equal("SPRINT-INV-04", ex.RuleCode);

        var view = (await Query(c).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter()))!;
        Assert.DoesNotContain(view.MoveTargets, t => t.SprintId == cancelled);
    }

    [Fact]
    public async Task CancelSprint_ByAgentViewerOrForeignOrganization_IsDenied_AndChangesNothing()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var planned = await SprintAsync(c, w, "Protected", W3Start);
        var (foreignOrg, _) = await TicketTestData.AddSecondOrganizationTeamAsync(c);
        var foreignAdmin = TicketTestData.UserInOrganization(foreignOrg, await TicketTestData.AddUserAsync(c), UserRole.Admin);

        foreach (var actor in new[] { w.Agent, w.Viewer, foreignAdmin })
        {
            await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(c).CancelSprintAsync(actor, planned));
        }

        await using var verify = _fixture.CreateContext();
        Assert.Equal(SprintStatus.Planned, (await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == planned)).Status);
    }

    // ---------------- historical membership & archive ----------------

    [Fact]
    public async Task Complete_FreezesEveryMembersResult()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, done, started, backlog) = await CompletedSprintAsync(c, w);

        await using var verify = _fixture.CreateContext();
        var snapshots = await verify.SprintTicketSnapshots.AsNoTracking().Where(s => s.SprintId == w1).ToDictionaryAsync(s => s.TicketId);
        Assert.Equal(3, snapshots.Count);
        Assert.True(snapshots[done].WasDone);
        Assert.Equal(Status.Resolved, snapshots[done].StatusAtCompletion);
        Assert.False(snapshots[started].WasDone);
        Assert.Equal(Status.InProgress, snapshots[started].StatusAtCompletion);
        Assert.False(snapshots[backlog].WasDone);
    }

    [Fact]
    public async Task SprintArchive_ListsEverySprint_WithFrozenCounts_EvenAfterCarryForward()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, _, _, _) = await CompletedSprintAsync(c, w);
        var w2 = await SprintAsync(c, w, "Week of Sep 21", W2Start, activate: true);
        await SprintAsync(c, w, "Week of Sep 28", W3Start);

        var before = (await Query(c).GetSprintArchiveAsync(w.Admin, w.ProjectId))!;
        var w1Before = before.Sprints.Single(s => s.SprintId == w1);
        Assert.Equal((3, 1, 2), (w1Before.TotalTickets, w1Before.DoneTickets, w1Before.UnfinishedTickets));
        Assert.Equal(3, before.Sprints.Count);

        Assert.True((await Sprints(c).CarryForwardAsync(w.Admin, w1)).Succeeded);

        var after = (await Query(c).GetSprintArchiveAsync(w.Admin, w.ProjectId))!;
        var w1After = after.Sprints.Single(s => s.SprintId == w1);
        Assert.Equal((3, 1, 2), (w1After.TotalTickets, w1After.DoneTickets, w1After.UnfinishedTickets)); // history is not rewritten
        Assert.Equal(2, after.Sprints.Single(s => s.SprintId == w2).TotalTickets);                      // current membership is
    }

    [Fact]
    public async Task SprintDetail_SplitsFinishedFromUnfinished_AndShowsWhereCarriedTicketsWentNow()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, done, started, backlog) = await CompletedSprintAsync(c, w);
        var w2 = await SprintAsync(c, w, "Week of Sep 21", W2Start, activate: true);
        await Tickets(c).MoveToSprintAsync(started, w2, w.Manager); // carried by hand; the other stays

        var detail = (await Query(c).GetSprintDetailAsync(w.Admin, w.ProjectId, w1))!;

        Assert.True(detail.IsSnapshot);
        Assert.Equal(done, Assert.Single(detail.Done).TicketId);
        Assert.Equal(new[] { started, backlog }.Order(), detail.Unfinished.Select(t => t.TicketId).Order());
        var moved = detail.Unfinished.Single(t => t.TicketId == started);
        Assert.Equal(w2, moved.CurrentSprintId);
        Assert.Equal(Status.InProgress, moved.StatusInSprint);
        Assert.Equal(w1, detail.Unfinished.Single(t => t.TicketId == backlog).CurrentSprintId);
    }

    [Fact]
    public async Task SprintArchiveAndDetail_ExcludeTicketsTheCallerCannotSee_AndForeignProjectsAre404()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var otherTeam = await TicketTestData.AddTeamAsync(c);
        var otherCategory = await TicketTestData.AddCategoryAsync(c, otherTeam);
        var sprint = await SprintAsync(c, w, "Mixed visibility", W2Start, activate: true);
        var hidden = (await Tickets(c).CreateAsync(new CreateTicketRequest("Belongs to another team", "Admins only.", WorkType.Incident, Priority.Low, otherTeam, otherCategory, w.ProjectId), w.Admin)).Id;
        await Tickets(c).MoveToSprintAsync(hidden, sprint, w.Admin);
        await TicketAsync(c, w, "Visible to the team", sprint);

        var asAgent = (await Query(c).GetSprintDetailAsync(w.Agent, w.ProjectId, sprint))!;
        var asAdmin = (await Query(c).GetSprintDetailAsync(w.Admin, w.ProjectId, sprint))!;
        Assert.Equal(1, asAgent.Sprint.TotalTickets);
        Assert.Equal(2, asAdmin.Sprint.TotalTickets);

        var (foreignOrg, foreignTeam) = await TicketTestData.AddSecondOrganizationTeamAsync(c);
        var foreignAdmin = TicketTestData.UserInOrganization(foreignOrg, await TicketTestData.AddUserAsync(c), UserRole.Admin);
        Assert.Null(await Query(c).GetSprintDetailAsync(foreignAdmin, w.ProjectId, sprint));
        Assert.Null(await Query(c).GetSprintArchiveAsync(foreignAdmin, w.ProjectId));
    }

    // ---------------- carry-forward ----------------

    [Fact]
    public async Task CarryForward_MovesOnlyUnfinishedTickets_PreservingStatusAssigneeAndDates()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, done, started, backlog) = await CompletedSprintAsync(c, w);
        var w2 = await SprintAsync(c, w, "Week of Sep 21", W2Start, activate: true);
        var before = await LoadAsync(c, started);

        var result = await Sprints(c).CarryForwardAsync(w.Manager, w1);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Moved);
        await using var verify = _fixture.CreateContext();
        var moved = await LoadAsync(verify, started);
        Assert.Equal(w2, moved.SprintId);
        Assert.Equal(before.Status, moved.Status);
        Assert.Equal(before.AssigneeId, moved.AssigneeId);
        Assert.Equal(before.SlaDueAt, moved.SlaDueAt);
        Assert.Equal(before.DueDate, moved.DueDate);
        Assert.Equal(w2, (await LoadAsync(verify, backlog)).SprintId);
        Assert.Equal(w1, (await LoadAsync(verify, done)).SprintId); // finished work stays historical
        Assert.Equal(3, await verify.SprintTicketSnapshots.CountAsync(s => s.SprintId == w1));
    }

    [Fact]
    public async Task CarryForward_NeedsACompletedSprintAndACurrentSprint()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, _, _, _) = await CompletedSprintAsync(c, w);

        var none = await Sprints(c).CarryForwardAsync(w.Admin, w1);
        Assert.False(none.Succeeded); // no current sprint yet

        var active = await SprintAsync(c, w, "Week of Sep 21", W2Start, activate: true);
        var notCompleted = await Sprints(c).CarryForwardAsync(w.Admin, active);
        Assert.False(notCompleted.Succeeded);
    }

    [Fact]
    public async Task CarryForward_SkipsTicketsTheCallerCannotPlan_AndDeniesAgentViewerAndForeignOrg()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (w1, _, started, _) = await CompletedSprintAsync(c, w);
        var w2 = await SprintAsync(c, w, "Week of Sep 21", W2Start, activate: true);
        var strangerManager = TicketTestData.Manager(await TicketTestData.AddUserAsync(c), int.MaxValue);

        var skipped = await Sprints(c).CarryForwardAsync(strangerManager, w1);
        Assert.True(skipped.Succeeded);
        Assert.Equal((0, 2), (skipped.Moved, skipped.Skipped)); // a Manager of another team may not plan these

        var (foreignOrg, _) = await TicketTestData.AddSecondOrganizationTeamAsync(c);
        var foreignAdmin = TicketTestData.UserInOrganization(foreignOrg, await TicketTestData.AddUserAsync(c), UserRole.Admin);
        foreach (var actor in new[] { w.Agent, w.Viewer, foreignAdmin })
        {
            await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(c).CarryForwardAsync(actor, w1));
        }

        await using var verify = _fixture.CreateContext();
        Assert.Equal(w1, (await LoadAsync(verify, started)).SprintId); // nothing moved
        Assert.Equal(0, await verify.Tickets.CountAsync(t => t.SprintId == w2));
    }

    // ---------------- board moves (server side of drag-and-drop) ----------------

    [Fact]
    public async Task BoardMove_BacklogToInProgress_UsesTheExistingTransitions_AndAuditsEachStep()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Dragged from backlog to in progress", sprint);

        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Agent);

        await using var verify = _fixture.CreateContext();
        var t = await LoadAsync(verify, ticket);
        Assert.Equal(Status.InProgress, t.Status);
        Assert.Equal(w.Agent.UserId, t.AssigneeId);
        Assert.True(t.SprintBacklog); // an Agent may start work but cannot plan, so the presentation flag is left as it was
        Assert.Equal(sprint, t.SprintId);
        Assert.Contains(t.Events, e => e.EventType == TicketEventType.Assigned);
        Assert.Contains(t.Events, e => e.EventType == TicketEventType.StatusChanged && e.NewValue == nameof(Status.InProgress));
        Assert.DoesNotContain(t.Events, e => e.EventType == TicketEventType.SprintChanged && e.Field == nameof(Ticket.SprintBacklog));
    }

    [Fact]
    public async Task BoardMove_BacklogToInProgress_ByAManager_AlsoClearsTheBacklogFlag()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Manager drags out of the backlog", sprint);

        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Manager);

        await using var verify = _fixture.CreateContext();
        var t = await LoadAsync(verify, ticket);
        Assert.Equal(Status.InProgress, t.Status);
        Assert.False(t.SprintBacklog);
        Assert.Contains(t.Events, e => e.EventType == TicketEventType.SprintChanged && e.Field == nameof(Ticket.SprintBacklog));
    }

    [Fact]
    public async Task BoardMove_InvalidTransition_IsRejected_AndTheDatabaseIsUnchanged()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Started work cannot go back to backlog", sprint);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Agent);
        var before = await LoadAsync(c, ticket);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Backlog, new BoardMoveInput(), w.Agent));
        Assert.Equal("BOARD-MOVE", ex.RuleCode);

        await using var verify = _fixture.CreateContext();
        var after = await LoadAsync(verify, ticket);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Events.Count, after.Events.Count);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task BoardMove_HoldAndResolve_NeedTheirRequiredInput_ExactlyLikeTheTicketPage()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Hold and resolve need their input", sprint);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Agent);

        var noReason = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Pending, new BoardMoveInput(), w.Agent));
        Assert.Equal("TICKET-INV-05", noReason.RuleCode);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Pending, new BoardMoveInput(Reason: "Waiting on vendor"), w.Agent);

        var shortNotes = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Done, new BoardMoveInput(ResolutionCode: Resolution.Fixed, ResolutionNotes: "short"), w.Agent));
        Assert.Equal("TICKET-INV-06", shortNotes.RuleCode);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Done, new BoardMoveInput(ResolutionCode: Resolution.Fixed, ResolutionNotes: "Fixed after the vendor replied"), w.Agent);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(Status.Resolved, (await LoadAsync(verify, ticket)).Status);
    }

    [Fact]
    public async Task BoardMove_Reopen_FromDone_ThenBackToBacklogFlow()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Reopened from the done column", sprint);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Manager);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Done, new BoardMoveInput(ResolutionCode: Resolution.Fixed, ResolutionNotes: "Done and dusted here"), w.Manager);

        // Finished work cannot be re-planned (TICKET-INV-12), but it can be reopened like anywhere else.
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Open, new BoardMoveInput(Reason: "Regression found"), w.Manager);

        await using var verify = _fixture.CreateContext();
        var t = await LoadAsync(verify, ticket);
        Assert.Equal(Status.Assigned, t.Status); // reopened onto its assignee
        Assert.Equal(1, t.ReopenCount);
    }

    [Fact]
    public async Task BoardMove_ByViewer_OrOnAForeignTicket_IsDenied_AndChangesNothing()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Only actors may drag this", sprint);
        var (foreignOrg, _) = await TicketTestData.AddSecondOrganizationTeamAsync(c);
        var foreignAdmin = TicketTestData.UserInOrganization(foreignOrg, await TicketTestData.AddUserAsync(c), UserRole.Admin);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Viewer));
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), foreignAdmin));

        await using var verify = _fixture.CreateContext();
        var t = await LoadAsync(verify, ticket);
        Assert.Equal(Status.Open, t.Status);
        Assert.True(t.SprintBacklog);
    }

    [Fact]
    public async Task BoardMove_OnATicketNotInASprint_IsRejected()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var ticket = await TicketAsync(c, w, "Not on any board");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.InProgress, new BoardMoveInput(), w.Manager));
        Assert.Equal("BOARD-MOVE", ex.RuleCode);
    }

    [Fact]
    public async Task BoardMove_OpenToBacklog_ReturnsTheTicketToTheSprintBacklog()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Week", W2Start, activate: true);
        var ticket = await TicketAsync(c, w, "Pulled then sent back to backlog", sprint);
        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Open, new BoardMoveInput(), w.Manager);
        Assert.False((await LoadAsync(c, ticket)).SprintBacklog);

        await Tickets(c).MoveOnBoardAsync(ticket, BoardColumnKey.Backlog, new BoardMoveInput(), w.Manager);

        await using var verify = _fixture.CreateContext();
        var t = await LoadAsync(verify, ticket);
        Assert.True(t.SprintBacklog);
        Assert.Equal(Status.Open, t.Status);
    }
}
