using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Catalog;
using FlowOps.Application.Directory;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Admin.Teams;

/// <summary>
/// Team management for one team — membership (ADR from the team-membership management phase) plus,
/// as of Phase 22 (ADR-0022), the team's own lifecycle (rename/deactivate) and its categories
/// (add/rename/deactivate). Admin-only (CLAUDE.md §6.1: "Manage... teams"), the same
/// <see cref="DirectoryAccessPolicy"/>/<see cref="CatalogAccessPolicy"/> gates <c>Admin/Index.cshtml.cs</c>
/// and <c>Admin/Projects/Index.cshtml.cs</c> already use — never a second authorization mechanism.
/// </summary>
public sealed class DetailsModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TeamService _teamService;
    private readonly CatalogService _catalogService;

    public DetailsModel(CurrentUserAccessor currentUserAccessor, TeamService teamService, CatalogService catalogService)
    {
        _currentUserAccessor = currentUserAccessor;
        _teamService = teamService;
        _catalogService = catalogService;
    }

    public TeamDetail? Team { get; private set; }

    public IReadOnlyList<EligibleMemberOption> EligibleMembers { get; private set; } = [];

    /// <summary>Phase 29C: a view choice (<c>?showInactive=true</c>), off by default. It only hides or shows
    /// rows the caller may already see; it changes no lifecycle state and no authorization.</summary>
    [BindProperty(SupportsGet = true, Name = "showInactive")]
    public bool ShowInactive { get; set; }

    public int HiddenInactiveCount { get; private set; }

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    [BindProperty]
    public CreateCategoryInputModel CreateCategoryInput { get; set; } = new();

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which <c>.notice</c> tone <c>StatusMessage</c> renders in — <c>false</c>
    /// (the default) for an ordinary confirmation, <c>true</c> when it is actually
    /// <c>result.Error</c> from a failed mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddMemberAsync(int id, Guid userId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.AddMemberAsync(user, id, userId, cancellationToken);
            StatusMessage = result.Succeeded ? "Member added to the team." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (TeamAccessDeniedException)
        {
            // A tampered userId (not one of this page's own "add member" options) or a team from
            // another organization — both are 403s, never disclosed as a different error shape.
            return Forbid();
        }

        // No RedirectToPage: that would start a brand-new request with a fresh PageModel
        // instance, and StatusMessage (a plain property, not TempData) would not survive it —
        // the same reload-then-Page() pattern Organization/Members.cshtml.cs already uses for
        // its own Invite/ChangeRole/RemoveMember handlers.
        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(int id, Guid userId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.RemoveMemberAsync(user, id, userId, cancellationToken);
            StatusMessage = result.Succeeded ? "Member removed from the team." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSetManagerAsync(int id, Guid userId, bool isTeamManager, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.SetTeamManagerAsync(user, id, userId, isTeamManager, cancellationToken);
            StatusMessage = result.Succeeded ? "Team manager updated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRenameTeamAsync(int id, string name, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.RenameAsync(user, id, name, cancellationToken);
            StatusMessage = result.Succeeded ? "Team renamed." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateTeamAsync(int id, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.DeactivateAsync(user, id, cancellationToken);
            StatusMessage = result.Succeeded ? "Team deactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (TeamAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddCategoryAsync(int id, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            if (!await LoadAsync(user, id, cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        try
        {
            var result = await _catalogService.CreateCategoryAsync(user, id, CreateCategoryInput.Name, CreateCategoryInput.DefaultWorkType, cancellationToken);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(nameof(CreateCategoryInput.Name), result.Error!);
            }
            else
            {
                StatusMessage = $"Category \"{CreateCategoryInput.Name.Trim()}\" added.";
                CreateCategoryInput = new CreateCategoryInputModel();
            }
        }
        catch (CategoryAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRenameCategoryAsync(int id, int categoryId, string name, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _catalogService.RenameCategoryAsync(user, categoryId, name, cancellationToken);
            StatusMessage = result.Succeeded ? "Category renamed." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (CategoryAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateCategoryAsync(int id, int categoryId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _catalogService.DeactivateCategoryAsync(user, categoryId, cancellationToken);
            StatusMessage = result.Succeeded ? "Category deactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (CategoryAccessDeniedException)
        {
            return Forbid();
        }

        if (!await LoadAsync(user, id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    /// <summary>Populates <see cref="Team"/>/<see cref="EligibleMembers"/>/<see cref="Categories"/>;
    /// returns <see langword="false"/> when the team does not exist or belongs to another
    /// organization (the caller then returns <see cref="NotFound"/> instead of rendering a page
    /// with a null team).</summary>
    private async Task<bool> LoadAsync(CurrentUser user, int id, CancellationToken cancellationToken)
    {
        Team = await _teamService.GetTeamDetailAsync(user, id, cancellationToken);
        if (Team is null)
        {
            return false;
        }

        EligibleMembers = await _teamService.GetEligibleMembersAsync(user, id, cancellationToken);
        var allCategories = await _catalogService.GetCategoriesForTeamAsync(user, id, cancellationToken);
        HiddenInactiveCount = ShowInactive ? 0 : allCategories.Count(c => !c.IsActive);
        Categories = ShowInactive ? allCategories : allCategories.Where(c => c.IsActive).ToList();
        return true;
    }

    public sealed class CreateCategoryInputModel
    {
        [Required]
        [StringLength(CatalogService.MaxNameLength, MinimumLength = 1, ErrorMessage = "Category name is required.")]
        [Display(Name = "Category name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Work type")]
        public WorkType DefaultWorkType { get; set; } = WorkType.Incident;
    }
}
