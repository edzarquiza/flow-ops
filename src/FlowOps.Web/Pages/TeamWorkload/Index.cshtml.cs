using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.TeamWorkload;

/// <summary>
/// ADR-0036: "Who is carrying the current work, and where is the operational pressure?" — reuses
/// the existing Team Analytics scope (AUTH-RULE-02) exactly as every other analytics view already
/// does: Admin sees every team, Manager sees managed teams, Viewer sees member teams read-only,
/// and Agent sees only their own workload (no team table at all — there is nothing for them to
/// drill into beyond what the summary already shows). Thin by contract (CLAUDE.md §11.1): bind
/// input, call the query services, hand the result to the view.
/// </summary>
/// <remarks>
/// Three query steps, matching the approved architecture: one summary aggregation, one team
/// aggregation, and — only when a team is explicitly expanded via <c>?expandedTeam=</c> — one
/// member aggregation. At-risk counts are a fourth, parallel step (never merged into the SQL
/// aggregations above), since <see cref="Domain.Attention.AttentionPolicy"/> alone decides what
/// counts as at risk; this page only merges its output into the same rows.
/// </remarks>
public sealed class IndexModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly AnalyticsQueryService _analyticsQueryService;
    private readonly AttentionQueryService _attentionQueryService;

    public IndexModel(CurrentUserAccessor currentUserAccessor, AnalyticsQueryService analyticsQueryService, AttentionQueryService attentionQueryService)
    {
        _currentUserAccessor = currentUserAccessor;
        _analyticsQueryService = analyticsQueryService;
        _attentionQueryService = attentionQueryService;
    }

    public TeamWorkloadSummary Summary { get; private set; } = new(0, 0, 0, 0, 0);

    /// <summary>Agent's Team Analytics scope is "own workload only" — no team table is offered at
    /// all, matching AUTH-RULE-02 exactly (never a team table with just one visible row standing in
    /// for a real team breakdown the role does not have).</summary>
    public bool IsOwnWorkloadOnly { get; private set; }

    public IReadOnlyList<TeamWorkloadRow> Teams { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "workType")]
    public WorkType? WorkTypeFilter { get; set; }

    /// <summary>Which team's member breakdown is currently shown, if any — a plain, bookmarkable
    /// GET the same way every other FlowOps filter already works, not a client-side toggle, since
    /// expanding a team means a real server fetch (member aggregation only when expanded).</summary>
    [BindProperty(SupportsGet = true, Name = "expandedTeam")]
    public int? ExpandedTeamId { get; set; }

    public string? ExpandedTeamName { get; private set; }

    public IReadOnlyList<TeamMemberWorkloadRow> ExpandedTeamMembers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        IsOwnWorkloadOnly = user.Role == UserRole.Agent;

        // Present-tense snapshot, like GetTeamWorkloadBreakdownAsync's own — RangeDays is ignored
        // by every method this page calls (includeDateRange: false throughout), so any value here
        // is inert; WorkType is the one filter this page's own summary/table both honour.
        var filter = new DashboardFilter(AnalyticsQueryService.ReportingWindowDays, null, WorkTypeFilter);

        var summaryCounts = await _analyticsQueryService.GetTeamWorkloadSummaryCountsAsync(user, filter, cancellationToken);
        var (atRiskTotal, atRiskByTeam) = await _attentionQueryService.GetAtRiskSummaryAsync(user, cancellationToken);
        Summary = summaryCounts with { AtRiskCount = atRiskTotal };

        if (IsOwnWorkloadOnly)
        {
            return Page();
        }

        var teamRows = await _analyticsQueryService.GetTeamWorkloadTableAsync(user, filter, cancellationToken);
        Teams = teamRows
            .Select(r => r with { AtRiskCount = atRiskByTeam.GetValueOrDefault(r.TeamId) })
            .ToList();

        if (ExpandedTeamId is { } teamId && Teams.Any(t => t.TeamId == teamId))
        {
            try
            {
                var members = await _analyticsQueryService.GetTeamMemberWorkloadAsync(user, teamId, cancellationToken);
                var atRiskByAssignee = await _attentionQueryService.GetAtRiskCountsByAssigneeAsync(user, teamId, cancellationToken);
                ExpandedTeamMembers = members
                    .Select(m => m with { AtRiskCount = atRiskByAssignee.GetValueOrDefault(m.UserId) })
                    .ToList();
                ExpandedTeamName = Teams.Single(t => t.TeamId == teamId).TeamName;
            }
            catch (TicketAccessDeniedException)
            {
                // The team row shown above already proves it's in scope (Teams is itself built from
                // GetTeamWorkloadTableAsync's own scoped query), so this only guards a crafted
                // ?expandedTeam= query value — never reachable through the page's own links.
                ExpandedTeamId = null;
            }
        }

        return Page();
    }
}
