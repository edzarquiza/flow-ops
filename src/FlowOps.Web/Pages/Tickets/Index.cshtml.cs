using FlowOps.Application.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Tickets;

/// <summary>
/// The paginated work queue. Thin by contract (CLAUDE.md §11.1): bind input, call the query
/// service, hand the result to the view. The team-visibility scope is applied inside the query
/// (AUTH-RULE-05) — this page performs no filtering of its own.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TicketQueryService _ticketQueryService;

    public IndexModel(CurrentUserAccessor currentUserAccessor, TicketQueryService ticketQueryService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketQueryService = ticketQueryService;
    }

    public PagedResult<TicketListItem> Queue { get; private set; } =
        new([], 1, TicketQueryService.PageSize, 0);

    public bool CanCreateTicket { get; private set; }

    /// <summary>Which Phase 10 KPI, if any, this page load is filtered to — echoed back so the
    /// view can show the active filter and a "clear filter" link.</summary>
    public TicketQueueFilter Filter { get; private set; }

    /// <summary>The active search term, echoed back so the view can show it in the search box and
    /// carry it into pagination links and the "clear filter" link.</summary>
    public string? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        int pageNumber = 1,
        TicketQueueFilter filter = TicketQueueFilter.None,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        // Drives whether the "New ticket" link is rendered. A UX affordance only — the real
        // decision is enforced in TicketService (CLAUDE.md §2 rule 8).
        CanCreateTicket = Domain.Tickets.TicketAccessPolicy.CanCreate(user);
        Filter = filter;
        Search = search;
        Queue = await _ticketQueryService.GetQueueAsync(user, pageNumber, filter, search, cancellationToken);
        return Page();
    }
}
