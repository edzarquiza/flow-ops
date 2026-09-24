using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Platform;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages;

/// <summary>
/// The operations dashboard — "what needs my attention right now" (CLAUDE.md §1/§22), evolved
/// from the Phase 4 authenticated-landing page rather than a new route. Thin by contract: resolve
/// the caller, bind the filter bar, call the existing query services, render what they return.
/// Every scoping/ranking/aggregation decision belongs to <see cref="AnalyticsQueryService"/> and
/// <see cref="FlowOps.Domain.Attention.AttentionPolicy"/> (via <see cref="AttentionQueryService"/>),
/// never re-decided here.
/// </summary>
/// <remarks>
/// Phase 11: the page also calls <see cref="AttentionQueryService.GetAtRiskAsync"/> — the same
/// existing, already-measured call the At-Risk page itself makes (page 1, <c>PageSize</c> 25) —
/// and shows only the first few rows as a preview.
/// </remarks>
/// <remarks>
/// Phase 20 (ADR-0019): a compact filter bar (date range / team / work type) is bound directly
/// from the query string via <c>[BindProperty(SupportsGet = true)]</c> — the same convention
/// already used elsewhere (e.g. <c>AcceptInvitationModel.Token</c>) — so a filtered view is a
/// plain, bookmarkable/shareable GET, no JavaScript required. The bound values are never trusted
/// directly: <see cref="DashboardFilter.From"/> coerces an invalid/malformed range to the default,
/// and <see cref="SelectedTeamId"/> is only honoured if it also appears in the caller's own
/// <see cref="AnalyticsQueryService.GetFilterTeamOptionsAsync"/> result — never a bare pass-through
/// of client input into an authorization-relevant query.
/// </remarks>
public sealed class IndexModel : PageModel
{
    /// <summary>How many at-risk tickets the dashboard previews before linking to the full queue.</summary>
    public const int AtRiskPreviewCount = 3;

    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly AnalyticsQueryService _analyticsQueryService;
    private readonly AttentionQueryService _attentionQueryService;
    private readonly WorkspaceSetupService _workspaceSetupService;
    private readonly AccountService _accountService;
    private readonly DemoOptions _demoOptions;

    public IndexModel(
        CurrentUserAccessor currentUserAccessor,
        PlatformUserAccessor platformUserAccessor,
        AnalyticsQueryService analyticsQueryService,
        AttentionQueryService attentionQueryService,
        WorkspaceSetupService workspaceSetupService,
        AccountService accountService,
        DemoOptions demoOptions)
    {
        _currentUserAccessor = currentUserAccessor;
        _platformUserAccessor = platformUserAccessor;
        _analyticsQueryService = analyticsQueryService;
        _attentionQueryService = attentionQueryService;
        _workspaceSetupService = workspaceSetupService;
        _accountService = accountService;
        _demoOptions = demoOptions;
    }

    public string Email { get; private set; } = string.Empty;

    public string Role { get; private set; } = "(none)";

    /// <summary>The header's own "context" line (Phase 20 §"Dashboard Information Hierarchy")
    /// needs the current organization's name, which <see cref="CurrentUser"/> itself never carries
    /// (ADR-0018/ADR-0008: role and identity are re-resolved, never cached) — resolved the same way
    /// <c>_Layout.cshtml</c>'s org switcher already does, via the caller's own membership list.</summary>
    public string OrganizationName { get; private set; } = string.Empty;

    /// <summary>Query-string filter state (Phase 20). Bound directly so a link/bookmark/back-button
    /// reproduces the exact same filtered view — never persisted server-side per user.</summary>
    [BindProperty(SupportsGet = true, Name = "range")]
    public int? SelectedRangeDays { get; set; }

    [BindProperty(SupportsGet = true, Name = "team")]
    public int? SelectedTeamId { get; set; }

    [BindProperty(SupportsGet = true, Name = "type")]
    public WorkType? SelectedWorkType { get; set; }

    public DashboardFilter Filter { get; private set; } = DashboardFilter.Default;

    public IReadOnlyList<DashboardFilterTeamOption> TeamOptions { get; private set; } = [];

    public DashboardSummary Summary { get; private set; } = new(0, 0, new SlaComplianceSummary(0, 0), new ResolutionTimeSummary(null, 0), []);

    /// <summary>The top <see cref="AtRiskPreviewCount"/> ranked at-risk tickets, and the true total
    /// so the page can say "and N more" without a second query — both already returned by the one
    /// call to <see cref="AttentionQueryService.GetAtRiskAsync"/>.</summary>
    public IReadOnlyList<AttentionListItem> AtRiskPreview { get; private set; } = [];

    public int AtRiskTotalCount { get; private set; }

    public IReadOnlyList<DashboardTrendPoint> VolumeTrend { get; private set; } = [];

    public IReadOnlyList<DashboardStatusCount> StatusBreakdown { get; private set; } = [];

    public IReadOnlyList<DashboardTeamWorkload> TeamWorkload { get; private set; } = [];

    public DashboardSlaBreakdown SlaBreakdown { get; private set; } = new(0, 0, 0, 0, 0);

    public IReadOnlyList<DashboardWorkTypeResolution> ResolutionByWorkType { get; private set; } = [];

    /// <summary>ADR-0020: non-null only for an Admin of a real (non-demo) organization that still
    /// has an incomplete first-run checklist item — the view renders nothing at all when this is
    /// null, which is also how a fully-configured workspace's panel disappears with no completion
    /// message (§3/§9 of the onboarding spec: the workspace itself is the source of truth, so
    /// there is nothing to "complete" once every item is already true).</summary>
    public WorkspaceSetupStatus? SetupStatus { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            // A caller with no organization membership at all has no tenant dashboard to show —
            // but if they hold platform authority, land them on /Platform (where login naturally
            // sends them) instead of a bare Access Denied, since that is a real, intended
            // destination for a platform-only administrator rather than an error state.
            var platformAdmin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
            return platformAdmin is not null ? RedirectToPage("/Platform/Index") : Forbid();
        }

        Email = User.Identity?.Name ?? string.Empty;
        Role = user.Role.ToString();

        // Guide first-time discovery cue: Dashboard is where sign-in (registration-approval,
        // invitation acceptance, or an ordinary login) always lands, so it is the one place that
        // genuinely represents "reaches FlowOps for the first time" — see
        // AccountService.TryConsumeFirstGuideCueAsync's own doc comment for the atomic one-shot
        // guarantee. ViewData carries it to _Layout.cshtml, the only place the cue itself renders
        // (anchored to the sidebar's Guide link, which _Layout owns).
        ViewData["ShowGuideCue"] = await _accountService.TryConsumeFirstGuideCueAsync(user.UserId, cancellationToken);

        var availableOrganizations = await _currentUserAccessor.GetAvailableOrganizationsAsync(user.UserId, cancellationToken);
        OrganizationName = availableOrganizations.FirstOrDefault(o => o.Id == user.OrganizationId)?.Name ?? string.Empty;

        // ADR-0020: the demo organization is excluded up front, by name, at zero query cost — it
        // is always fully seeded (teams, members, hundreds of tickets), so it would fail every
        // checklist item and never render the panel regardless, but a demo deployment is also the
        // most-trafficked path, so skipping the three existence checks entirely here (rather than
        // running them and discarding the result) avoids paying for a query nobody will ever see
        // the answer to. Only an Admin can act on any setup item, so the check is skipped for
        // everyone else too.
        if (CanActOnSetup(user))
        {
            var setupStatus = await _workspaceSetupService.GetWorkspaceSetupStatusAsync(user, cancellationToken);
            SetupStatus = setupStatus.IsComplete ? null : setupStatus;
        }

        TeamOptions = await _analyticsQueryService.GetFilterTeamOptionsAsync(user, cancellationToken);

        // A team id that is not one of the caller's own options is silently dropped rather than
        // passed through — it can never narrow anything (ApplyDashboardScope would simply return
        // zero rows for it), and dropping it here keeps the rendered filter bar itself honest
        // about what is actually selected.
        var validatedTeamId = SelectedTeamId is { } teamId && TeamOptions.Any(t => t.TeamId == teamId)
            ? teamId
            : (int?)null;

        Filter = DashboardFilter.From(SelectedRangeDays, validatedTeamId, SelectedWorkType);
        SelectedRangeDays = Filter.RangeDays;
        SelectedTeamId = Filter.TeamId;
        SelectedWorkType = Filter.WorkType;

        // A small, fixed number of independent queries, run sequentially — the same discipline
        // AnalyticsQueryService itself already follows (CLAUDE.md §16).
        var atRisk = await _attentionQueryService.GetAtRiskAsync(user, pageNumber: 1, cancellationToken: cancellationToken);
        AtRiskPreview = atRisk.Items.Take(AtRiskPreviewCount).ToList();
        AtRiskTotalCount = atRisk.TotalCount;

        Summary = await _analyticsQueryService.GetDashboardSummaryAsync(user, Filter, cancellationToken);
        VolumeTrend = await _analyticsQueryService.GetTicketVolumeTrendAsync(user, Filter, cancellationToken);
        StatusBreakdown = await _analyticsQueryService.GetStatusBreakdownAsync(user, Filter, cancellationToken);
        TeamWorkload = await _analyticsQueryService.GetTeamWorkloadBreakdownAsync(user, Filter, cancellationToken);
        SlaBreakdown = await _analyticsQueryService.GetSlaBreakdownAsync(user, Filter, cancellationToken);
        ResolutionByWorkType = await _analyticsQueryService.GetResolutionTimeByWorkTypeAsync(user, Filter, cancellationToken);

        return Page();
    }

    /// <summary>ADR-0020/ADR-0026: only an Admin of a real (non-demo) organization can see or act
    /// on any setup item — the demo organization is always fully seeded and would fail/skip every
    /// checklist item on its own merits regardless.</summary>
    private bool CanActOnSetup(CurrentUser user) =>
        user.Role == UserRole.Admin && !(_demoOptions.Enabled && OrganizationName == DemoDataSeeder.OrganizationName);

    public async Task<IActionResult> OnPostSkipInviteAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        await _workspaceSetupService.SkipInviteStepAsync(user, cancellationToken);
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostSkipProjectAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        await _workspaceSetupService.SkipProjectStepAsync(user, cancellationToken);
        return RedirectToPage("/Index");
    }
}
