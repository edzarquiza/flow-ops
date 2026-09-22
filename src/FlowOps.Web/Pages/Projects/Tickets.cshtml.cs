using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace FlowOps.Web.Pages.Projects;

/// <summary>The project's ticket planning list (ADR-0029): every ticket the caller may see in this
/// project — sprint or not — paginated and filterable. All mutations reuse the shared handlers in
/// <see cref="ProjectPlanningPageModel"/>, which call the existing application services.</summary>
public sealed class TicketsModel : ProjectPlanningPageModel
{
    private readonly ProjectPlanningQueryService _query;

    public TicketsModel(CurrentUserAccessor currentUserAccessor, TicketService ticketService, SprintService sprintService, ProjectPlanningQueryService query)
        : base(currentUserAccessor, ticketService, sprintService)
    {
        _query = query;
    }

    /// <summary>Filters are plain query-string state (same convention as the Dashboard/Work Queue),
    /// never trusted: unknown enum text simply fails to bind and falls back to "no filter", and a
    /// foreign assignee id can only intersect an already-scoped query into zero rows.</summary>
    [BindProperty(SupportsGet = true, Name = "sprint")]
    public SprintScope SprintFilter { get; set; } = SprintScope.All;

    [BindProperty(SupportsGet = true, Name = "status")]
    public Status? StatusFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "priority")]
    public Priority? PriorityFilter { get; set; }

    /// <summary>Empty = anyone, <c>none</c> = unassigned, otherwise a user id.</summary>
    [BindProperty(SupportsGet = true, Name = "assignee")]
    public string? AssigneeFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "pageNumber")]
    public int PageNumber { get; set; } = 1;

    public ProjectTicketsView View { get; private set; } = null!;

    public bool HasFilter => SprintFilter != SprintScope.All || StatusFilter is not null || PriorityFilter is not null || !string.IsNullOrEmpty(AssigneeFilter);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await CurrentUsers.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var unassigned = string.Equals(AssigneeFilter, "none", StringComparison.OrdinalIgnoreCase);
        var assigneeId = !unassigned && Guid.TryParse(AssigneeFilter, out var parsed) ? parsed : (Guid?)null;

        var filter = new ProjectTicketFilter(SprintFilter, StatusFilter, PriorityFilter, assigneeId, unassigned);
        var view = await _query.GetTicketsAsync(user, id, PageNumber, filter, cancellationToken);
        if (view is null)
        {
            return NotFound();
        }

        View = view;
        Caller = user;
        CanManageSprints = PlanningAccessPolicy.CanManageSprints(user) && view.Header.IsActive;
        return Page();
    }
}
