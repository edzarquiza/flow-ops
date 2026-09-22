using FlowOps.Application.Planning;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Xunit;

namespace FlowOps.Application.Tests.Planning;

/// <summary>Phase 28B against real PostgreSQL: the read-only sprint context on Ticket Detail, the
/// "carried from" marker (derived from completion snapshots, so it stays true after later movement),
/// the Overview's not-in-a-sprint count, and resolved names in the ticket timeline.</summary>
[Collection("Postgres")]
public sealed class SprintContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public SprintContextTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(int ProjectId, int TeamId, int CategoryId, CurrentUser Admin, CurrentUser Manager, CurrentUser Agent);

    private static TicketTestData.FixedTimeProvider Clock() => new(Now);
    private static SprintService Sprints(FlowOpsDbContext c) => new(c, Clock());
    private static TicketService Tickets(FlowOpsDbContext c) => new(c, Clock());
    private static ProjectPlanningQueryService Planning(FlowOpsDbContext c) => new(c, Clock());
    private static TicketQueryService TicketQueries(FlowOpsDbContext c) => new(c, Clock());

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var projectId = await TicketTestData.AddProjectAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        var agentId = await TicketTestData.AddUserAsync(context);
        foreach (var id in new[] { managerId, agentId })
        {
            await TicketTestData.AddTeamMembershipAsync(context, teamId, id);
        }

        return new World(projectId, teamId, categoryId,
            TicketTestData.User(adminId, UserRole.Admin),
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(agentId, UserRole.Agent, teamId));
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
            new CreateTicketRequest(title, "A ticket for sprint context tests.", WorkType.Incident, Priority.Medium, w.TeamId, w.CategoryId, w.ProjectId), w.Agent);
        if (sprintId is { } s)
        {
            await Tickets(c).MoveToSprintAsync(id, s, w.Manager);
        }

        return id;
    }

    /// <summary>Sprint 1 (completed) has one unfinished ticket, carried into an active Sprint 2.</summary>
    private static async Task<(int S1, int S2, int Carried)> CarriedScenarioAsync(FlowOpsDbContext c, World w)
    {
        var s1 = await SprintAsync(c, w, "Sprint 1", new DateOnly(2026, 9, 7), activate: true);
        var carried = await TicketAsync(c, w, "Unfinished work", s1);
        Assert.True((await Sprints(c).CompleteSprintAsync(w.Admin, s1)).Succeeded);
        var s2 = await SprintAsync(c, w, "Sprint 2", new DateOnly(2026, 9, 21), activate: true);
        Assert.True((await Sprints(c).CarryForwardAsync(w.Admin, s1)).Succeeded);
        return (s1, s2, carried);
    }

    [Fact]
    public async Task CarriedTicket_IsMarkedOnTheBoardAndTheTicketsList_ButNotOthers()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (_, s2, carried) = await CarriedScenarioAsync(c, w);
        var fresh = await TicketAsync(c, w, "New this sprint", s2);
        await Tickets(c).PullFromSprintBacklogAsync(carried, w.Manager);

        var board = (await Planning(c).GetBoardAsync(w.Admin, w.ProjectId))!;
        var cards = board.Columns.SelectMany(col => col.Cards).ToList();
        Assert.Equal("Sprint 1", cards.Single(x => x.TicketId == carried).CarriedFromSprint);
        Assert.Null(cards.Single(x => x.TicketId == fresh).CarriedFromSprint);

        var rows = (await Planning(c).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter()))!.Tickets.Items;
        Assert.Equal("Sprint 1", rows.Single(x => x.TicketId == carried).CarriedFromSprint);
        Assert.Null(rows.Single(x => x.TicketId == fresh).CarriedFromSprint);
    }

    [Fact]
    public async Task CarriedMarker_StaysTrueAfterMovingOnToAnotherSprint_AndDisappearsWhenTakenOutOfEverySprint()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (_, _, carried) = await CarriedScenarioAsync(c, w);
        var s3 = await SprintAsync(c, w, "Sprint 3", new DateOnly(2026, 10, 5));

        await Tickets(c).MoveToSprintAsync(carried, s3, w.Manager);
        var inSprint3 = (await TicketQueries(c).GetDetailAsync(carried, w.Admin))!;
        Assert.Equal("Sprint 3", inSprint3.Sprint!.Name);
        Assert.Equal("Sprint 1", inSprint3.Sprint.CarriedFromSprint); // history, not location

        await Tickets(c).MoveToSprintAsync(carried, null, w.Manager);
        Assert.Null((await TicketQueries(c).GetDetailAsync(carried, w.Admin))!.Sprint);
    }

    [Fact]
    public async Task TicketDetail_SprintContext_CoversNoSprintPlannedAndOnTheBoard()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var active = await SprintAsync(c, w, "Current one", new DateOnly(2026, 9, 21), activate: true);
        var loose = await TicketAsync(c, w, "Not planned");
        var planned = await TicketAsync(c, w, "Planned for sprint", active);
        var onBoard = await TicketAsync(c, w, "Already on the board", active);
        await Tickets(c).PullFromSprintBacklogAsync(onBoard, w.Manager);

        Assert.Null((await TicketQueries(c).GetDetailAsync(loose, w.Admin))!.Sprint);

        var plannedContext = (await TicketQueries(c).GetDetailAsync(planned, w.Admin))!.Sprint!;
        Assert.Equal("Current one", plannedContext.Name);
        Assert.Equal(SprintStatus.Active, plannedContext.Status);
        Assert.True(plannedContext.InPlanned);
        Assert.Null(plannedContext.CarriedFromSprint);

        Assert.False((await TicketQueries(c).GetDetailAsync(onBoard, w.Admin))!.Sprint!.InPlanned);
    }

    [Fact]
    public async Task Overview_CountsTicketsNotInASprint_LikeTheNoSprintFilter()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Only sprint", new DateOnly(2026, 9, 21), activate: true);
        await TicketAsync(c, w, "In the sprint", sprint);
        await TicketAsync(c, w, "Loose one");
        await TicketAsync(c, w, "Loose two");

        var overview = (await Planning(c).GetOverviewAsync(w.Admin, w.ProjectId))!;
        var filtered = (await Planning(c).GetTicketsAsync(w.Admin, w.ProjectId, 1, new ProjectTicketFilter(Sprint: SprintScope.NoSprint)))!;

        Assert.Equal(2, overview.UnplannedTickets);
        Assert.Equal(overview.UnplannedTickets, filtered.Tickets.TotalCount);
    }

    [Fact]
    public async Task History_ResolvesSprintAndPersonNames_InsteadOfIds()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var sprint = await SprintAsync(c, w, "Named sprint", new DateOnly(2026, 9, 21), activate: true);
        var id = await TicketAsync(c, w, "Has history", sprint);
        await Tickets(c).AssignAsync(id, w.Agent.UserId, w.Manager);

        var history = await TicketQueries(c).GetHistoryAsync(id, w.Admin);

        var moved = history.Single(e => e.EventType == TicketEventType.SprintChanged && e.Field == "SprintId");
        Assert.Equal("Named sprint", moved.NewDisplay);
        var assigned = history.Single(e => e.EventType == TicketEventType.Assigned);
        Assert.False(string.IsNullOrEmpty(assigned.NewDisplay));
        Assert.False(Guid.TryParse(assigned.NewDisplay, out _));
    }

    [Fact] // Validation is unchanged: the suggestion is accepted, overlapping dates are still refused.
    public async Task SuggestedDates_AreAcceptedWhileAnActiveSprintExists_AndOverlapIsStillRejected()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await SprintAsync(c, w, "Running", new DateOnly(2026, 9, 21), activate: true);

        var overview = (await Planning(c).GetOverviewAsync(w.Admin, w.ProjectId))!;
        var suggestion = SprintDefaults.Suggest(overview.Sprints, new DateOnly(2026, 9, 21));

        var accepted = await Sprints(c).CreateSprintAsync(w.Admin, w.ProjectId, suggestion.Name, suggestion.Start, suggestion.End);
        Assert.True(accepted.Succeeded, accepted.Error);

        var overlapping = await Sprints(c).CreateSprintAsync(w.Admin, w.ProjectId, "Overlaps", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27));
        Assert.False(overlapping.Succeeded);
    }
}

/// <summary>Create Sprint suggestions: never overlap an existing planned/active sprint, two weeks
/// long, and only planned/active sprints matter (completed and cancelled ones are history).</summary>
public sealed class SprintDefaultsTests
{
    private static readonly DateOnly Today = new(2026, 9, 21);

    private static SprintSummary Sprint(int id, SprintStatus status, DateOnly start, DateOnly end) =>
        new(id, $"Sprint {id}", start, end, status, 0);

    [Fact]
    public void NoSprints_StartsToday_AndRunsTwoWeeks()
    {
        var s = SprintDefaults.Suggest([], Today);

        Assert.Equal(Today, s.Start);
        Assert.Equal(Today.AddDays(13), s.End);
        Assert.Equal("Sprint 1", s.Name);
    }

    [Fact]
    public void ActiveSprint_StartsTheDayAfterItEnds()
    {
        var s = SprintDefaults.Suggest([Sprint(1, SprintStatus.Active, Today.AddDays(-7), Today.AddDays(6))], Today);

        Assert.Equal(Today.AddDays(7), s.Start);
        Assert.Equal(Today.AddDays(20), s.End);
        Assert.Equal("Sprint 2", s.Name);
    }

    [Fact]
    public void PlannedSprintAfterTheActiveOne_PushesTheSuggestionPastIt()
    {
        var s = SprintDefaults.Suggest(
        [
            Sprint(1, SprintStatus.Active, Today.AddDays(-7), Today.AddDays(6)),
            Sprint(2, SprintStatus.Planned, Today.AddDays(7), Today.AddDays(20)),
        ], Today);

        Assert.Equal(Today.AddDays(21), s.Start);
        Assert.Equal("Sprint 3", s.Name);
    }

    [Fact]
    public void OnlyCompletedOrCancelledSprints_DoNotBlockToday()
    {
        var s = SprintDefaults.Suggest(
        [
            Sprint(1, SprintStatus.Completed, Today.AddDays(-28), Today.AddDays(-15)),
            Sprint(2, SprintStatus.Cancelled, Today.AddDays(-14), Today.AddDays(-1)),
        ], Today);

        Assert.Equal(Today, s.Start);
        Assert.Equal("Sprint 3", s.Name);
    }
}
