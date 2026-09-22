using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Projects;

/// <summary>The Projects list (ADR-0029): every active project in the caller's organization with
/// its current sprint. Visible to every organization member; ticket counts follow ordinary ticket
/// visibility, so they never disclose a ticket the caller could not open.</summary>
public sealed class IndexModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly ProjectPlanningQueryService _query;

    public IndexModel(CurrentUserAccessor currentUserAccessor, ProjectPlanningQueryService query)
    {
        _currentUserAccessor = currentUserAccessor;
        _query = query;
    }

    public IReadOnlyList<ProjectListEntry> Projects { get; private set; } = [];

    public bool CanManageProjects { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        CanManageProjects = CatalogAccessPolicy.CanManageProjects(user);
        Projects = await _query.GetProjectsAsync(user, cancellationToken);
        return Page();
    }
}
