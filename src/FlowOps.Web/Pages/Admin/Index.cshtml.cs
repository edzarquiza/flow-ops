using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Catalog;
using FlowOps.Application.Directory;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Admin;

/// <summary>
/// AUTH-RULE-01's coarse Admin-only gate (§6.1: "Manage users/teams/categories/SLA is Admin-only"),
/// moved off the Phase 4 <c>AuthorizeFolder("/Admin", "AdminOnly")</c>/<c>RequireRole</c> mechanism
/// and onto the same <see cref="CurrentUserAccessor"/> + <see cref="CurrentUser.Role"/> check every
/// other role-gated page in this app already uses (ADR-0021). <see cref="CurrentUser.Role"/> is
/// re-resolved from <c>OrganizationMembership</c> on every request — never a stale claim — exactly
/// like <c>MembersModel</c>, <c>CreateModel</c>, and every other authorization decision in Phase 16+.
/// </summary>
/// <remarks>
/// First real content behind this gate: minimal team creation (a Team plus its first Category,
/// created together) — the smallest thing that makes the Workspace Setup checklist's "set up your
/// first team" step actually do something, rather than the placeholder text that used to be here.
/// Everything beyond team/category creation (renaming, deletion, SLA configuration, user
/// management) is still out of scope, exactly as before.
/// </remarks>
public sealed class IndexModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TeamService _teamService;
    private readonly CatalogService _catalogService;
    private readonly WorkspaceSetupService _workspaceSetupService;

    public IndexModel(CurrentUserAccessor currentUserAccessor, TeamService teamService, CatalogService catalogService, WorkspaceSetupService workspaceSetupService)
    {
        _currentUserAccessor = currentUserAccessor;
        _teamService = teamService;
        _catalogService = catalogService;
        _workspaceSetupService = workspaceSetupService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Phase 29C: a view choice (<c>?showInactive=true</c>), off by default. It only hides or shows
    /// rows the caller may already see; it changes no lifecycle state and no authorization.</summary>
    [BindProperty(SupportsGet = true, Name = "showInactive")]
    public bool ShowInactive { get; set; }

    public int HiddenInactiveCount { get; private set; }

    public IReadOnlyList<TeamListItem> Teams { get; private set; } = [];

    public string? StatusMessage { get; set; }

    /// <summary>ADR-0026: non-null only once this page's own step (a team exists) is done and
    /// another setup step still isn't — see <c>_SetupNextStep.cshtml</c>.</summary>
    public SetupNextStepViewModel? NextStep { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        Teams = ApplyInactiveFilter(await _teamService.GetTeamsAsync(user, cancellationToken));
        NextStep = await ResolveNextStepAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateTeamAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            Teams = ApplyInactiveFilter(await _teamService.GetTeamsAsync(user, cancellationToken));
            return Page();
        }

        var teamResult = await _teamService.CreateAsync(user, Input.TeamName, cancellationToken);
        if (!teamResult.Succeeded)
        {
            ModelState.AddModelError(nameof(Input.TeamName), teamResult.Error!);
            Teams = ApplyInactiveFilter(await _teamService.GetTeamsAsync(user, cancellationToken));
            return Page();
        }

        // The team already exists at this point even if the category step below fails — a team
        // with no categories yet is a valid, unsurprising state (it simply offers nothing to file
        // a ticket against until a category is added), so there is nothing to roll back.
        var categoryResult = await _catalogService.CreateCategoryAsync(user, teamResult.TeamId!.Value, Input.CategoryName, Input.CategoryWorkType, cancellationToken);
        if (!categoryResult.Succeeded)
        {
            ModelState.AddModelError(nameof(Input.CategoryName), categoryResult.Error!);
            Teams = ApplyInactiveFilter(await _teamService.GetTeamsAsync(user, cancellationToken));
            return Page();
        }

        StatusMessage = $"Team \"{Input.TeamName}\" created with category \"{Input.CategoryName}\".";
        // No RedirectToPage here — a redirect starts a brand-new request with a fresh PageModel
        // instance, and StatusMessage (a plain property, not TempData) would not survive it, the
        // same lesson the Web-layer team-membership tests caught for Admin/Teams/Details.cshtml.cs.
        Input = new InputModel();
        Teams = ApplyInactiveFilter(await _teamService.GetTeamsAsync(user, cancellationToken));
        NextStep = await ResolveNextStepAsync(user, cancellationToken);
        return Page();
    }

    /// <summary>ADR-0026: only for an Admin, and only once "team" is this org's own state, not a
    /// still-outstanding step — matches <c>_SetupNextStep.cshtml</c>'s "already done, here's
    /// what's next" purpose rather than nagging about the very step this page itself is for.</summary>
    private async Task<SetupNextStepViewModel?> ResolveNextStepAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var status = await _workspaceSetupService.GetWorkspaceSetupStatusAsync(user, cancellationToken);
        if (!SetupSteps.IsStepDone("team", status))
        {
            return null;
        }

        var next = SetupSteps.FirstIncomplete(status);
        return next is null ? null : new SetupNextStepViewModel(next);
    }

    private IReadOnlyList<TeamListItem> ApplyInactiveFilter(IReadOnlyList<TeamListItem> all)
    {
        HiddenInactiveCount = ShowInactive ? 0 : all.Count(x => !x.IsActive);
        return ShowInactive ? all : all.Where(x => x.IsActive).ToList();
    }

    public sealed class InputModel
    {
        [Required]
        [StringLength(TeamService.MaxNameLength, MinimumLength = 1, ErrorMessage = "Team name is required.")]
        [Display(Name = "Team name")]
        public string TeamName { get; set; } = string.Empty;

        [Required]
        [StringLength(CatalogService.MaxNameLength, MinimumLength = 1, ErrorMessage = "Category name is required.")]
        [Display(Name = "Category name")]
        public string CategoryName { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Work type")]
        public WorkType CategoryWorkType { get; set; } = WorkType.Incident;
    }
}