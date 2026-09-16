using FlowOps.Application.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Users;

public sealed class IndexModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformUserService _platformUserService;

    public IndexModel(PlatformUserAccessor platformUserAccessor, PlatformUserService platformUserService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformUserService = platformUserService;
    }

    public FlowOps.Application.Tickets.PagedResult<PlatformUserListItem> Users { get; private set; } =
        new([], 1, PlatformUserService.PageSize, 0);

    public PlatformUserStatusSummary Summary { get; private set; } = new(0, 0, 0, 0);

    public string? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, string? search = null, CancellationToken cancellationToken = default)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        Search = search;
        Users = await _platformUserService.ListUsersAsync(pageNumber, search, cancellationToken: cancellationToken);
        Summary = await _platformUserService.GetUserStatusSummaryAsync(cancellationToken);
        return Page();
    }
}
