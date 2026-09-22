using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using Microsoft.AspNetCore.Mvc;

namespace FlowOps.Web.Pages.Projects;

/// <summary>Project Overview (ADR-0029): current sprint at a glance, every sprint, and — for
/// Admin/Manager — the create-sprint form. Restrained on purpose: not a second dashboard.</summary>
public sealed class DetailsModel : ProjectPlanningPageModel
{
    private readonly ProjectPlanningQueryService _query;

    public DetailsModel(CurrentUserAccessor currentUserAccessor, TicketService ticketService, SprintService sprintService, ProjectPlanningQueryService query)
        : base(currentUserAccessor, ticketService, sprintService)
    {
        _query = query;
    }

    public ProjectOverview Overview { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await CurrentUsers.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var overview = await _query.GetOverviewAsync(user, id, cancellationToken);
        if (overview is null)
        {
            return NotFound();
        }

        Overview = overview;
        Caller = user;
        CanManageSprints = PlanningAccessPolicy.CanManageSprints(user) && overview.Header.IsActive;
        return Page();
    }
}
