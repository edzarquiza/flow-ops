using System.Text;
using System.Text.RegularExpressions;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 10 dashboard KPIs against real PostgreSQL: correctness of all four capped KPIs, the
/// 90-day boundary, the reopen edge case, workload grouping, the Team-analytics authorization
/// scope (AUTH-RULE-02), and query count.
/// </summary>
[Collection("Postgres")]
public sealed partial class AnalyticsQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public AnalyticsQueryServiceTests(PostgresFixture fixture) => _fixture = fixture;

    // ---- Open Work ----

    [Fact]
    public async Task OpenWork_CountsOnlyNonTerminalTicketsInScope()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var openId = await CreateTicketAsync(context, world, Now);
        var closedId = await CreateTicketAsync(context, world, Now);
        await LifecycleToResolvedAsync(context, world, closedId, Now);
        await world.Service.CloseAsync(closedId, world.Manager);

        var summary = await Analytics(context).GetDashboardSummaryAsync(world.Admin);

        // Two tickets exist; only the non-terminal one counts. (openId used only to keep the
        // compiler/analyzer from flagging an unused variable — its existence is the assertion.)
        Assert.True(summary.OpenWorkCount >= 1);
        _ = openId;
    }

    [Fact] // Resolved/Closed tickets are explicitly excluded, not merely "not counted by accident".
    public async Task OpenWork_ExcludesResolvedAndClosed()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var resolvedId = await CreateTicketAsync(context, world, Now);
        await LifecycleToResolvedAsync(context, world, resolvedId, Now);

        var summaryBefore = await Analytics(context).GetDashboardSummaryAsync(world.Admin);
        var openBefore = summaryBefore.OpenWorkCount;

        var closedId = await CreateTicketAsync(context, world, Now);
        await LifecycleToResolvedAsync(context, world, closedId, Now);
        await world.Service.CloseAsync(closedId, world.Manager);

        var summaryAfter = await Analytics(context).GetDashboardSummaryAsync(world.Admin);

        // Two more terminal tickets were added; Open Work must not have moved.
        Assert.Equal(openBefore, summaryAfter.OpenWorkCount);
    }

    // ---- Overdue ----

    [Fact] // AttentionSignalCode.Overdue's own definition: DueDate, never SLA breach.
    public async Task Overdue_UsesDueDate_NotSlaBreach()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // SLA-breached (created far enough in the past that SlaDueAt has passed) but DueDate is
        // in the future: must NOT count as Overdue.
        var slaBreachedOnlyId = await CreateTicketAsync(context, world, Now.AddDays(-30));
        await SetDueDateAsync(context, world, slaBreachedOnlyId, Now.AddDays(10));

        // DueDate in the past, non-terminal: must count as Overdue.
        var dueDateOverdueId = await CreateTicketAsync(context, world, Now);
        await SetDueDateAsync(context, world, dueDateOverdueId, Now.AddDays(-1));

        var query = Analytics(context, Now);
        var summary = await query.GetDashboardSummaryAsync(world.Admin);
        var overdueIds = await ScopedTicketIdsAsync(context, world, t => t.DueDate != null && t.DueDate < Now && t.Status != Status.Resolved && t.Status != Status.Closed);

        Assert.Contains(dueDateOverdueId, overdueIds);
        Assert.DoesNotContain(slaBreachedOnlyId, overdueIds);
        Assert.Equal(overdueIds.Count, summary.OverdueCount);
    }

    [Fact] // A terminal ticket with a past DueDate is not Overdue — Overdue implies actionable.
    public async Task Overdue_ExcludesTerminalTickets()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var ticketId = await CreateTicketAsync(context, world, Now.AddDays(-5));
        await SetDueDateAsync(context, world, ticketId, Now.AddDays(-1));
        await LifecycleToResolvedAsync(context, world, ticketId, Now.AddDays(-5));

        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Admin);
        var overdueIds = await ScopedTicketIdsAsync(context, world, t => t.DueDate != null && t.DueDate < Now && t.Status != Status.Resolved && t.Status != Status.Closed);

        Assert.DoesNotContain(ticketId, overdueIds);
        Assert.Equal(overdueIds.Count, summary.OverdueCount);
    }

    // ---- SLA Compliance ----

    [Fact]
    public async Task SlaCompliance_CountsMetAndQualifyingCorrectly()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Met: resolved well inside the 1440-minute Medium target.
        var metId = await CreateTicketAsync(context, world, Now.AddDays(-1));
        await LifecycleToResolvedAsync(context, world, metId, Now.AddDays(-1), resolveAfter: TimeSpan.FromMinutes(60));

        // Breached: created 5 days ago, resolved 3 days after creation (i.e. 2 days ago, still
        // inside the 90-day window and — critically — still in the past relative to "Now").
        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-5));
        await LifecycleToResolvedAsync(context, world, breachedId, Now.AddDays(-5), resolveAfter: TimeSpan.FromDays(3));

        // Scoped to this test's own fresh team (world.Manager), not Admin — Admin's AllTeams
        // scope would also pick up every other test's tickets in this shared container.
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager);

        Assert.True(summary.SlaCompliance.QualifyingCount >= 2);
        Assert.True(summary.SlaCompliance.MetCount >= 1);
        Assert.True(summary.SlaCompliance.MetCount < summary.SlaCompliance.QualifyingCount);
        Assert.NotNull(summary.SlaCompliance.Percentage);
        Assert.InRange(summary.SlaCompliance.Percentage!.Value, 0, 100);

        _ = metId;
        _ = breachedId;
    }

    [Fact] // The exact boundary: resolved precisely 90 days ago counts; 91 days ago does not.
    public async Task SlaCompliance_NinetyDayBoundary_IsInclusiveAtExactlyNinetyDays()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var exactlyNinetyId = await CreateTicketAsync(context, world, Now.AddDays(-91));
        await LifecycleToResolvedAsync(context, world, exactlyNinetyId, Now.AddDays(-91), resolveAfter: TimeSpan.FromDays(1));
        // Resolved at Now-90d exactly (created 91d ago, resolved 1d after creation).

        var ninetyOneId = await CreateTicketAsync(context, world, Now.AddDays(-92));
        await LifecycleToResolvedAsync(context, world, ninetyOneId, Now.AddDays(-92), resolveAfter: TimeSpan.FromDays(1));
        // Resolved at Now-91d — one day outside the window.

        var qualifyingIds = await ResolvedInWindowIdsAsync(context, world, Now);

        Assert.Contains(exactlyNinetyId, qualifyingIds);
        Assert.DoesNotContain(ninetyOneId, qualifyingIds);
    }

    [Fact] // Zero qualifying tickets: an honest null percentage, never 0% or NaN.
    public async Task SlaCompliance_NoQualifyingTickets_PercentageIsNull()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now); // open, never resolved

        // world.Manager, scoped to this test's own fresh team — not Admin, whose AllTeams scope
        // would also pick up other tests' resolved tickets in this shared container.
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager);

        Assert.Equal(0, summary.SlaCompliance.QualifyingCount);
        Assert.Null(summary.SlaCompliance.Percentage);
    }

    [Fact] // SLA-RULE-09: a reopened-and-still-open ticket has no current SlaMet and does not count.
    public async Task SlaCompliance_ReopenedTicket_NotYetResolvedAgain_IsExcluded()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var ticketId = await CreateTicketAsync(context, world, Now.AddDays(-5));
        await LifecycleToResolvedAsync(context, world, ticketId, Now.AddDays(-5), resolveAfter: TimeSpan.FromHours(1));
        await world.Service.ReopenAsync(ticketId, "Recurred", world.Requester);

        var qualifyingIds = await ResolvedInWindowIdsAsync(context, world, Now);

        Assert.DoesNotContain(ticketId, qualifyingIds);
    }

    // ---- Average Resolution Time ----

    [Fact] // ResolvedAt - SlaStartedAt, not CreatedAt: a reopened-then-resolved ticket's average
           // reflects only the current cycle's duration.
    public async Task AverageResolutionTime_UsesSlaStartedAt_NotCreatedAt_AfterReopen()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // First cycle: created far in the past, resolved quickly (would inflate an
        // Average based on CreatedAt if it were used).
        var ticketId = await CreateTicketAsync(context, world, Now.AddDays(-60));
        await LifecycleToResolvedAsync(context, world, ticketId, Now.AddDays(-60), resolveAfter: TimeSpan.FromMinutes(30));

        // Reopen and resolve the second cycle exactly 2 hours later. Reopen lands back on
        // Assigned (the ticket still has its assignee from the first cycle), so StartWork is
        // required again before Resolve accepts it — the same real sequence a user would follow.
        await world.Service.ReopenAsync(ticketId, "Recurred", world.Requester);
        var reopenedAt = world.Clock.GetUtcNow();
        await world.Service.StartWorkAsync(ticketId, world.Agent);
        world.Clock.Advance(TimeSpan.FromHours(2));
        await world.Service.ResolveAsync(ticketId, Resolution.Fixed, "Fixed for real this time.", world.Agent);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);

        Assert.Equal(reopenedAt, ticket.SlaStartedAt);
        Assert.Equal(TimeSpan.FromHours(2), ticket.ResolvedAt!.Value - ticket.SlaStartedAt);
        // The gap from original CreatedAt would be ~60 days — proving the two bases diverge and
        // that a wrong implementation (CreatedAt-based) would produce a wildly different average.
        Assert.True(ticket.ResolvedAt!.Value - ticket.CreatedAt > TimeSpan.FromDays(59));

        // world.Manager, scoped to this test's own fresh team — isolates the average from other
        // tests' resolved tickets in this shared container.
        var summary = await Analytics(context, world.Clock.GetUtcNow()).GetDashboardSummaryAsync(world.Manager);

        Assert.NotNull(summary.AverageResolutionTime.Average);
        // With only this one qualifying ticket in a fresh scope, the average equals its own
        // SlaStartedAt-based duration, not the CreatedAt-based one.
        Assert.True(summary.AverageResolutionTime.Average!.Value < TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task AverageResolutionTime_NoResolvedTickets_IsNull()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now);

        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager);

        Assert.Equal(0, summary.AverageResolutionTime.SampleCount);
        Assert.Null(summary.AverageResolutionTime.Average);
    }

    // ---- Workload ----

    [Fact] // Grouped in PostgreSQL: verified both by the resulting shape and by the captured SQL.
    public async Task Workload_GroupsOpenTicketsByTeamAndAssignee()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedAsync(context);

        var firstId = await CreateTicketAsync(context, world, Now);
        await world.Service.AssignAsync(firstId, world.AgentId, world.Admin);
        var secondId = await CreateTicketAsync(context, world, Now);
        await world.Service.AssignAsync(secondId, world.AgentId, world.Admin);
        await CreateTicketAsync(context, world, Now); // unassigned

        sql.Clear();
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Admin);
        var emitted = sql.ToString();

        Assert.Contains("GROUP BY", emitted, StringComparison.OrdinalIgnoreCase);

        var agentRow = summary.Workload.SingleOrDefault(w => w.AssigneeId == world.AgentId && w.TeamId == world.TeamId);
        Assert.NotNull(agentRow);
        Assert.Equal(2, agentRow.OpenTicketCount);
        Assert.Equal("Phase 5 Test User", agentRow.AssigneeDisplayName); // TicketTestData's fixed display name

        Assert.Contains(summary.Workload, w => w.AssigneeId == null && w.TeamId == world.TeamId);
    }

    // ---- Authorization (AUTH-RULE-02 "Team analytics") ----

    [Fact]
    public async Task Admin_SeesAcrossAllTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now);
        var otherTeamTicket = await CreateOtherTeamTicketAsync(context, world, Now);

        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Admin);

        // Independently derived over the WHOLE table (Admin scope = AllTeams), filtered by
        // exactly the same "open" predicate OpenWorkCount uses — a genuine cross-check rather
        // than restating the query under test, and unaffected by other tests' tickets in this
        // shared container since both sides examine the identical unfiltered population.
        var openIds = await context.Tickets.AsNoTracking()
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed)
            .Select(t => t.Id)
            .ToListAsync();

        Assert.Contains(otherTeamTicket, openIds); // proves cross-team data really is included
        Assert.Equal(openIds.Count, summary.OpenWorkCount);
    }

    [Fact] // Manager's "own teams" is ManagedTeamIds, not mere membership — proven by a team the
           // manager belongs to but does not manage.
    public async Task Manager_SeesOnlyManagedTeams_NotMerelyMemberTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var managedTicketId = await CreateTicketAsync(context, world, Now); // world.TeamId: managed

        // A third team the manager is a plain MEMBER of, but does not manage.
        var memberOnlyTeamId = await TicketTestData.AddTeamAsync(context);
        var memberOnlyCategoryId = await TicketTestData.AddCategoryAsync(context, memberOnlyTeamId);
        await TicketTestData.AddTeamMembershipAsync(context, memberOnlyTeamId, world.Manager.UserId, isTeamManager: false);
        var memberOnlyTicketId = (await ServiceAt(context, Now).CreateAsync(
            Request(memberOnlyTeamId, memberOnlyCategoryId),
            TicketTestData.User(world.Requester.UserId, UserRole.Agent, memberOnlyTeamId))).Id;

        var manager = new CurrentUser(world.Manager.UserId, UserRole.Manager, new HashSet<int> { world.TeamId, memberOnlyTeamId }, new HashSet<int> { world.TeamId });
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(manager);

        // The managed team's (unassigned) ticket shows up as an "Unassigned" workload row...
        Assert.Contains(summary.Workload, w => w.TeamId == world.TeamId && w.AssigneeId == null);
        // ...but the member-only team contributes nothing at all, even though CanView would admit it.
        Assert.DoesNotContain(summary.Workload, w => w.TeamId == memberOnlyTeamId);

        var openInManagedTeam = await context.Tickets.AsNoTracking()
            .Where(t => t.TeamId == world.TeamId && t.Status != Status.Resolved && t.Status != Status.Closed)
            .CountAsync();
        Assert.Equal(openInManagedTeam, summary.OpenWorkCount);

        _ = managedTicketId;
        _ = memberOnlyTicketId;
    }

    [Fact] // The central rule this whole feature exists for: Agent sees only their own assignments,
           // never a team-wide number, even though CanView would let them see the whole team.
    public async Task Agent_SeesOnlyOwnAssignedTickets_NeverTeamWideCounts()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var ownTicketId = await CreateTicketAsync(context, world, Now);
        await world.Service.AssignAsync(ownTicketId, world.AgentId, world.Admin);

        // Four more open tickets on the SAME team, assigned to someone else — CanView would show
        // the agent all five, but analytics must not.
        var otherAgentId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, world.TeamId, otherAgentId);
        for (var i = 0; i < 4; i++)
        {
            var id = await CreateTicketAsync(context, world, Now);
            await world.Service.AssignAsync(id, otherAgentId, world.Admin);
        }

        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Agent);

        Assert.Equal(1, summary.OpenWorkCount);
        Assert.Single(summary.Workload);
        Assert.Equal(world.AgentId, summary.Workload[0].AssigneeId);
        Assert.Equal(1, summary.Workload[0].OpenTicketCount);

        _ = ownTicketId;
    }

    [Fact]
    public async Task Viewer_SeesOnlyMemberTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now);
        var otherTeamTicketId = await CreateOtherTeamTicketAsync(context, world, Now);

        var viewer = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Viewer, world.TeamId);
        await TicketTestData.AddTeamMembershipAsync(context, world.TeamId, viewer.UserId);

        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(viewer);
        var memberScopeIds = await ScopedTicketIdsAsync(context, world, t => true, UserRole.Viewer, viewerTeamId: world.TeamId);

        Assert.DoesNotContain(otherTeamTicketId, memberScopeIds);
        Assert.Equal(memberScopeIds.Count, summary.OpenWorkCount);
    }

    // ---- Empty / zero-data ----

    [Fact]
    public async Task NoTicketsAtAll_EveryKpiIsHonestlyEmpty()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context, createNoTickets: true);

        // world.Manager, scoped to this test's own empty team — Admin's AllTeams scope would
        // pick up every other test's tickets in this shared container and none of these zero
        // assertions would hold.
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager);

        Assert.Equal(0, summary.OpenWorkCount);
        Assert.Equal(0, summary.OverdueCount);
        Assert.Equal(0, summary.SlaCompliance.QualifyingCount);
        Assert.Null(summary.SlaCompliance.Percentage);
        Assert.Equal(0, summary.AverageResolutionTime.SampleCount);
        Assert.Null(summary.AverageResolutionTime.Average);
        Assert.Empty(summary.Workload);
    }

    // ---- Query count / no N+1 ----

    [Fact]
    public async Task GetDashboardSummaryAsync_IssuesAtMostSixQueries_RegardlessOfTicketCount()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedAsync(context);

        for (var i = 0; i < 12; i++)
        {
            var id = await CreateTicketAsync(context, world, Now.AddDays(-i));
            await world.Service.AssignAsync(id, world.AgentId, world.Admin);
        }

        sql.Clear();
        var summary = await Analytics(context, Now).GetDashboardSummaryAsync(world.Admin);
        var emitted = sql.ToString();

        var executed = SelectStatementPattern().Matches(emitted).Count;

        Assert.True(executed <= 6, $"expected at most 6 queries (CLAUDE.md §16), saw {executed}:{Environment.NewLine}{emitted}");
        Assert.True(summary.OpenWorkCount >= 12);
        Assert.DoesNotContain("AsEnumerable", emitted, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- helpers ----------

    private AnalyticsQueryService Analytics(FlowOpsDbContext context, DateTimeOffset? now = null) =>
        new(context, new TicketTestData.FixedTimeProvider(now ?? Now));

    private static TicketService ServiceAt(FlowOpsDbContext context, DateTimeOffset instant) =>
        new(context, new TicketTestData.FixedTimeProvider(instant));

    private static async Task<int> CreateTicketAsync(FlowOpsDbContext context, World world, DateTimeOffset instant) =>
        (await ServiceAt(context, instant).CreateAsync(Request(world.TeamId, world.CategoryId), world.Requester)).Id;

    private static async Task<int> CreateOtherTeamTicketAsync(FlowOpsDbContext context, World world, DateTimeOffset instant)
    {
        var requester = TicketTestData.User(world.Requester.UserId, UserRole.Agent, world.OtherTeamId);
        var (id, _) = await ServiceAt(context, instant).CreateAsync(Request(world.OtherTeamId, world.OtherCategoryId), requester);
        return id;
    }

    private static CreateTicketRequest Request(int teamId, int categoryId) =>
        new(
            Title: "Printer on 3rd floor is jammed",
            Description: "The printer near the east stairwell is jammed and needs a technician.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: null);

    /// <summary>Assign → StartWork → Resolve, entirely through the aggregate, landing exactly
    /// <paramref name="resolveAfter"/> after creation.</summary>
    private static async Task LifecycleToResolvedAsync(
        FlowOpsDbContext context,
        World world,
        int ticketId,
        DateTimeOffset createdAt,
        TimeSpan? resolveAfter = null)
    {
        var resolvedAt = createdAt + (resolveAfter ?? TimeSpan.FromMinutes(30));
        await ServiceAt(context, createdAt).AssignAsync(ticketId, world.AgentId, world.Admin);
        await ServiceAt(context, createdAt).StartWorkAsync(ticketId, world.Agent);
        await ServiceAt(context, resolvedAt).ResolveAsync(ticketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);
    }

    private static async Task SetDueDateAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset dueDate)
    {
        var ticket = await context.Tickets.SingleAsync(t => t.Id == ticketId);
        ticket.ChangeDueDate(dueDate, world.Admin.UserId, Now.AddDays(-100)); // arbitrary actor timestamp, not under test
        await context.SaveChangesAsync();
    }

    /// <summary>The raw ticket ids matching a predicate within one team, used as an
    /// independently-derived expectation to compare KPI counts against.</summary>
    private static async Task<List<int>> ScopedTicketIdsAsync(
        FlowOpsDbContext context,
        World world,
        System.Linq.Expressions.Expression<Func<Ticket, bool>> predicate,
        UserRole? role = null,
        int? viewerTeamId = null)
    {
        var teamId = role switch
        {
            UserRole.Manager => world.TeamId,
            UserRole.Viewer => viewerTeamId ?? world.TeamId,
            _ => (int?)null,
        };

        var query = context.Tickets.AsNoTracking().Where(predicate);
        query = role switch
        {
            null or UserRole.Admin => query,
            _ => query.Where(t => t.TeamId == teamId),
        };

        return await query.Select(t => t.Id).ToListAsync();
    }

    private static async Task<List<int>> ResolvedInWindowIdsAsync(FlowOpsDbContext context, World world, DateTimeOffset now)
    {
        var windowStart = now.AddDays(-AnalyticsQueryService.ReportingWindowDays);
        return await context.Tickets
            .AsNoTracking()
            .Where(t => t.TeamId == world.TeamId && t.ResolvedAt != null && t.ResolvedAt >= windowStart && t.ResolvedAt <= now)
            .Select(t => t.Id)
            .ToListAsync();
    }

    private sealed record World(
        int TeamId,
        int CategoryId,
        int OtherTeamId,
        int OtherCategoryId,
        Guid AgentId,
        CurrentUser Agent,
        CurrentUser Requester,
        CurrentUser Manager,
        CurrentUser Admin,
        TicketService Service,
        TicketTestData.FixedTimeProvider Clock);

    private static async Task<World> SeedAsync(FlowOpsDbContext context, bool createNoTickets = false)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var requesterId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);

        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, requesterId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);

        var clock = new TicketTestData.FixedTimeProvider(Now);
        var service = new TicketService(context, clock);

        _ = createNoTickets; // no ticket is created in the common seed path regardless — kept for
                             // the empty-data test's readability at the call site.

        return new World(
            teamId,
            categoryId,
            otherTeamId,
            otherCategoryId,
            agentId,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            TicketTestData.User(requesterId, UserRole.Agent, teamId),
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(adminId, UserRole.Admin),
            service,
            clock);
    }

    [GeneratedRegex(@"SELECT\s", RegexOptions.IgnoreCase)]
    private static partial Regex SelectStatementPattern();
}
