using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using Microsoft.AspNetCore.Mvc;

namespace FlowOps.Web.Pages.Projects;

/// <summary>One sprint's ticket list (ADR-0030) — finished and unfinished — for a live sprint or a
/// frozen historical one. Tickets link to the existing Ticket Detail page; there is no second
/// ticket view. For a completed sprint, Admin/Manager can carry its unfinished tickets forward to the
/// current sprint — always an explicit action, never automatic.</summary>
public sealed class SprintDetailModel : ProjectPlanningPageModel
{
    private readonly ProjectPlanningQueryService _query;

    public SprintDetailModel(CurrentUserAccessor currentUserAccessor, TicketService ticketService, SprintService sprintService, ProjectPlanningQueryService query)
        : base(currentUserAccessor, ticketService, sprintService)
    {
        _query = query;
    }

    public SprintDetailView Detail { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, int sprintId, CancellationToken cancellationToken = default)
    {
        var user = await CurrentUsers.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var detail = await _query.GetSprintDetailAsync(user, id, sprintId, cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        Caller = user;
        CanManageSprints = PlanningAccessPolicy.CanManageSprints(user) && detail.Header.IsActive;
        return Page();
    }
}
