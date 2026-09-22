using FlowOps.Application.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Tickets;

/// <summary>
/// The paginated work queue. Thin by contract (CLAUDE.md §11.1): bind input, call the query
/// service, hand the result to the view. The team-visibility scope is applied inside the query
/// (AUTH-RULE-05) — this page performs no filtering of its own.
/// </summary>
/// <remarks>
/// Phase 25: the date filter (<see cref="DateField"/>/<see cref="DateRangeOption"/>/
/// <see cref="From"/>/<see cref="To"/>) is bound the same way the Dashboard's own filter bar
/// already is (<c>[BindProperty(SupportsGet = true)]</c>, ADR-0019's convention) — a plain,
/// bookmarkable GET, no JavaScript required. An invalid Custom range (From &gt; To) is rejected
/// with a validation message rather than silently swapped or silently ignored (§12).
/// </remarks>
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

    [BindProperty(SupportsGet = true, Name = "dateField")]
    public QueueDateField DateField { get; set; } = QueueDateField.Created;

    [BindProperty(SupportsGet = true, Name = "range")]
    public DateRangeOption DateRangeOption { get; set; } = DateRangeOption.AllTime;

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? To { get; set; }

    /// <summary>The date filter actually applied — <see langword="null"/> once an invalid Custom
    /// range has been rejected, so the query never silently applies a range the user did not
    /// validly request.</summary>
    public DateRangeFilter? DateRange { get; private set; }

    /// <summary>Phase 29B: the Work Queue opens on unfinished work; this asks for Resolved and Closed
    /// tickets as well. A KPI filter or a search already defines its own population, so both include
    /// finished tickets without needing the switch.</summary>
    [BindProperty(SupportsGet = true, Name = "showFinished")]
    public bool ShowFinished { get; set; }

    public bool IncludesFinished => ShowFinished || Filter != TicketQueueFilter.None || !string.IsNullOrWhiteSpace(Search);

    /// <summary>The "Show finished" switch is only meaningful when neither a KPI filter nor a search is active.</summary>
    public bool OffersFinishedSwitch => Filter == TicketQueueFilter.None && string.IsNullOrWhiteSpace(Search);

    public bool HasDateFilter => DateRangeOption != DateRangeOption.AllTime;

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

        var requestedRange = new DateRangeFilter(DateRangeOption, From, To);
        if (requestedRange.IsInvalidCustomRange)
        {
            ModelState.AddModelError(string.Empty, "The custom date range's start must be on or before its end.");
            DateRange = null;
        }
        else
        {
            DateRange = requestedRange;
        }

        Queue = await _ticketQueryService.GetQueueAsync(user, pageNumber, filter, search, DateField, DateRange, IncludesFinished, cancellationToken);
        return Page();
    }
}
