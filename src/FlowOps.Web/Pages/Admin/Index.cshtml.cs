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

    public IndexModel(CurrentUserAccessor currentUserAccessor, TeamService teamService, CatalogService catalogService)
    {
        _currentUserAccessor = currentUserAccessor;
        _teamService = teamService;
        _catalogService = catalogService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<TeamListItem> Teams { get; private set; } = [];

    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        Teams = await _teamService.GetTeamsAsync(user, cancellationToken);
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
            Teams = await _teamService.GetTeamsAsync(user, cancellationToken);
            return Page();
        }

        var teamResult = await _teamService.CreateAsync(user, Input.TeamName, cancellationToken);
        if (!teamResult.Succeeded)
        {
            ModelState.AddModelError(nameof(Input.TeamName), teamResult.Error!);
            Teams = await _teamService.GetTeamsAsync(user, cancellationToken);
            return Page();
        }

        // The team already exists at this point even if the category step below fails — a team
        // with no categories yet is a valid, unsurprising state (it simply offers nothing to file
        // a ticket against until a category is added), so there is nothing to roll back.
        var categoryResult = await _catalogService.CreateCategoryAsync(user, teamResult.TeamId!.Value, Input.CategoryName, Input.CategoryWorkType, cancellationToken);
        if (!categoryResult.Succeeded)
        {
            ModelState.AddModelError(nameof(Input.CategoryName), categoryResult.Error!);
            Teams = await _teamService.GetTeamsAsync(user, cancellationToken);
            return Page();
        }

        StatusMessage = $"Team \"{Input.TeamName}\" created with category \"{Input.CategoryName}\".";
        // No RedirectToPage here — a redirect starts a brand-new request with a fresh PageModel
        // instance, and StatusMessage (a plain property, not TempData) would not survive it, the
        // same lesson the Web-layer team-membership tests caught for Admin/Teams/Details.cshtml.cs.
        Input = new InputModel();
        Teams = await _teamService.GetTeamsAsync(user, cancellationToken);
        return Page();
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
