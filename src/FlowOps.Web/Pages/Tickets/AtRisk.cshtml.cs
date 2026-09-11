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

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        Queue = await _attentionQueryService.GetAtRiskAsync(user, pageNumber, cancellationToken);
        return Page();
    }
}
