using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Catalog;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Admin.Projects;

/// <summary>
/// Project management — the smallest legitimate way for an organization to populate the Create
/// Ticket "Project (optional)" dropdown, which previously had no data source beyond
/// <c>DemoDataSeeder</c>. Admin-only (CLAUDE.md §6.1: "Manage... projects"), the same
/// <see cref="FlowOps.Domain.Catalog.CatalogAccessPolicy"/> gate <c>Admin/Index.cshtml.cs</c> already
/// uses for categories — never a second authorization mechanism, and never the stale Identity-role
/// check ADR-0021 removed.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly CatalogService _catalogService;

    public IndexModel(CurrentUserAccessor currentUserAccessor, CatalogService catalogService)
    {
        _currentUserAccessor = currentUserAccessor;
        _catalogService = catalogService;
    }

    [BindProperty]
    public CreateInputModel CreateInput { get; set; } = new();

    public IReadOnlyList<ProjectListItem> Projects { get; private set; } = [];

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which <c>.notice</c> tone <c>StatusMessage</c> renders in — <c>false</c>
    /// (the default) for an ordinary confirmation, <c>true</c> when it is actually
    /// <c>result.Error</c> from a failed mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
            return Page();
        }

        var result = await _catalogService.CreateProjectAsync(user, CreateInput.Name, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(nameof(CreateInput.Name), result.Error!);
            Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
            return Page();
        }

        StatusMessage = $"Project \"{CreateInput.Name.Trim()}\" created.";
        // No RedirectToPage: a redirect starts a brand-new request with a fresh PageModel instance,
        // and StatusMessage (a plain property, not TempData) would not survive it — the same lesson
        // Admin/Index.cshtml.cs and Admin/Teams/Details.cshtml.cs already apply.
        CreateInput = new CreateInputModel();
        Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(int projectId, string name, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _catalogService.RenameProjectAsync(user, projectId, name, cancellationToken);
            StatusMessage = result.Succeeded ? "Project renamed." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (ProjectAccessDeniedException)
        {
            // A tampered projectId (not one of this page's own rows) or a project from another
            // organization — both are 403s, never disclosed as a different error shape.
            return Forbid();
        }

        Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(int projectId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        try
        {
            var result = await _catalogService.DeactivateProjectAsync(user, projectId, cancellationToken);
            StatusMessage = result.Succeeded ? "Project deactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (ProjectAccessDeniedException)
        {
            return Forbid();
        }

        Projects = await _catalogService.GetProjectsAsync(user, cancellationToken);
        return Page();
    }

    public sealed class CreateInputModel
    {
        [Required]
        [StringLength(CatalogService.MaxNameLength, MinimumLength = 1, ErrorMessage = "Project name is required.")]
        [Display(Name = "Project name")]
        public string Name { get; set; } = string.Empty;
    }
}
