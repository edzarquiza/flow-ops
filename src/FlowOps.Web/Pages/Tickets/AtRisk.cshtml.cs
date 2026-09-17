using FlowOps.Application.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Tickets;

/// <summary>
/// The at-risk queue — "what needs my attention right now, ranked" (CLAUDE.md §1). Thin by
/// contract: resolve the caller, call the query service, render the order it returns. The page
/// detects nothing and ranks nothing; <see cref="FlowOps.Domain.Attention.AttentionPolicy"/> owns
/// both, and the caller's team scope is applied inside the query (AUTH-RULE-05).
/// </summary>
/// <remarks>
/// Phase 25 §16: the date filter here is deliberately narrower than the Work Queue's — a single
/// quick-range selector with no field choice, always narrowing by SLA due date (see
/// <see cref="AttentionQueryService.GetAtRiskAsync"/>'s own doc comment for why). This is an
/// intelligence view, not a ticket-history report.
/// </remarks>
public sealed class AtRiskModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly AttentionQueryService _attentionQueryService;

    public AtRiskModel(CurrentUserAccessor currentUserAccessor, AttentionQueryService attentionQueryService)
    {
        _currentUserAccessor = currentUserAccessor;
        _attentionQueryService = attentionQueryService;
    }

    public PagedResult<AttentionListItem> Queue { get; private set; } =
        new([], 1, AttentionQueryService.PageSize, 0);

    /// <summary>The active search term, echoed back so the view can show it in the search box and
    /// carry it into pagination links and the "clear search" link.</summary>
    public string? Search { get; private set; }

    [BindProperty(SupportsGet = true, Name = "range")]
    public DateRangeOption DateRangeOption { get; set; } = DateRangeOption.AllTime;

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? To { get; set; }

    public DateRangeFilter? DateRange { get; private set; }

    public bool HasDateFilter => DateRangeOption != DateRangeOption.AllTime;

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, string? search = null, CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

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

        Queue = await _attentionQueryService.GetAtRiskAsync(user, pageNumber, search, DateRange, cancellationToken);
        return Page();
    }
}
