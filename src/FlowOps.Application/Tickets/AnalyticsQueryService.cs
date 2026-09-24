using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// The Analytics module's public surface (CLAUDE.md §3.3: "Dashboard KPIs, workload, trends,
/// aging"). Phase 10 built exactly the four capped dashboard KPIs (§22) plus the current workload
/// distribution; Phase 20 (ADR-0019) adds the dashboard's further operational sections — ticket
/// volume trend, status breakdown, team workload, SLA breakdown, and resolution time by work
/// type — as additional methods on this same class, never a second analytics abstraction.
/// </summary>
/// <remarks>
/// Every query aggregates in SQL — <c>Count</c>/<c>Average</c>/<c>GroupBy</c> translated by EF
/// Core, never <c>AsEnumerable()</c> before aggregating — with the one deliberate exception of
/// <see cref="GetSlaBreakdownAsync"/>, which materialises each in-scope ticket's raw SLA columns
/// (bounded by the same organization/role/filter scope as everything else here, never "all
/// tickets ever") and classifies them with <see cref="SlaPolicy.GetStatus"/> in memory — the
/// single existing SLA-status authority, never re-derived independently (ADR-0019).
/// </remarks>
public sealed class AnalyticsQueryService
{
    /// <summary>
    /// The fixed reporting window for the two original Phase 10 KPIs (SLA Compliance, Average
    /// Resolution Time) — unaffected by the Phase 20 dashboard filter's <see cref="DashboardFilter.RangeDays"/>,
    /// which governs only the newer, explicitly time-windowed sections below. Changing an
    /// already-documented KPI's own window when an unrelated filter changes would be exactly the
    /// "invent a conflicting KPI definition" Phase 20 was told not to do.
    /// </summary>
    public const int ReportingWindowDays = 90;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public AnalyticsQueryService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The dashboard's four KPIs plus the current workload distribution, all scoped to the
    /// caller's <see cref="AnalyticsScope"/> (AUTH-RULE-02 "Team analytics" row) — never the
    /// ordinary ticket-view scope, so an Agent's numbers can never disclose their team's data.
    /// </summary>
    /// <param name="filter">Phase 20: when supplied, <see cref="DashboardFilter.TeamId"/>/
    /// <see cref="DashboardFilter.WorkType"/> narrow all four KPIs and the workload table
    /// consistently with the rest of the dashboard. <see cref="DashboardFilter.RangeDays"/> is
    /// deliberately NOT applied here — see <see cref="ReportingWindowDays"/>.</param>
    public async Task<DashboardSummary> GetDashboardSummaryAsync(CurrentUser user, DashboardFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var windowStart = now.AddDays(-ReportingWindowDays);

        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false);

        // Query 1: Open Work.
        var openWorkCount = await scoped.CountAsync(
            t => t.Status != Status.Resolved && t.Status != Status.Closed,
            cancellationToken);

        // Query 2: Overdue. DueDate, never SlaDueAt — this is not SLA breach.
        var overdueCount = await scoped.CountAsync(
            t => t.DueDate != null && t.DueDate < now && t.Status != Status.Resolved && t.Status != Status.Closed,
            cancellationToken);

        // The population both remaining historical KPIs share: resolved within the window.
        // ResolvedAt and SlaMet are set together (Ticket.Resolve) and cleared together
        // (Ticket.Reopen) — never independently — so "resolved in window" and "SlaMet non-null in
        // window" are exactly the same set of tickets. One filter serves both queries below.
        var resolvedInWindow = scoped.Where(t => t.ResolvedAt != null && t.ResolvedAt >= windowStart && t.ResolvedAt <= now);

        // Query 3: SLA Compliance — both the met-count and the qualifying-count in one grouped
        // query, aggregating the persisted SlaMet fact (SLA-RULE-08) rather than re-deriving it.
        var slaGroups = await resolvedInWindow
            .GroupBy(t => t.SlaMet)
            .Select(g => new { SlaMet = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var slaMetCount = slaGroups.FirstOrDefault(g => g.SlaMet == true)?.Count ?? 0;
        var slaQualifyingCount = slaGroups.Sum(g => g.Count);

        // Query 4: Average Resolution Time, ResolvedAt - SlaStartedAt (not CreatedAt — see
        // ResolutionTimeSummary's doc comment). The nullable-double Average overload returns null
        // for an empty sequence instead of throwing, so a zero-resolved-tickets window is handled
        // honestly without a separate existence check.
        var averageResolutionMinutes = await resolvedInWindow
            .Select(t => (double?)(t.ResolvedAt!.Value - t.SlaStartedAt).TotalMinutes)
            .AverageAsync(cancellationToken);

        // Query 5: current workload distribution — open tickets grouped by (team, assignee),
        // joined to names in the same statement. "Current" (not historical), a plain count (not
        // the optional ATTN-RULE-07 score), grouped in PostgreSQL rather than in memory.
        // Ordering happens on the flat, ungrouped shape below — EF Core cannot translate an
        // OrderBy expressed against a member of an already-constructed WorkloadItem record, only
        // against plain column/anonymous-type access, so the record itself is projected last,
        // immediately before materialisation.
        var workload = await scoped
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed)
            .GroupBy(t => new { t.TeamId, t.AssigneeId })
            .Select(g => new { g.Key.TeamId, g.Key.AssigneeId, Count = g.Count() })
            .Join(_dbContext.Teams, g => g.TeamId, team => team.Id, (g, team) => new { g.AssigneeId, g.Count, TeamId = team.Id, TeamName = team.Name })
            .GroupJoin(_dbContext.Users, g => g.AssigneeId, u => (Guid?)u.Id, (g, users) => new { g, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, user) => new
            {
                x.g.TeamId,
                x.g.TeamName,
                x.g.AssigneeId,
                AssigneeDisplayName = user == null ? null : user.DisplayName,
                x.g.Count,
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.TeamId)
            .ThenBy(x => x.AssigneeId)
            .Select(x => new WorkloadItem(x.TeamId, x.TeamName, x.AssigneeId, x.AssigneeDisplayName, x.Count))
            .ToListAsync(cancellationToken);

        return new DashboardSummary(
            openWorkCount,
            overdueCount,
            new SlaComplianceSummary(slaMetCount, slaQualifyingCount),
            new ResolutionTimeSummary(
                averageResolutionMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                slaQualifyingCount),
            workload);
    }

    /// <summary>
    /// Phase 20 §3: ticket count per period. Grouped server-side by calendar day
    /// (<c>t.CreatedAt.Date</c> — a plain EF Core translation, not a provider-specific function;
    /// Application stays free of an Npgsql-specific package reference per ADR-0002's layering)
    /// — never by loading tickets and grouping in C#. The aggregate query returns at most one row
    /// per day in the selected range (≤180), so bucketing those day-rows into weeks for the
    /// longer ranges is a cheap in-memory pass over already-aggregated counts, not raw tickets.
    /// Population: tickets whose <c>CreatedAt</c> falls in the selected range (Step 7's "is demand
    /// increasing/decreasing/stable" is a question about when work arrives). Granularity: daily
    /// for a 30-day range, weekly otherwise (Step 3's own suggestion) — every period in the range
    /// is present, including zero-count ones, so the line is never discontinuous.
    /// </summary>
    public async Task<IReadOnlyList<DashboardTrendPoint>> GetTicketVolumeTrendAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = now.AddDays(-filter.RangeDays);
        var weekly = filter.RangeDays > 30;

        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.CreatedAt >= start && t.CreatedAt <= now);

        var rows = await scoped
            .GroupBy(t => t.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var countsByDay = rows.ToDictionary(r => DateOnly.FromDateTime(r.Day), r => r.Count);

        var firstDay = DateOnly.FromDateTime(start.UtcDateTime.Date);
        var lastDay = DateOnly.FromDateTime(now.UtcDateTime.Date);

        if (!weekly)
        {
            var dailyPoints = new List<DashboardTrendPoint>();
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                dailyPoints.Add(new DashboardTrendPoint(day, countsByDay.GetValueOrDefault(day)));
            }

            return dailyPoints;
        }

        // Weekly buckets start on the same weekday as the range's own first day, so the final
        // (possibly partial) bucket lands at the end rather than requiring calendar-week alignment.
        var weeklyPoints = new List<DashboardTrendPoint>();
        for (var weekStart = firstDay; weekStart <= lastDay; weekStart = weekStart.AddDays(7))
        {
            var weekEnd = weekStart.AddDays(6) < lastDay ? weekStart.AddDays(6) : lastDay;
            var count = 0;
            for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
            {
                count += countsByDay.GetValueOrDefault(day);
            }

            weeklyPoints.Add(new DashboardTrendPoint(weekStart, count));
        }

        return weeklyPoints;
    }

    /// <summary>Phase 20 §4: current status of every ticket created within the selected range.
    /// Every <see cref="Status"/> appears, in the workflow's own declaration order, including zero
    /// counts — never sorted by count, since the point is to show where in the workflow work
    /// currently sits, not to rank statuses.</summary>
    public async Task<IReadOnlyList<DashboardStatusCount>> GetStatusBreakdownAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = now.AddDays(-filter.RangeDays);

        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.CreatedAt >= start && t.CreatedAt <= now);

        var rows = await scoped
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Enum.GetValues<Status>()
            .Select(status => new DashboardStatusCount(status, rows.FirstOrDefault(r => r.Status == status)?.Count ?? 0))
            .ToList();
    }

    /// <summary>
    /// Phase 20 §5: current open-ticket count per team — a present-tense snapshot, deliberately
    /// NOT scoped by <see cref="DashboardFilter.RangeDays"/> (a ticket created 200 days ago and
    /// still open today is still part of "who is carrying the work right now"); team/work-type
    /// filters still apply. Teams with zero current open tickets in scope are simply absent —
    /// unlike <see cref="GetStatusBreakdownAsync"/>'s closed status set, the set of teams is not
    /// closed/small enough to always enumerate, and Step 16/Step 6 both require that a team
    /// outside the caller's own scope never appear even with a zero count.
    /// </summary>
    public async Task<IReadOnlyList<DashboardTeamWorkload>> GetTeamWorkloadBreakdownAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed);

        return await scoped
            .GroupBy(t => t.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .Join(_dbContext.Teams, g => g.TeamId, team => team.Id, (g, team) => new { team.Id, team.Name, g.Count })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Id)
            .Select(x => new DashboardTeamWorkload(x.Id, x.Name, x.Count))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// ADR-0036: Team Workload's summary strip — Open/Unassigned/Overdue/High-Critical, all
    /// SQL-derivable counts over the caller's own <see cref="AnalyticsScope"/>, present-tense
    /// (never <see cref="DashboardFilter.RangeDays"/>-scoped, the same reasoning
    /// <see cref="GetTeamWorkloadBreakdownAsync"/>'s own doc comment gives). <see cref="TeamWorkloadSummary.AtRiskCount"/>
    /// always arrives here as <c>0</c> — the caller merges in <see cref="AttentionQueryService.GetAtRiskSummaryAsync"/>'s
    /// result, since <see cref="Attention.AttentionPolicy"/> alone decides what counts as at risk.
    /// </summary>
    public async Task<TeamWorkloadSummary> GetTeamWorkloadSummaryCountsAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed);

        var openCount = await scoped.CountAsync(cancellationToken);
        var unassignedCount = await scoped.CountAsync(t => t.AssigneeId == null, cancellationToken);
        var overdueCount = await scoped.CountAsync(t => t.DueDate != null && t.DueDate < now, cancellationToken);
        var highCriticalCount = await scoped.CountAsync(t => t.Priority == Priority.High || t.Priority == Priority.Critical, cancellationToken);

        return new TeamWorkloadSummary(openCount, unassignedCount, 0, overdueCount, highCriticalCount);
    }

    /// <summary>
    /// ADR-0036: the Team Workload table — extends <see cref="GetTeamWorkloadBreakdownAsync"/>'s
    /// own (team, open-count) grouping with Unassigned/Overdue, computed as three separate grouped
    /// queries rather than one query with several conditional aggregates (matching this class's own
    /// existing style — see <see cref="GetDashboardSummaryAsync"/>'s numbered-query comments — over
    /// a less-proven single-query shape) and merged by team id in memory; the team count is small
    /// enough that this is a handful of round trips, not one per team. <see cref="TeamWorkloadRow.AtRiskCount"/>
    /// always arrives as <c>0</c>, merged in by the caller the same way <see cref="GetTeamWorkloadSummaryCountsAsync"/>'s
    /// own <c>AtRiskCount</c> is.
    /// </summary>
    public async Task<IReadOnlyList<TeamWorkloadRow>> GetTeamWorkloadTableAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed);

        var openByTeam = await scoped
            .GroupBy(t => t.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);

        var unassignedByTeam = await scoped
            .Where(t => t.AssigneeId == null)
            .GroupBy(t => t.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);

        var overdueByTeam = await scoped
            .Where(t => t.DueDate != null && t.DueDate < now)
            .GroupBy(t => t.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);

        var teamIds = openByTeam.Keys.ToArray();
        var teamNames = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return openByTeam
            .Select(kv =>
            {
                var open = kv.Value;
                var unassigned = unassignedByTeam.GetValueOrDefault(kv.Key);
                return new TeamWorkloadRow(
                    kv.Key,
                    teamNames.GetValueOrDefault(kv.Key, string.Empty),
                    open,
                    open - unassigned,
                    unassigned,
                    0,
                    overdueByTeam.GetValueOrDefault(kv.Key));
            })
            .OrderByDescending(r => r.OpenCount)
            .ThenBy(r => r.TeamId)
            .ToList();
    }

    /// <summary>
    /// ADR-0036: one team's member workload, computed only when that team is explicitly expanded —
    /// never eagerly for every team on the page's initial load. Authorized by
    /// <see cref="TicketAccessPolicy.CanViewTeamWorkload"/>, deliberately not
    /// <see cref="Directory.TeamService.GetTeamDetailAsync"/>'s Admin-only team-management gate —
    /// read-only workload visibility is not team-management authority. Driven from the team's own
    /// active roster (<c>TeamMembers</c>), so a member currently carrying zero open work still
    /// appears with an all-zero row — "who is NOT carrying work right now" is itself part of the
    /// answer this page exists to give. <see cref="TeamMemberWorkloadRow.AtRiskCount"/> always
    /// arrives as <c>0</c>, merged in by the caller from <see cref="AttentionQueryService.GetAtRiskCountsByAssigneeAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<TeamMemberWorkloadRow>> GetTeamMemberWorkloadAsync(CurrentUser user, int teamId, CancellationToken cancellationToken = default)
    {
        if (!TicketAccessPolicy.CanViewTeamWorkload(user, teamId))
        {
            throw new TicketAccessDeniedException("This team's workload is not available to you.");
        }

        var now = _timeProvider.GetUtcNow();

        var members = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Join(_dbContext.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        var openTickets = _dbContext.Tickets
            .AsNoTracking()
            .Where(t => t.TeamId == teamId && t.AssigneeId != null && t.Status != Status.Resolved && t.Status != Status.Closed);

        var openByAssignee = await openTickets
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { AssigneeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssigneeId, x => x.Count, cancellationToken);

        var inProgressByAssignee = await openTickets
            .Where(t => t.Status == Status.InProgress)
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { AssigneeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssigneeId, x => x.Count, cancellationToken);

        var pendingByAssignee = await openTickets
            .Where(t => t.Status == Status.Pending)
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { AssigneeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssigneeId, x => x.Count, cancellationToken);

        var overdueByAssignee = await openTickets
            .Where(t => t.DueDate != null && t.DueDate < now)
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { AssigneeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssigneeId, x => x.Count, cancellationToken);

        return members
            .Select(m => new TeamMemberWorkloadRow(
                m.Id,
                m.DisplayName,
                openByAssignee.GetValueOrDefault(m.Id),
                inProgressByAssignee.GetValueOrDefault(m.Id),
                pendingByAssignee.GetValueOrDefault(m.Id),
                0,
                overdueByAssignee.GetValueOrDefault(m.Id)))
            .OrderByDescending(r => r.OpenCount)
            .ThenBy(r => r.DisplayName)
            .ToList();
    }

    /// <summary>
    /// Phase 20 §6: every ticket created within the selected range, classified by
    /// <see cref="SlaPolicy.GetStatus"/> — the single existing SLA-status authority, never
    /// re-derived. Two queries (raw facts, then the tiny SLA configuration table — the same shape
    /// <see cref="TicketQueryService"/>/<see cref="AttentionQueryService"/> already use), then one
    /// in-memory classification pass over the already-scoped, already-filtered result set — not
    /// "all tickets ever."
    /// </summary>
    public async Task<DashboardSlaBreakdown> GetSlaBreakdownAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = now.AddDays(-filter.RangeDays);

        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.CreatedAt >= start && t.CreatedAt <= now);

        var facts = await scoped
            .Select(t => new
            {
                t.WorkType,
                t.Priority,
                t.Status,
                t.SlaMet,
                t.SlaStartedAt,
                t.SlaDueAt,
                t.SlaPausedMinutes,
                t.SlaTargetMinutes,
            })
            .ToListAsync(cancellationToken);

        var configurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);

        int met = 0, within = 0, paused = 0, atRisk = 0, breached = 0;
        foreach (var fact in facts)
        {
            var configuration = SlaPolicy.ResolveConfiguration(configurations, fact.WorkType, fact.Priority);
            var status = SlaPolicy.GetStatus(
                fact.Status, fact.SlaMet, now, fact.SlaStartedAt, fact.SlaDueAt,
                fact.SlaPausedMinutes, fact.SlaTargetMinutes, configuration.RiskThresholdPercent);

            switch (status)
            {
                case SlaStatus.Met: met++; break;
                case SlaStatus.Within: within++; break;
                case SlaStatus.Paused: paused++; break;
                case SlaStatus.AtRisk: atRisk++; break;
                case SlaStatus.Breached: breached++; break;
            }
        }

        return new DashboardSlaBreakdown(met, within, paused, atRisk, breached);
    }

    /// <summary>Phase 20 §7: mean <c>ResolvedAt − SlaStartedAt</c> (identical definition to the
    /// existing <see cref="ResolutionTimeSummary"/> KPI) for tickets resolved within the selected
    /// range, grouped by <see cref="WorkType"/>. All four work types appear, including one with no
    /// qualifying tickets. Population is by <c>ResolvedAt</c>, not <c>CreatedAt</c> — a ticket
    /// created long before the window but resolved inside it still counts — so
    /// <see cref="ApplyDashboardScope"/> is called with <c>includeDateRange: false</c> and this
    /// method applies its own date predicate against the column that actually matters here.</summary>
    public async Task<IReadOnlyList<DashboardWorkTypeResolution>> GetResolutionTimeByWorkTypeAsync(CurrentUser user, DashboardFilter filter, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = now.AddDays(-filter.RangeDays);

        var scoped = ApplyDashboardScope(ApplyAnalyticsScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user), filter, includeDateRange: false)
            .Where(t => t.ResolvedAt != null && t.ResolvedAt >= start && t.ResolvedAt <= now);

        var rows = await scoped
            .GroupBy(t => t.WorkType)
            .Select(g => new
            {
                WorkType = g.Key,
                AverageMinutes = g.Average(t => (double?)(t.ResolvedAt!.Value - t.SlaStartedAt).TotalMinutes),
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken);

        return Enum.GetValues<WorkType>()
            .Select(workType =>
            {
                var row = rows.FirstOrDefault(r => r.WorkType == workType);
                return new DashboardWorkTypeResolution(
                    workType,
                    row?.AverageMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                    row?.Count ?? 0);
            })
            .ToList();
    }

    /// <summary>
    /// Phase 20 §17: the dashboard filter bar's own team dropdown — only teams the caller's
    /// <see cref="AnalyticsScope"/> already admits, so the list itself can never disclose a team
    /// outside their authorization (Step 16/§16's anti-leak requirement extends to filter options,
    /// not just results).
    /// </summary>
    public async Task<IReadOnlyList<DashboardFilterTeamOption>> GetFilterTeamOptionsAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        var teamsQuery = _dbContext.Teams.AsNoTracking().Where(t => t.OrganizationId == user.OrganizationId);
        if (scope.Kind is AnalyticsScopeKind.ManagedTeams or AnalyticsScopeKind.MemberTeams)
        {
            var teamIds = scope.TeamIds.ToArray();
            teamsQuery = teamsQuery.Where(t => teamIds.Contains(t.Id));
        }
        else if (scope.Kind == AnalyticsScopeKind.OwnAssignedTicketsOnly)
        {
            // An Agent's analytics scope is "own assigned tickets" — no team-wide filter makes
            // sense to offer, since selecting one could never narrow anything further.
            return [];
        }

        return await teamsQuery
            .OrderBy(t => t.Name)
            .Select(t => new DashboardFilterTeamOption(t.Id, t.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// AUTH-RULE-02 "Team analytics" row, translated to SQL. Deliberately not
    /// <see cref="TicketQueryService"/>'s <c>ApplyViewScope</c> — see
    /// <see cref="TicketAccessPolicy.GetAnalyticsScope"/> for why Agent needs a narrower shape
    /// here than ordinary ticket viewing.
    /// </summary>
    private static IQueryable<Ticket> ApplyAnalyticsScope(FlowOpsDbContext dbContext, IQueryable<Ticket> tickets, CurrentUser user)
    {
        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        // Phase 16: applied unconditionally, before the role-based scope below — including for
        // AllTeams (Admin), which otherwise sees every organization's tickets in its analytics.
        var orgScoped = tickets.Where(t =>
            dbContext.Teams.Any(team => team.Id == t.TeamId && team.OrganizationId == user.OrganizationId));

        // Materialised to an array before the lambda (same convention as
        // TicketQueryService.ApplyViewScope) so the provider translates it to a single SQL array
        // membership test rather than an unpredictable set-type translation.
        var teamIds = scope.TeamIds.ToArray();

        return scope.Kind switch
        {
            AnalyticsScopeKind.AllTeams => orgScoped,
            AnalyticsScopeKind.ManagedTeams or AnalyticsScopeKind.MemberTeams =>
                orgScoped.Where(t => teamIds.Contains(t.TeamId)),
            AnalyticsScopeKind.OwnAssignedTicketsOnly => orgScoped.Where(t => t.AssigneeId == scope.AssigneeId),
            _ => throw new ArgumentOutOfRangeException(nameof(user)),
        };
    }

    /// <summary>
    /// Phase 20 (ADR-0019): applies the dashboard's own filter bar — team, work type, and
    /// (optionally) the created-date range — strictly ON TOP of <see cref="ApplyAnalyticsScope"/>,
    /// never in place of it. A <see cref="DashboardFilter.TeamId"/> the caller cannot actually see
    /// (wrong organization, or a real team outside their role scope) intersects with an
    /// already-narrowed query and simply yields zero rows — never a leak, never a distinguishable
    /// error (Step 6/16).
    /// </summary>
    /// <param name="includeDateRange">No default on purpose: every call site below has its own
    /// specific date-column semantics (CreatedAt, ResolvedAt, or none at all for a present-tense
    /// snapshot) — see each public method's own doc comment — so silently defaulting this one way
    /// would eventually apply the wrong filter to a method that meant something else.</param>
    private IQueryable<Ticket> ApplyDashboardScope(IQueryable<Ticket> tickets, DashboardFilter? filter, bool includeDateRange)
    {
        if (filter is null)
        {
            return tickets;
        }

        if (filter.TeamId is { } teamId)
        {
            tickets = tickets.Where(t => t.TeamId == teamId);
        }

        if (filter.WorkType is { } workType)
        {
            tickets = tickets.Where(t => t.WorkType == workType);
        }

        if (includeDateRange)
        {
            var now = _timeProvider.GetUtcNow();
            var start = now.AddDays(-filter.RangeDays);
            tickets = tickets.Where(t => t.CreatedAt >= start && t.CreatedAt <= now);
        }

        return tickets;
    }
}
