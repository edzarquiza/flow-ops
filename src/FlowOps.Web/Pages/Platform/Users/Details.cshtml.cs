using FlowOps.Application.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Users;

public sealed class DetailsModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformUserService _platformUserService;

    public DetailsModel(PlatformUserAccessor platformUserAccessor, PlatformUserService platformUserService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformUserService = platformUserService;
    }

    public PlatformUserDetail? TargetUser { get; private set; }

    public IReadOnlyList<PlatformAuditEventListItem> RecentActivity { get; private set; } = [];

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which .notice tone StatusMessage renders in - false (the default)
    /// for an ordinary confirmation, true when it is actually result.Error from a failed
    /// mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        TargetUser = await _platformUserService.GetUserDetailAsync(id, cancellationToken);
        if (TargetUser is null)
        {
            return NotFound();
        }

        RecentActivity = await _platformUserService.GetRecentAuditEventsAsync(id, cancellationToken: cancellationToken);
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

        return await ReloadAsync(id, cancellationToken);
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

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostDeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformUserService.DeactivateUserAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "User deactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostReactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformUserService.ReactivateUserAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "User reactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    private async Task<IActionResult> ReloadAsync(Guid id, CancellationToken cancellationToken)
    {
        TargetUser = await _platformUserService.GetUserDetailAsync(id, cancellationToken);
        if (TargetUser is null)
        {
            return NotFound();
        }

        RecentActivity = await _platformUserService.GetRecentAuditEventsAsync(id, cancellationToken: cancellationToken);
        return Page();
    }
}
