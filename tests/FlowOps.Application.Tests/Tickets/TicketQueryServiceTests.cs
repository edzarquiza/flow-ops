using System.Text;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 5 read side against real PostgreSQL: the team-scoped work queue, deterministic
/// pagination, and detail retrieval that cannot distinguish "not yours" from "not there".
/// </summary>
[Collection("Postgres")]
public sealed class TicketQueryServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketQueryServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact] // Create → appears in the caller's queue.
    public async Task GetQueueAsync_ReturnsTicketVisibleToTheCaller()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 1, ticketsInTeamB: 0);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Matches(@"^FO-\d{6}$", item.Reference);
        Assert.Equal(world.TeamAName, item.TeamName);
        Assert.Equal(Status.Open, item.Status);
    }

    [Fact] // AUTH-RULE-05: a ticket on a team the caller does not belong to is never returned.
    public async Task GetQueueAsync_ExcludesTicketsFromTeamsTheCallerIsNotIn()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 2, ticketsInTeamB: 3);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal(world.TeamAName, i.TeamName));
    }

    [Fact] // AUTH-RULE-02 "View tickets: all" — Admin sees across every team.
    public async Task GetQueueAsync_AdminSeesTicketsAcrossTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 2, ticketsInTeamB: 3);
        var query = new TicketQueryService(context, TimeProvider.System);

        // An Admin's scope is unfiltered, so their queue also contains tickets other tests in this
        // shared container created. Walk the pages rather than assuming these are the newest —
        // that assumption held when this was written and stopped holding as later phases added
        // their own fixtures.
        var visibleIds = await AllVisibleIdsAsync(query, world.Admin);

        Assert.All(world.TeamATicketIds, id => Assert.Contains(id, visibleIds));
        Assert.All(world.TeamBTicketIds, id => Assert.Contains(id, visibleIds));

        // The same Admin sees team B's tickets that the team-A agent cannot.
        var agentIds = (await query.GetQueueAsync(world.AgentInTeamA, 1)).Items.Select(i => i.Id).ToHashSet();
        Assert.All(world.TeamBTicketIds, id => Assert.DoesNotContain(id, agentIds));
    }

    /// <summary>
    /// AUTH-RULE-05 / CLAUDE.md §7.2: the scope must be a WHERE clause, not a post-fetch filter.
    /// Asserted against the SQL EF Core actually emitted, and by proving the row count the
    /// database returned equals the authorized count rather than the table total.
    /// </summary>
    [Fact]
    public async Task GetQueueAsync_AppliesAuthorizationScopeInSql()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedWorldAsync(context, ticketsInTeamA: 2, ticketsInTeamB: 3);

        sql.Clear();
        var page = await new TicketQueryService(context, TimeProvider.System).GetQueueAsync(world.AgentInTeamA, 1);
        var emitted = sql.ToString();

        // The team predicate reached the database…
        Assert.Contains("team_id", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", emitted, StringComparison.OrdinalIgnoreCase);
        // …and the paging was done there too, rather than in memory.
        Assert.Contains("LIMIT", emitted, StringComparison.OrdinalIgnoreCase);

        // The caller is authorized for exactly their own team's two tickets, while the table
        // holds strictly more than that — so the rows were excluded by the query, not afterwards.
        Assert.Equal(2, page.TotalCount);
        Assert.True(
            context.Tickets.Count() > page.TotalCount,
            "the table must contain rows the caller was not given, or this proves nothing");
    }

    /// <summary>
    /// Guards the one deliberate duplication in this slice: <c>ApplyViewScope</c>'s SQL predicate
    /// must agree with <see cref="TicketAccessPolicy.CanView"/>. Every row the scope hands back is
    /// checked against the policy itself, and every row the policy rejects is asserted absent.
    /// </summary>
    [Fact]
    public async Task GetQueueAsync_ScopedRowsAgreeWithCanView()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 2, ticketsInTeamB: 3);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);
        var returnedIds = page.Items.Select(i => i.Id).ToHashSet();

        // Everything returned is something CanView accepts.
        foreach (var item in page.Items)
        {
            var snapshot = new TicketAuthorizationSnapshot(
                item.Id,
                TeamIdFor(world, item.TeamName),
                RequesterId: Guid.Empty,
                AssigneeId: null,
                item.Status);

            Assert.True(TicketAccessPolicy.CanView(snapshot, world.AgentInTeamA));
        }

        // And everything CanView rejects is absent.
        foreach (var teamBTicketId in world.TeamBTicketIds)
        {
            var snapshot = new TicketAuthorizationSnapshot(
                teamBTicketId,
                world.TeamBId,
                RequesterId: Guid.Empty,
                AssigneeId: null,
                Status.Open);

            Assert.False(TicketAccessPolicy.CanView(snapshot, world.AgentInTeamA));
            Assert.DoesNotContain(teamBTicketId, returnedIds);
        }
    }

    [Fact] // CLAUDE.md §7.2: CreatedAt DESC, Id DESC.
    public async Task GetQueueAsync_OrdersByCreatedAtThenIdDescending()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 5, ticketsInTeamB: 0);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);
        var ordered = page.Items
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .ToList();

        Assert.Equal(ordered.Select(i => i.Id), page.Items.Select(i => i.Id));
    }

    [Fact] // Same instant on every ticket: only the Id tiebreak can order them deterministically.
    public async Task GetQueueAsync_IdBreaksTiesWhenCreatedAtIsIdentical()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 4, ticketsInTeamB: 0, advanceClock: false);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);
        var ids = page.Items.Select(i => i.Id).ToList();

        Assert.Equal(ids.OrderByDescending(i => i), ids);
    }

    [Fact] // Deterministic paging: stable across calls, no row on two pages, none missing.
    public async Task GetQueueAsync_PaginationIsDeterministicAndNonOverlapping()
    {
        await using var context = _fixture.CreateContext();
        const int total = TicketQueryService.PageSize + 7;
        var world = await SeedWorldAsync(context, ticketsInTeamA: total, ticketsInTeamB: 0);
        var query = new TicketQueryService(context, TimeProvider.System);

        var firstPage = await query.GetQueueAsync(world.AgentInTeamA, 1);
        var secondPage = await query.GetQueueAsync(world.AgentInTeamA, 2);
        var firstPageAgain = await query.GetQueueAsync(world.AgentInTeamA, 1);

        Assert.Equal(TicketQueryService.PageSize, firstPage.Items.Count);
        Assert.Equal(7, secondPage.Items.Count);
        Assert.Equal(total, firstPage.TotalCount);
        Assert.Equal(2, firstPage.TotalPages);

        // Repeating page 1 returns the identical sequence.
        Assert.Equal(firstPage.Items.Select(i => i.Id), firstPageAgain.Items.Select(i => i.Id));

        // The two pages are disjoint and together cover every authorized ticket exactly once.
        var combined = firstPage.Items.Concat(secondPage.Items).Select(i => i.Id).ToList();
        Assert.Equal(total, combined.Count);
        Assert.Equal(total, combined.Distinct().Count());
    }

    [Fact] // A page past the end is empty but still reports the true authorized total.
    public async Task GetQueueAsync_PageBeyondLastPage_IsEmptyWithCorrectTotal()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 3, ticketsInTeamB: 0);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 99);

        Assert.Empty(page.Items);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(99, page.PageNumber);
    }

    [Fact] // No visible tickets at all: empty page, zero total, one page.
    public async Task GetQueueAsync_NoVisibleTickets_ReturnsEmptyPage()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 0, ticketsInTeamB: 4);
        var query = new TicketQueryService(context, TimeProvider.System);

        var page = await query.GetQueueAsync(world.AgentInTeamA, 1);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task GetDetailAsync_AuthorizedCaller_ReturnsFullDetail()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 1, ticketsInTeamB: 0);
        var query = new TicketQueryService(context, TimeProvider.System);
        var ticketId = world.TeamATicketIds[0];

        var detail = await query.GetDetailAsync(ticketId, world.AgentInTeamA);

        Assert.NotNull(detail);
        Assert.Equal(ticketId, detail.Id);
        Assert.Equal(world.TeamAName, detail.TeamName);
        Assert.Equal("Phase 5 Test User", detail.RequesterDisplayName);
        Assert.Null(detail.AssigneeDisplayName);
        Assert.Null(detail.ProjectName);
        Assert.NotEmpty(detail.Description);
    }

    [Fact] // Cross-team detail is indistinguishable from a nonexistent ticket: both null.
    public async Task GetDetailAsync_TicketOutsideCallerTeams_ReturnsNullLikeNotFound()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 0, ticketsInTeamB: 1);
        var query = new TicketQueryService(context, TimeProvider.System);
        var teamBTicketId = world.TeamBTicketIds[0];

        var forbidden = await query.GetDetailAsync(teamBTicketId, world.AgentInTeamA);
        var missing = await query.GetDetailAsync(int.MaxValue, world.AgentInTeamA);

        Assert.Null(forbidden);
        Assert.Null(missing);
    }

    [Fact]
    public async Task GetDetailAsync_Admin_CanViewTicketOutsideAnyTeamMembership()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedWorldAsync(context, ticketsInTeamA: 0, ticketsInTeamB: 1);
        var query = new TicketQueryService(context, TimeProvider.System);
        var teamBTicketId = world.TeamBTicketIds[0];

        var detail = await query.GetDetailAsync(teamBTicketId, world.Admin);

        Assert.NotNull(detail);
        Assert.Equal(world.TeamBName, detail.TeamName);
    }

    /// <summary>Every ticket id the caller can see, across all pages of their queue.</summary>
    private static async Task<HashSet<int>> AllVisibleIdsAsync(TicketQueryService query, CurrentUser user)
    {
        var ids = new HashSet<int>();
        var pageNumber = 1;

        while (true)
        {
            var page = await query.GetQueueAsync(user, pageNumber);
            foreach (var item in page.Items)
            {
                ids.Add(item.Id);
            }

            if (!page.HasNextPage)
            {
                return ids;
            }

            pageNumber++;
        }
    }

    private static int TeamIdFor(World world, string teamName) =>
        teamName == world.TeamAName ? world.TeamAId : world.TeamBId;

    /// <summary>
    /// Ticket ids are returned per team because the Postgres container is shared across the whole
    /// test collection: an Admin caller is unscoped by definition and therefore also sees tickets
    /// other tests created. Admin assertions must key off the ids this test created rather than
    /// absolute table totals, which are not stable across a shared database.
    /// </summary>
    private sealed record World(
        int TeamAId,
        string TeamAName,
        int TeamBId,
        string TeamBName,
        IReadOnlyList<int> TeamATicketIds,
        IReadOnlyList<int> TeamBTicketIds,
        CurrentUser AgentInTeamA,
        CurrentUser Admin);

    /// <summary>
    /// Two teams, one agent who belongs only to team A, and one admin who belongs to neither —
    /// the minimum world needed to tell "scoped by membership" apart from "sees everything".
    /// </summary>
    private static async Task<World> SeedWorldAsync(
        FlowOpsDbContext context,
        int ticketsInTeamA,
        int ticketsInTeamB,
        bool advanceClock = true)
    {
        var teamAId = await TicketTestData.AddTeamAsync(context);
        var teamBId = await TicketTestData.AddTeamAsync(context);
        var categoryAId = await TicketTestData.AddCategoryAsync(context, teamAId);
        var categoryBId = await TicketTestData.AddCategoryAsync(context, teamBId);

        var agentId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamAId, agentId);
        var adminId = await TicketTestData.AddUserAsync(context);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var service = new TicketService(context, clock);
        var creator = TicketTestData.User(agentId, UserRole.Admin, teamAId, teamBId);

        var teamATicketIds = new List<int>();
        for (var i = 0; i < ticketsInTeamA; i++)
        {
            var (id, _) = await service.CreateAsync(TicketRequest(teamAId, categoryAId), creator);
            teamATicketIds.Add(id);
            if (advanceClock)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var teamBTicketIds = new List<int>();
        for (var i = 0; i < ticketsInTeamB; i++)
        {
            var (id, _) = await service.CreateAsync(TicketRequest(teamBId, categoryBId), creator);
            teamBTicketIds.Add(id);
            if (advanceClock)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var teamNames = context.Teams
            .Where(t => t.Id == teamAId || t.Id == teamBId)
            .ToDictionary(t => t.Id, t => t.Name);

        return new World(
            teamAId,
            teamNames[teamAId],
            teamBId,
            teamNames[teamBId],
            teamATicketIds,
            teamBTicketIds,
            TicketTestData.User(agentId, UserRole.Agent, teamAId),
            TicketTestData.User(adminId, UserRole.Admin));
    }

    private static CreateTicketRequest TicketRequest(int teamId, int categoryId) =>
        new(
            Title: "Printer on 3rd floor is jammed",
            Description: "The printer near the east stairwell is jammed and needs a technician.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: null);
}
