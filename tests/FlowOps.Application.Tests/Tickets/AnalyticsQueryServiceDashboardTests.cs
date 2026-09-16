using System.Text;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 20 (ADR-0019): the dashboard's five new analytics methods against real PostgreSQL —
/// filter semantics (default range, custom range, team, work type, combinations, malformed
/// input), each section's own aggregation correctness with real value assertions, the SLA
/// breakdown's reuse of <see cref="SlaPolicy"/>, organization/role scope, empty-period behavior,
/// and boundary dates. Shares <see cref="AnalyticsQueryServiceTests"/>'s seeding helpers (same
/// partial class, same file collection) rather than re-implementing them.
/// </summary>
public sealed partial class AnalyticsQueryServiceTests
{
    // ---- KPI strip regression: RangeDays must never leak into the fixed 90-day KPIs ----

    [Fact] // Guards against a real bug caught during this phase's own perf review: the dashboard
           // filter's RangeDays must never narrow Open Work / Overdue / current workload — those
           // are present-tense or fixed-90-day facts, never scoped by the filter bar's date range.
    public async Task GetDashboardSummaryAsync_OpenWorkAndWorkload_AreUnaffectedByFilterRangeDays()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Created 60 days ago — inside the 90-day KPI window used elsewhere, but OUTSIDE a
        // hypothetical 30-day dashboard filter range, and still open today.
        await CreateTicketAsync(context, world, Now.AddDays(-60));

        var unfiltered = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager);
        var filteredToThirtyDays = await Analytics(context, Now).GetDashboardSummaryAsync(world.Manager, DashboardFilter.From(30, null, null));

        Assert.Equal(unfiltered.OpenWorkCount, filteredToThirtyDays.OpenWorkCount);
        Assert.Equal(unfiltered.Workload.Count, filteredToThirtyDays.Workload.Count);
        Assert.True(filteredToThirtyDays.OpenWorkCount >= 1);
    }

    // ---- DashboardFilter itself ----

    [Fact]
    public void DashboardFilter_From_CoercesInvalidRangeToDefault()
    {
        Assert.Equal(90, DashboardFilter.From(null, null, null).RangeDays);
        Assert.Equal(90, DashboardFilter.From(45, null, null).RangeDays);
        Assert.Equal(90, DashboardFilter.From(-30, null, null).RangeDays);
        Assert.Equal(30, DashboardFilter.From(30, null, null).RangeDays);
        Assert.Equal(180, DashboardFilter.From(180, null, null).RangeDays);
    }

    [Fact]
    public void DashboardFilter_From_PassesThroughTeamAndWorkType()
    {
        var filter = DashboardFilter.From(30, 7, WorkType.Problem);
        Assert.Equal(7, filter.TeamId);
        Assert.Equal(WorkType.Problem, filter.WorkType);
    }

    // ---- Ticket volume trend ----

    [Fact]
    public async Task VolumeTrend_DailyGranularity_ForThirtyDayRange_FillsEveryDayIncludingZeros()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now.AddDays(-5));
        await CreateTicketAsync(context, world, Now.AddDays(-5));
        await CreateTicketAsync(context, world, Now.AddDays(-1));

        var filter = DashboardFilter.From(30, null, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Manager, filter);

        Assert.Equal(31, points.Count); // inclusive of both endpoints, one point per day
        Assert.Equal(2, points.Single(p => p.PeriodStart == DateOnly.FromDateTime(Now.AddDays(-5).UtcDateTime)).Count);
        Assert.Equal(1, points.Single(p => p.PeriodStart == DateOnly.FromDateTime(Now.AddDays(-1).UtcDateTime)).Count);
        Assert.Contains(points, p => p.Count == 0); // most days in this fresh scope are empty
    }

    [Fact]
    public async Task VolumeTrend_WeeklyGranularity_ForNinetyDayRange_BucketsIntoWeeks()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now.AddDays(-2));
        await CreateTicketAsync(context, world, Now.AddDays(-3));

        var filter = DashboardFilter.From(90, null, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Manager, filter);

        // 91 days / 7 = 13 whole weeks + a partial one.
        Assert.True(points.Count is 13 or 14);
        Assert.Equal(2, points.Sum(p => p.Count));
        Assert.Equal(2, points[^1].Count); // both fall in the final (most recent) bucket
    }

    [Fact]
    public async Task VolumeTrend_OutsideRange_IsExcluded()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now.AddDays(-45)); // outside a 30-day range

        var filter = DashboardFilter.From(30, null, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Manager, filter);

        Assert.Equal(0, points.Sum(p => p.Count));
    }

    [Fact]
    public async Task VolumeTrend_TeamFilter_ExcludesOtherTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now);
        await CreateOtherTeamTicketAsync(context, world, Now);

        var filter = DashboardFilter.From(30, world.TeamId, null);
        var admin = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Admin);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(admin, filter);

        Assert.Equal(1, points.Sum(p => p.Count));
    }

    [Fact]
    public async Task VolumeTrend_WorkTypeFilter_NarrowsToMatchingTickets()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await ServiceAt(context, Now).CreateAsync(Request(world.TeamId, world.CategoryId) with { WorkType = WorkType.Incident }, world.Requester);
        await ServiceAt(context, Now).CreateAsync(Request(world.TeamId, world.CategoryId) with { WorkType = WorkType.Problem }, world.Requester);

        var filter = DashboardFilter.From(30, null, WorkType.Problem);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Manager, filter);

        Assert.Equal(1, points.Sum(p => p.Count));
    }

    [Fact] // A team id outside the caller's own scope must yield zero results, never a leak.
    public async Task VolumeTrend_TeamFilterOutsideCallerScope_YieldsEmpty_NotOtherTeamsData()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var otherTeamTicketId = await CreateOtherTeamTicketAsync(context, world, Now);

        // Manager's scope is world.TeamId only; ask for otherTeamId anyway.
        var filter = DashboardFilter.From(30, world.OtherTeamId, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Manager, filter);

        Assert.Equal(0, points.Sum(p => p.Count));
        _ = otherTeamTicketId;
    }

    // ---- Status breakdown ----

    [Fact]
    public async Task StatusBreakdown_ReturnsEveryStatus_InWorkflowOrder_WithRealCounts()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now); // Open
        var assignedId = await CreateTicketAsync(context, world, Now);
        await world.Service.AssignAsync(assignedId, world.AgentId, world.Admin); // Assigned

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetStatusBreakdownAsync(world.Manager, filter);

        Assert.Equal(Enum.GetValues<Status>(), breakdown.Select(b => b.Status).ToArray());
        Assert.Equal(1, breakdown.Single(b => b.Status == Status.Open).Count);
        Assert.Equal(1, breakdown.Single(b => b.Status == Status.Assigned).Count);
        Assert.Equal(0, breakdown.Single(b => b.Status == Status.Closed).Count);
    }

    [Fact]
    public async Task StatusBreakdown_EmptyPeriod_ReturnsAllStatusesAtZero()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context, createNoTickets: true);

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetStatusBreakdownAsync(world.Manager, filter);

        Assert.Equal(Enum.GetValues<Status>().Length, breakdown.Count);
        Assert.All(breakdown, b => Assert.Equal(0, b.Count));
    }

    // ---- Team workload ----

    [Fact] // Deliberately NOT date-scoped — a ticket created long ago but still open still counts.
    public async Task TeamWorkload_IsNotDateScoped_OldOpenTicketStillCounts()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await CreateTicketAsync(context, world, Now.AddDays(-200));

        var filter = DashboardFilter.From(30, null, null); // 30-day range, ticket created 200 days ago
        var workload = await Analytics(context, Now).GetTeamWorkloadBreakdownAsync(world.Manager, filter);

        var row = Assert.Single(workload);
        Assert.Equal(world.TeamId, row.TeamId);
        Assert.Equal(1, row.OpenTicketCount);
    }

    [Fact]
    public async Task TeamWorkload_ExcludesResolvedAndClosed()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var resolvedId = await CreateTicketAsync(context, world, Now);
        await LifecycleToResolvedAsync(context, world, resolvedId, Now);

        var filter = DashboardFilter.From(90, null, null);
        var workload = await Analytics(context, Now).GetTeamWorkloadBreakdownAsync(world.Manager, filter);

        Assert.Empty(workload);
    }

    [Fact] // A Manager's team-workload result must never include a team they don't manage.
    public async Task TeamWorkload_Manager_NeverIncludesUnmanagedTeam()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var otherTicketId = await CreateOtherTeamTicketAsync(context, world, Now);

        var filter = DashboardFilter.From(90, null, null);
        var workload = await Analytics(context, Now).GetTeamWorkloadBreakdownAsync(world.Manager, filter);

        Assert.DoesNotContain(workload, w => w.TeamId == world.OtherTeamId);
        _ = otherTicketId;
    }

    // ---- SLA breakdown ----

    [Fact]
    public async Task SlaBreakdown_ClassifiesUsingSlaPolicy_MetAndBreached()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var metId = await CreateTicketAsync(context, world, Now.AddDays(-1));
        await LifecycleToResolvedAsync(context, world, metId, Now.AddDays(-1), resolveAfter: TimeSpan.FromMinutes(30));

        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-1));
        await LifecycleToResolvedAsync(context, world, breachedId, Now.AddDays(-1), resolveAfter: TimeSpan.FromDays(3));

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetSlaBreakdownAsync(world.Manager, filter);

        Assert.Equal(1, breakdown.MetCount);
        Assert.Equal(1, breakdown.BreachedCount);
        Assert.Equal(2, breakdown.Total);
    }

    [Fact]
    public async Task SlaBreakdown_OpenTicketWithinTarget_CountsAsWithin()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now); // just created, still Open, well inside target

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetSlaBreakdownAsync(world.Manager, filter);

        Assert.Equal(1, breakdown.WithinCount);
        Assert.Equal(0, breakdown.BreachedCount);
        Assert.Equal(0, breakdown.AtRiskCount);
    }

    [Fact]
    public async Task SlaBreakdown_PendingTicket_CountsAsPaused()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var ticketId = await CreateTicketAsync(context, world, Now);
        await world.Service.AssignAsync(ticketId, world.AgentId, world.Admin);
        await world.Service.StartWorkAsync(ticketId, world.Agent);
        await world.Service.PutOnHoldAsync(ticketId, "Waiting on requester.", world.Agent);

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetSlaBreakdownAsync(world.Manager, filter);

        Assert.Equal(1, breakdown.PausedCount);
    }

    [Fact]
    public async Task SlaBreakdown_EmptyPeriod_IsAllZero()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context, createNoTickets: true);

        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetSlaBreakdownAsync(world.Manager, filter);

        Assert.Equal(0, breakdown.Total);
    }

    // ---- Resolution time by work type ----

    [Fact]
    public async Task ResolutionByWorkType_AveragesOnlyResolvedTicketsOfThatType()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var incidentId = (await ServiceAt(context, Now.AddDays(-1)).CreateAsync(
            Request(world.TeamId, world.CategoryId) with { WorkType = WorkType.Incident }, world.Requester)).Id;
        await LifecycleToResolvedAsync(context, world, incidentId, Now.AddDays(-1), resolveAfter: TimeSpan.FromHours(2));

        var filter = DashboardFilter.From(30, null, null);
        var byType = await Analytics(context, Now).GetResolutionTimeByWorkTypeAsync(world.Manager, filter);

        Assert.Equal(Enum.GetValues<WorkType>().Length, byType.Count);
        var incidentRow = byType.Single(r => r.WorkType == WorkType.Incident);
        Assert.Equal(1, incidentRow.SampleCount);
        Assert.NotNull(incidentRow.Average);
        Assert.Equal(TimeSpan.FromHours(2), incidentRow.Average!.Value);

        var problemRow = byType.Single(r => r.WorkType == WorkType.Problem);
        Assert.Equal(0, problemRow.SampleCount);
        Assert.Null(problemRow.Average);
    }

    [Fact]
    public async Task ResolutionByWorkType_UsesResolvedAt_NotCreatedAt_ForRangeMembership()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Created outside a 30-day window, but resolved inside it.
        var ticketId = await CreateTicketAsync(context, world, Now.AddDays(-60));
        await LifecycleToResolvedAsync(context, world, ticketId, Now.AddDays(-60), resolveAfter: TimeSpan.FromDays(59));

        var filter = DashboardFilter.From(30, null, null);
        var byType = await Analytics(context, Now).GetResolutionTimeByWorkTypeAsync(world.Manager, filter);

        Assert.Equal(1, byType.Single(r => r.WorkType == WorkType.Incident).SampleCount);
    }

    // ---- Filter team options (leak prevention) ----

    [Fact]
    public async Task FilterTeamOptions_Agent_ReturnsEmpty()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var options = await Analytics(context, Now).GetFilterTeamOptionsAsync(world.Agent);

        Assert.Empty(options);
    }

    [Fact]
    public async Task FilterTeamOptions_Manager_OnlyIncludesManagedTeam()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var options = await Analytics(context, Now).GetFilterTeamOptionsAsync(world.Manager);

        Assert.Contains(options, o => o.TeamId == world.TeamId);
        Assert.DoesNotContain(options, o => o.TeamId == world.OtherTeamId);
    }

    [Fact] // Admin sees every team in their own organization, never a foreign organization's teams.
    public async Task FilterTeamOptions_Admin_NeverIncludesAnotherOrganizationsTeam()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var (_, foreignTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);

        var options = await Analytics(context, Now).GetFilterTeamOptionsAsync(world.Admin);

        Assert.Contains(options, o => o.TeamId == world.TeamId);
        Assert.DoesNotContain(options, o => o.TeamId == foreignTeamId);
    }

    // ---- Organization isolation (applies to every new method via the shared ApplyAnalyticsScope) ----

    [Fact] // Even a client-supplied team id that genuinely exists (just in a different organization)
           // must yield zero — never that foreign team's real data — because ApplyAnalyticsScope's
           // organization boundary is applied before the dashboard filter's team id ever narrows
           // anything (see AnalyticsQueryService.ApplyDashboardScope's own doc comment).
    public async Task VolumeTrend_TeamFilterNamingAnotherOrganizationsRealTeam_YieldsEmpty()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now); // world's own org has real data this period

        var (foreignOrgId, foreignTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var foreignCategoryId = await TicketTestData.AddCategoryAsync(context, foreignTeamId);
        var foreignUserId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, foreignTeamId, foreignUserId);
        var foreignRequester = TicketTestData.UserInOrganization(foreignOrgId, foreignUserId, UserRole.Agent, foreignTeamId);
        await ServiceAt(context, Now).CreateAsync(Request(foreignTeamId, foreignCategoryId), foreignRequester);

        // world.Admin belongs to world's own organization, not the foreign one — asking for the
        // foreign org's real, existing team id must never return that team's ticket.
        var filter = DashboardFilter.From(30, foreignTeamId, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Admin, filter);

        Assert.Equal(0, points.Sum(p => p.Count));
    }

    [Fact] // Positive counterpart: filtering to the caller's OWN team, in the presence of another
           // organization's data, returns exactly the caller's own ticket — proving the zero above
           // is the org boundary at work, not an unrelated empty-result bug.
    public async Task VolumeTrend_TeamFilterNamingOwnTeam_ReturnsOwnDataDespiteForeignOrgActivity()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await CreateTicketAsync(context, world, Now);

        var (foreignOrgId, foreignTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var foreignCategoryId = await TicketTestData.AddCategoryAsync(context, foreignTeamId);
        var foreignUserId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, foreignTeamId, foreignUserId);
        var foreignRequester = TicketTestData.UserInOrganization(foreignOrgId, foreignUserId, UserRole.Agent, foreignTeamId);
        await ServiceAt(context, Now).CreateAsync(Request(foreignTeamId, foreignCategoryId), foreignRequester);

        var filter = DashboardFilter.From(30, world.TeamId, null);
        var points = await Analytics(context, Now).GetTicketVolumeTrendAsync(world.Admin, filter);

        Assert.Equal(1, points.Sum(p => p.Count));
    }

    // ---- Query count / no N+1 ----

    [Fact]
    public async Task GetSlaBreakdownAsync_IssuesAtMostTwoQueries_RegardlessOfTicketCount()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedAsync(context);

        for (var i = 0; i < 10; i++)
        {
            await CreateTicketAsync(context, world, Now);
        }

        sql.Clear();
        var filter = DashboardFilter.From(30, null, null);
        var breakdown = await Analytics(context, Now).GetSlaBreakdownAsync(world.Manager, filter);
        var emitted = sql.ToString();

        var executed = CommandExecutedPattern().Matches(emitted).Count;
        Assert.True(executed <= 2, $"expected at most 2 queries, saw {executed}:{Environment.NewLine}{emitted}");
        Assert.Equal(10, breakdown.Total);
        Assert.DoesNotContain("AsEnumerable", emitted, StringComparison.OrdinalIgnoreCase);
    }
}
