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
/// ADR-0029 against real PostgreSQL: sprint lifecycle, ticket membership, the board and Tickets
/// queries, authorization, and cross-organization isolation (forged ids must be refused AND leave
/// the database unchanged). Each test builds its own project/team so counts stay isolated in the
/// shared Postgres container.
/// </summary>
[Collection("Postgres")]
public sealed class SprintPlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Mon = new(2026, 9, 21);
    private static readonly DateOnly Sun = new(2026, 9, 27);

    private readonly PostgresFixture _fixture;

    public SprintPlanningTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(
        int ProjectId, int TeamId, int CategoryId,
        CurrentUser Admin, CurrentUser Manager, CurrentUser Agent, CurrentUser Viewer);

    private static TicketTestData.FixedTimeProvider Clock() => new(Now);

    private async Task<World> SeedAsync(FlowOpsDbContext context)
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

        return new World(
            projectId, teamId, categoryId,
            TicketTestData.User(adminId, UserRole.Admin),
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            TicketTestData.User(viewerId, UserRole.Viewer, teamId));
    }

    private static SprintService Sprints(FlowOpsDbContext context) => new(context, Clock());

    private static TicketService Tickets(FlowOpsDbContext context) => new(context, Clock());

    private static ProjectPlanningQueryService Query(FlowOpsDbContext context) => new(context, Clock());

    private static async Task<int> NewSprintAsync(FlowOpsDbContext context, World w, string name = "Week of Sep 21", DateOnly? start = null, DateOnly? end = null, bool activate = false)
    {
        var result = await Sprints(context).CreateSprintAsync(w.Admin, w.ProjectId, name, start ?? Mon, end ?? Sun);
        Assert.True(result.Succeeded, result.Error);
        if (activate)
        {
            var started = await Sprints(context).StartSprintAsync(w.Admin, result.SprintId!.Value);
            Assert.True(started.Succeeded, started.Error);
        }

        return result.SprintId!.Value;
    }

    private static async Task<int> NewTicketAsync(FlowOpsDbContext context, World w, string title = "Migrate the reporting database", int? projectId = -1)
    {
        var (id, _) = await Tickets(context).CreateAsync(
            new CreateTicketRequest(title, "Move the reporting database to the new cluster.", WorkType.Incident, Priority.Medium, w.TeamId, w.CategoryId, projectId == -1 ? w.ProjectId : projectId),
            w.Agent);
        return id;
    }

    // ---------------- sprint lifecycle ----------------

    [Fact]
    public async Task CreateSprint_Valid_PersistsAsPlanned()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);

        var sprintId = await NewSprintAsync(context, w);

        await using var verify = _fixture.CreateContext();
        var sprint = await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId);
        Assert.Equal(SprintStatus.Planned, sprint.Status);
        Assert.Equal(w.ProjectId, sprint.ProjectId);
        Assert.Equal(Mon, sprint.StartDate);
        Assert.Equal(Sun, sprint.EndDate);
    }

    [Fact]
    public async Task CreateSprint_StartAfterEnd_IsRefusedWithAMessage()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);

        var result = await Sprints(context).CreateSprintAsync(w.Admin, w.ProjectId, "Backwards", Sun, Mon);

        Assert.False(result.Succeeded);
        Assert.Contains("start date", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Sprints.CountAsync(s => s.ProjectId == w.ProjectId));
    }

    [Fact]
    public async Task CreateSprint_OverlappingAnotherOpenSprint_IsRefused_ButCompletedOnesDoNotBlock()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var first = await NewSprintAsync(context, w, activate: true);

        var overlap = await Sprints(context).CreateSprintAsync(w.Admin, w.ProjectId, "Overlaps", Mon.AddDays(3), Sun.AddDays(3));
        Assert.False(overlap.Succeeded);

        var adjacent = await Sprints(context).CreateSprintAsync(w.Admin, w.ProjectId, "Next week", Sun.AddDays(1), Sun.AddDays(7));
        Assert.True(adjacent.Succeeded);

        Assert.True((await Sprints(context).CompleteSprintAsync(w.Admin, first)).Succeeded);
        var reuse = await Sprints(context).CreateSprintAsync(w.Admin, w.ProjectId, "Same dates again", Mon, Sun);
        Assert.True(reuse.Succeeded, reuse.Error);
    }

    [Fact]
    public async Task StartSprint_WhenAnotherIsActive_IsRefused()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        await NewSprintAsync(context, w, "One", Mon, Sun, activate: true);
        var second = await NewSprintAsync(context, w, "Two", Sun.AddDays(1), Sun.AddDays(7));

        var result = await Sprints(context).StartSprintAsync(w.Admin, second);

        Assert.False(result.Succeeded);
        await using var verify = _fixture.CreateContext();
        Assert.Equal(SprintStatus.Planned, (await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == second)).Status);
    }

    [Fact] // The invariant holds even if application code is bypassed entirely.
    public async Task Database_RefusesASecondActiveSprintForOneProject()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        await NewSprintAsync(context, w, "One", Mon, Sun, activate: true);
        var second = await NewSprintAsync(context, w, "Two", Sun.AddDays(1), Sun.AddDays(7));

        await using var raw = _fixture.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            raw.Database.ExecuteSqlAsync($"UPDATE sprints SET status = 'Active' WHERE id = {second}"));
        Assert.Contains("ux_sprints_one_active_per_project", ex.Message);
    }

    [Fact]
    public async Task CompleteSprint_KeepsMembership_AndBlocksNewTickets()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        var otherTicketId = await NewTicketAsync(context, w, "Second ticket for the project");
        await Tickets(context).MoveToSprintAsync(ticketId, sprintId, w.Manager);

        Assert.True((await Sprints(context).CompleteSprintAsync(w.Admin, sprintId)).Succeeded);

        await using var verify = _fixture.CreateContext();
        var stillThere = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(sprintId, stillThere.SprintId); // history is kept, never silently moved

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Tickets(verify).MoveToSprintAsync(otherTicketId, sprintId, w.Manager));
        Assert.Equal("SPRINT-INV-04", ex.RuleCode);

        // ...but the unfinished ticket can be carried into a planned sprint.
        var next = await NewSprintAsync(context, w, "Next", Sun.AddDays(1), Sun.AddDays(7));
        await Tickets(verify).MoveToSprintAsync(ticketId, next, w.Manager);
        Assert.Equal(next, (await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).SprintId);
    }

    [Fact]
    public async Task CompleteSprint_Twice_SecondIsRefused()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);

        Assert.True((await Sprints(context).CompleteSprintAsync(w.Admin, sprintId)).Succeeded);
        Assert.False((await Sprints(context).CompleteSprintAsync(w.Admin, sprintId)).Succeeded);
    }

    // ---------------- ticket membership ----------------

    [Fact]
    public async Task MoveToSprint_PreservesStatusAssignmentSlaAndDates_AndAudits()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        await Tickets(context).AssignAsync(ticketId, w.Agent.UserId, w.Manager);
        var before = await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);

        await Tickets(context).MoveToSprintAsync(ticketId, sprintId, w.Manager);

        await using var verify = _fixture.CreateContext();
        var after = await verify.Tickets.AsNoTracking().Include(t => t.Events).SingleAsync(t => t.Id == ticketId);
        Assert.Equal(sprintId, after.SprintId);
        Assert.True(after.SprintBacklog);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.AssigneeId, after.AssigneeId);
        Assert.Equal(before.SlaDueAt, after.SlaDueAt);
        Assert.Equal(before.DueDate, after.DueDate);
        Assert.Contains(after.Events, e => e.EventType == TicketEventType.SprintChanged && e.NewValue == sprintId.ToString() && e.ActorUserId == w.Manager.UserId);
    }

    [Fact]
    public async Task MoveToSprint_BetweenSprints_LeavesTheFirstAndEntersTheSecond()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var one = await NewSprintAsync(context, w, "One", Mon, Sun, activate: true);
        var two = await NewSprintAsync(context, w, "Two", Sun.AddDays(1), Sun.AddDays(7));
        var ticketId = await NewTicketAsync(context, w);

        await Tickets(context).MoveToSprintAsync(ticketId, one, w.Manager);
        await Tickets(context).MoveToSprintAsync(ticketId, two, w.Manager);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(two, (await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).SprintId);
        Assert.Equal(0, (await Query(verify).GetBoardAsync(w.Admin, w.ProjectId))!.Columns.Sum(c => c.Cards.Count));
    }

    [Fact]
    public async Task MoveToSprint_Null_RemovesFromTheBoardButNotFromTheProject()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        await Tickets(context).MoveToSprintAsync(ticketId, sprintId, w.Manager);

        await Tickets(context).MoveToSprintAsync(ticketId, null, w.Manager);

        var board = (await Query(context).GetBoardAsync(w.Admin, w.ProjectId))!;
        Assert.DoesNotContain(board.Columns.SelectMany(c => c.Cards), c => c.TicketId == ticketId);
        var all = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter()))!;
        Assert.Contains(all.Tickets.Items, t => t.TicketId == ticketId && t.SprintId is null);
    }

    [Fact] // A sprint of a different project can never be a destination.
    public async Task MoveToSprint_SprintOfAnotherProject_IsRefused_AndChangesNothing()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var otherProjectId = await TicketTestData.AddProjectAsync(context);
        var foreignSprint = (await Sprints(context).CreateSprintAsync(w.Admin, otherProjectId, "Other", Mon, Sun)).SprintId!.Value;
        var ticketId = await NewTicketAsync(context, w);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(ticketId, foreignSprint, w.Manager));

        await using var verify = _fixture.CreateContext();
        Assert.Null((await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).SprintId);
    }

    [Fact]
    public async Task MoveToSprint_TicketWithoutProject_IsRefused()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w, "No project on this one", projectId: null);

        var ex = await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(ticketId, sprintId, w.Manager));
        Assert.NotNull(ex); // sprint.ProjectId never equals a null ticket project, so it is "not available"
    }

    [Fact]
    public async Task PullFromBacklog_MovesCardToItsStatusColumn_WithoutChangingStatus()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        await Tickets(context).MoveToSprintAsync(ticketId, sprintId, w.Manager);

        var before = (await Query(context).GetBoardAsync(w.Admin, w.ProjectId))!;
        Assert.Contains(before.Columns.Single(c => c.Key == BoardColumnKey.Backlog).Cards, c => c.TicketId == ticketId);

        await Tickets(context).PullFromSprintBacklogAsync(ticketId, w.Manager);

        var after = (await Query(context).GetBoardAsync(w.Admin, w.ProjectId))!;
        Assert.DoesNotContain(after.Columns.Single(c => c.Key == BoardColumnKey.Backlog).Cards, c => c.TicketId == ticketId);
        Assert.Contains(after.Columns.Single(c => c.Key == BoardColumnKey.Open).Cards, c => c.TicketId == ticketId);
        Assert.Equal(Status.Open, (await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).Status);
    }

    // ---------------- board & tickets queries ----------------

    [Fact]
    public async Task Board_ShowsOnlyTheActiveSprintsTickets_AndFollowsRealStatus()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var inSprint = await NewTicketAsync(context, w, "In the sprint and started");
        var backlog = await NewTicketAsync(context, w, "In the sprint backlog only");
        var outside = await NewTicketAsync(context, w, "Not in any sprint");
        foreach (var id in new[] { inSprint, backlog })
        {
            await Tickets(context).MoveToSprintAsync(id, sprintId, w.Manager);
        }

        await Tickets(context).PullFromSprintBacklogAsync(inSprint, w.Manager);
        await Tickets(context).AssignAsync(inSprint, w.Agent.UserId, w.Manager);
        await Tickets(context).StartWorkAsync(inSprint, w.Agent);

        var board = (await Query(context).GetBoardAsync(w.Admin, w.ProjectId))!;

        var cards = board.Columns.SelectMany(c => c.Cards).ToList();
        Assert.DoesNotContain(cards, c => c.TicketId == outside);
        Assert.Contains(board.Columns.Single(c => c.Key == BoardColumnKey.InProgress).Cards, c => c.TicketId == inSprint);
        Assert.Contains(board.Columns.Single(c => c.Key == BoardColumnKey.Backlog).Cards, c => c.TicketId == backlog);
        Assert.Equal(sprintId, board.ActiveSprint!.SprintId);
    }

    [Fact]
    public async Task Board_WithNoActiveSprint_HasNoCards_AndListsPlannedSprints()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var planned = await NewSprintAsync(context, w);

        var board = (await Query(context).GetBoardAsync(w.Admin, w.ProjectId))!;

        Assert.Null(board.ActiveSprint);
        Assert.All(board.Columns, c => Assert.Empty(c.Cards));
        Assert.Contains(board.PlannedSprints, s => s.SprintId == planned);
    }

    [Fact]
    public async Task ProjectTickets_ShowAllProjectTickets_Filter_AndPaginate()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ids = new List<int>();
        for (var i = 0; i < 27; i++)
        {
            ids.Add(await NewTicketAsync(context, w, $"Project ticket number {i:00}"));
        }

        await Tickets(context).MoveToSprintAsync(ids[0], sprintId, w.Manager);

        var page1 = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter()))!;
        var page2 = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 2, new ProjectTicketFilter()))!;
        Assert.Equal(27, page1.Tickets.TotalCount);
        Assert.Equal(25, page1.Tickets.Items.Count);
        Assert.Equal(2, page2.Tickets.Items.Count);
        Assert.Empty(page1.Tickets.Items.Select(t => t.TicketId).Intersect(page2.Tickets.Items.Select(t => t.TicketId)));

        var current = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter(SprintScope.Current)))!;
        Assert.Equal(ids[0], Assert.Single(current.Tickets.Items).TicketId);
        var none = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter(SprintScope.NoSprint)))!;
        Assert.Equal(26, none.Tickets.TotalCount);
        var unassigned = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter(Unassigned: true)))!;
        Assert.Equal(27, unassigned.Tickets.TotalCount);
        var completed = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter(SprintScope.Completed)))!;
        Assert.Equal(0, completed.Tickets.TotalCount);
    }

    [Fact] // Sprint counts and lists follow ordinary ticket visibility.
    public async Task ProjectQueries_NeverShowTicketsTheCallerCannotSee()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var hidden = (await Tickets(context).CreateAsync(
            new CreateTicketRequest("Belongs to a team the agent is not in", "Only visible to admins.", WorkType.Incident, Priority.Low, otherTeamId, otherCategoryId, w.ProjectId),
            w.Admin)).Id;
        await Tickets(context).MoveToSprintAsync(hidden, sprintId, w.Admin);

        var asAgent = (await Query(context).GetTicketsAsync(w.Agent, w.ProjectId, 1, new ProjectTicketFilter()))!;
        var asAdmin = (await Query(context).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter()))!;
        var agentBoard = (await Query(context).GetBoardAsync(w.Agent, w.ProjectId))!;

        Assert.DoesNotContain(asAgent.Tickets.Items, t => t.TicketId == hidden);
        Assert.Contains(asAdmin.Tickets.Items, t => t.TicketId == hidden);
        Assert.DoesNotContain(agentBoard.Columns.SelectMany(c => c.Cards), c => c.TicketId == hidden);
        Assert.Equal(0, agentBoard.ActiveSprint!.TicketCount);
    }

    // ---------------- authorization ----------------

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task SprintMutations_ByAgentOrViewer_AreDenied_AndChangeNothing(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w);
        var actor = role == UserRole.Agent ? w.Agent : w.Viewer;

        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).CreateSprintAsync(actor, w.ProjectId, "Nope", Sun.AddDays(1), Sun.AddDays(7)));
        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).StartSprintAsync(actor, sprintId));
        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).CompleteSprintAsync(actor, sprintId));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.Sprints.CountAsync(s => s.ProjectId == w.ProjectId));
        Assert.Equal(SprintStatus.Planned, (await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId)).Status);
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task TicketPlanning_ByAgentOrViewer_IsDenied_AndChangesNothing(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        var actor = role == UserRole.Agent ? w.Agent : w.Viewer;

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(ticketId, sprintId, actor));

        await using var verify = _fixture.CreateContext();
        Assert.Null((await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).SprintId);
    }

    [Fact] // A Manager may only plan tickets of teams they manage.
    public async Task TicketPlanning_ManagerOfAnotherTeam_IsDenied()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var sprintId = await NewSprintAsync(context, w, activate: true);
        var ticketId = await NewTicketAsync(context, w);
        var strangerManager = TicketTestData.Manager(await TicketTestData.AddUserAsync(context), int.MaxValue);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(ticketId, sprintId, strangerManager));
    }

    // ---------------- tenant isolation (forged ids) ----------------

    private sealed record Foreign(int OrganizationId, int ProjectId, int SprintId, int TicketId, CurrentUser Admin);

    private async Task<Foreign> SeedForeignOrgAsync(FlowOpsDbContext context)
    {
        var (orgId, teamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var project = new Project(0, orgId, $"Foreign-{Guid.NewGuid():N}", Now);
        context.Add(project);
        await context.SaveChangesAsync();
        var adminId = await TicketTestData.AddUserAsync(context);
        var admin = TicketTestData.UserInOrganization(orgId, adminId, UserRole.Admin);

        var sprintId = (await Sprints(context).CreateSprintAsync(admin, project.Id, "Foreign sprint", Mon, Sun)).SprintId!.Value;
        var ticketId = (await Tickets(context).CreateAsync(
            new CreateTicketRequest("Foreign organization ticket", "Belongs to organization B.", WorkType.Incident, Priority.Low, teamId, categoryId, project.Id),
            admin)).Id;
        return new Foreign(orgId, project.Id, sprintId, ticketId, admin);
    }

    [Fact]
    public async Task CrossOrg_ViewingAForeignProject_IsIndistinguishableFromMissing()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var foreign = await SeedForeignOrgAsync(context);

        Assert.Null(await Query(context).GetBoardAsync(w.Admin, foreign.ProjectId));
        Assert.Null(await Query(context).GetOverviewAsync(w.Admin, foreign.ProjectId));
        Assert.Null(await Query(context).GetTicketsAsync(w.Admin, foreign.ProjectId, 1, new ProjectTicketFilter()));
        Assert.DoesNotContain(await Query(context).GetProjectsAsync(w.Admin), p => p.ProjectId == foreign.ProjectId);
    }

    [Fact]
    public async Task CrossOrg_MutatingAForeignSprint_IsDenied_AndTheSprintIsUnchanged()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var foreign = await SeedForeignOrgAsync(context);

        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).StartSprintAsync(w.Admin, foreign.SprintId));
        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).CompleteSprintAsync(w.Admin, foreign.SprintId));
        await Assert.ThrowsAsync<PlanningAccessDeniedException>(() => Sprints(context).CreateSprintAsync(w.Admin, foreign.ProjectId, "Injected", Sun.AddDays(1), Sun.AddDays(7)));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(SprintStatus.Planned, (await verify.Sprints.AsNoTracking().SingleAsync(s => s.Id == foreign.SprintId)).Status);
        Assert.Equal(1, await verify.Sprints.CountAsync(s => s.ProjectId == foreign.ProjectId));
    }

    [Fact]
    public async Task CrossOrg_MovingAForeignTicket_OrUsingAForeignSprint_IsDenied_AndNothingChanges()
    {
        await using var context = _fixture.CreateContext();
        var w = await SeedAsync(context);
        var mySprint = await NewSprintAsync(context, w, activate: true);
        var myTicket = await NewTicketAsync(context, w);
        var foreign = await SeedForeignOrgAsync(context);

        // Org A admin: foreign ticket -> own sprint; own ticket -> foreign sprint; foreign ticket -> foreign sprint.
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(foreign.TicketId, mySprint, w.Admin));
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(myTicket, foreign.SprintId, w.Admin));
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).MoveToSprintAsync(foreign.TicketId, foreign.SprintId, w.Admin));
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => Tickets(context).PullFromSprintBacklogAsync(foreign.TicketId, w.Admin));

        await using var verify = _fixture.CreateContext();
        Assert.Null((await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == foreign.TicketId)).SprintId);
        Assert.Null((await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == myTicket)).SprintId);
        Assert.Equal(0, await verify.TicketEvents.CountAsync(e => e.TicketId == foreign.TicketId && e.EventType == TicketEventType.SprintChanged));
    }
}
