using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using Microsoft.AspNetCore.Mvc;

namespace FlowOps.Web.Pages.Projects;

/// <summary>The current-sprint board (ADR-0029). Only the active sprint's tickets appear; the
/// columns come from the existing status model plus the sprint-backlog flag. No drag-and-drop —
/// every move is an explicit menu action.</summary>
public sealed class BoardModel : ProjectPlanningPageModel
{
    private readonly ProjectPlanningQueryService _query;

    public BoardModel(CurrentUserAccessor currentUserAccessor, TicketService ticketService, SprintService sprintService, ProjectPlanningQueryService query)
        : base(currentUserAccessor, ticketService, sprintService)
    {
        _query = query;
    }

    public ProjectBoard Board { get; private set; } = null!;

    /// <summary>Sprints a card can be moved to from the board: the planned ones (a card already sits
    /// in the active sprint; completed sprints are never a destination).</summary>
    public IReadOnlyList<SprintSummary> MoveTargets => Board.PlannedSprints;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await CurrentUsers.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var board = await _query.GetBoardAsync(user, id, cancellationToken);
        if (board is null)
        {
            return NotFound();
        }

        Board = board;
        Caller = user;
        CanManageSprints = PlanningAccessPolicy.CanManageSprints(user) && board.Header.IsActive;
        return Page();
    }
}
