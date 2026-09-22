using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using Microsoft.AspNetCore.Mvc;

namespace FlowOps.Web.Pages.Projects;

/// <summary>The project's sprint history (ADR-0030): every sprint — planned, active, completed,
/// cancelled — with how much of it got done. Each row opens the sprint's own detail view.</summary>
public sealed class SprintsModel : ProjectPlanningPageModel
{
    private readonly ProjectPlanningQueryService _query;

    public SprintsModel(CurrentUserAccessor currentUserAccessor, TicketService ticketService, SprintService sprintService, ProjectPlanningQueryService query)
        : base(currentUserAccessor, ticketService, sprintService)
    {
        _query = query;
    }

    public SprintArchiveView Archive { get; private set; } = null!;

    public bool HasActiveSprint => Archive.Sprints.Any(s => s.Status == SprintStatus.Active);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await CurrentUsers.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var archive = await _query.GetSprintArchiveAsync(user, id, cancellationToken);
        if (archive is null)
        {
            return NotFound();
        }

        Archive = archive;
        Caller = user;
        CanManageSprints = PlanningAccessPolicy.CanManageSprints(user) && archive.Header.IsActive;
        return Page();
    }
}
