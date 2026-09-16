using FlowOps.Application.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Organizations;

public sealed class IndexModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformOrganizationService _platformOrganizationService;

    public IndexModel(PlatformUserAccessor platformUserAccessor, PlatformOrganizationService platformOrganizationService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformOrganizationService = platformOrganizationService;
    }

    public FlowOps.Application.Tickets.PagedResult<PlatformOrganizationListItem> Organizations { get; private set; } =
        new([], 1, PlatformOrganizationService.PageSize, 0);

    public string? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, string? search = null, CancellationToken cancellationToken = default)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        Search = search;
        Organizations = await _platformOrganizationService.ListOrganizationsAsync(pageNumber, search, cancellationToken);
        return Page();
    }
}
