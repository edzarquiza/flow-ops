using FlowOps.Application.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages;

/// <summary>
/// The Phase 10/11 dashboard — "what needs my attention right now" (CLAUDE.md §1/§22), evolved
/// from the Phase 4 authenticated-landing page rather than a new route. Thin by contract: resolve
/// the caller, call the existing query services, render what they return. Exactly the four capped
/// KPIs (§22) plus the current workload distribution and an at-risk preview — every scoping/
/// ranking decision belongs to <see cref="AnalyticsQueryService"/> and
/// <see cref="FlowOps.Domain.Attention.AttentionPolicy"/> (via <see cref="AttentionQueryService"/>),
/// never re-decided here.
/// </summary>
/// <remarks>
/// Phase 11: the page now also calls <see cref="AttentionQueryService.GetAtRiskAsync"/> — the same
/// existing, already-measured call the At-Risk page itself makes (page 1, <c>PageSize</c> 25) —
/// and shows only the first few rows as a preview. No new query, no new ranking logic, and no
/// per-row Work Queue attention evaluation (that deferral, from Phase 8, is unaffected: this is
/// the dashboard calling the at-risk service once, not the Work Queue evaluating signals per row).
/// </remarks>
public sealed class IndexModel : PageModel
{
    /// <summary>How many at-risk tickets the dashboard previews before linking to the full queue.</summary>
    public const int AtRiskPreviewCount = 5;

    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly AnalyticsQueryService _analyticsQueryService;
    private readonly AttentionQueryService _attentionQueryService;

    public IndexModel(
        CurrentUserAccessor currentUserAccessor,
        AnalyticsQueryService analyticsQueryService,
        AttentionQueryService attentionQueryService)
    {
        _currentUserAccessor = currentUserAccessor;
        _analyticsQueryService = analyticsQueryService;
        _attentionQueryService = attentionQueryService;
    }

    public string Email { get; private set; } = string.Empty;

    public string Role { get; private set; } = "(none)";

    public DashboardSummary Summary { get; private set; } = new(0, 0, new SlaComplianceSummary(0, 0), new ResolutionTimeSummary(null, 0), []);

    /// <summary>The top <see cref="AtRiskPreviewCount"/> ranked at-risk tickets, and the true total
    /// so the page can say "and N more" without a second query — both already returned by the one
    /// call to <see cref="AttentionQueryService.GetAtRiskAsync"/>.</summary>
    public IReadOnlyList<AttentionListItem> AtRiskPreview { get; private set; } = [];

    public int AtRiskTotalCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        Email = User.Identity?.Name ?? string.Empty;
        Role = user.Role.ToString();

        // Two independent queries, run sequentially — the same "small, fixed number of queries"
        // discipline as AnalyticsQueryService itself (CLAUDE.md §16); see the Phase 11 report for
        // the measured total.
        var atRisk = await _attentionQueryService.GetAtRiskAsync(user, pageNumber: 1, cancellationToken);
        AtRiskPreview = atRisk.Items.Take(AtRiskPreviewCount).ToList();
        AtRiskTotalCount = atRisk.TotalCount;

        Summary = await _analyticsQueryService.GetDashboardSummaryAsync(user, cancellationToken);
        return Page();
    }
}
