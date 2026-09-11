using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// The Analytics module's public surface (CLAUDE.md §3.3: "Dashboard KPIs, workload, trends,
/// aging"). Computes exactly the four capped dashboard KPIs (§22: "KPIs are limited to Open Work,
/// Overdue, SLA Compliance, Average Resolution Time") plus a current workload distribution —
/// nothing else. No historical trend, no weighted workload score (ATTN-RULE-07 stays optional and
/// unbuilt), no attention-signal history (none is persisted to aggregate).
/// </summary>
/// <remarks>
/// Every query aggregates in SQL — <c>Count</c>/<c>Average</c>/<c>GroupBy</c> translated by EF
/// Core, never <c>AsEnumerable()</c> before aggregating. Five independent queries run
/// sequentially against the one scoped <see cref="FlowOpsDbContext"/> (CLAUDE.md §16's ≤6 ceiling,
/// with headroom); a literal reading of "executed concurrently" would need
/// <c>IDbContextFactory</c>, a persistence pattern this project does not otherwise use, and
/// introducing it for a first dashboard was judged disproportionate (Phase 10 decision).
/// </remarks>
public sealed class AnalyticsQueryService
{
    /// <summary>
    /// The fixed reporting window for the two historical KPIs (Phase 10 decision — CLAUDE.md
    /// itself specifies no window; this project fills that gap with 90 days, matching
    /// <c>docs/database.md</c>'s own "resolution-trend and SLA-compliance reporting over a date
    /// range" rationale for <c>ix_tickets_resolved_at</c>). Shared with
    /// <see cref="TicketQueryService"/>'s <see cref="TicketQueueFilter.ResolvedRecently"/> filter
    /// so a KPI and the ticket list it links to always describe the exact same population.
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
    public async Task<DashboardSummary> GetDashboardSummaryAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var windowStart = now.AddDays(-ReportingWindowDays);

        var scoped = ApplyAnalyticsScope(_dbContext.Tickets.AsNoTracking(), user);

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
    /// AUTH-RULE-02 "Team analytics" row, translated to SQL. Deliberately not
    /// <see cref="TicketQueryService"/>'s <c>ApplyViewScope</c> — see
    /// <see cref="TicketAccessPolicy.GetAnalyticsScope"/> for why Agent needs a narrower shape
    /// here than ordinary ticket viewing.
    /// </summary>
    private static IQueryable<Ticket> ApplyAnalyticsScope(IQueryable<Ticket> tickets, CurrentUser user)
    {
        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        // Materialised to an array before the lambda (same convention as
        // TicketQueryService.ApplyViewScope) so the provider translates it to a single SQL array
        // membership test rather than an unpredictable set-type translation.
        var teamIds = scope.TeamIds.ToArray();

        return scope.Kind switch
        {
            AnalyticsScopeKind.AllTeams => tickets,
            AnalyticsScopeKind.ManagedTeams or AnalyticsScopeKind.MemberTeams =>
                tickets.Where(t => teamIds.Contains(t.TeamId)),
            AnalyticsScopeKind.OwnAssignedTicketsOnly => tickets.Where(t => t.AssigneeId == scope.AssigneeId),
            _ => throw new ArgumentOutOfRangeException(nameof(user)),
        };
    }
}
