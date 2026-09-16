using FlowOps.Application.Platform;
using FlowOps.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Users;

/// <summary>Phase 24A (ADR-0024): the focused "these accounts cannot use FlowOps yet" workflow —
/// only Pending accounts, with an Approve action right on each row, separate from the general
/// (searchable, all-status) <see cref="IndexModel"/> list.</summary>
public sealed class PendingModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformUserService _platformUserService;

    public PendingModel(PlatformUserAccessor platformUserAccessor, PlatformUserService platformUserService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformUserService = platformUserService;
    }

    public FlowOps.Application.Tickets.PagedResult<PlatformUserListItem> PendingUsers { get; private set; } =
        new([], 1, PlatformUserService.PageSize, 0);

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which .notice tone StatusMessage renders in - false (the default)
    /// for an ordinary confirmation, true when it is actually result.Error from a failed
    /// mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        PendingUsers = await _platformUserService.ListUsersAsync(pageNumber, status: AccountStatus.Pending, cancellationToken: cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformUserService.ApproveUserAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "User approved." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        PendingUsers = await _platformUserService.ListUsersAsync(1, status: AccountStatus.Pending, cancellationToken: cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformUserService.RejectUserAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "User rejected." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        PendingUsers = await _platformUserService.ListUsersAsync(1, status: AccountStatus.Pending, cancellationToken: cancellationToken);
        return Page();
    }
}
