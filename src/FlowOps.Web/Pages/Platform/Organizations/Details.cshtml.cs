using FlowOps.Application.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Organizations;

public sealed class DetailsModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformOrganizationService _platformOrganizationService;

    public DetailsModel(PlatformUserAccessor platformUserAccessor, PlatformOrganizationService platformOrganizationService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformOrganizationService = platformOrganizationService;
    }

    public PlatformOrganizationDetail? Organization { get; private set; }

    public IReadOnlyList<PlatformPendingInvitation> PendingInvitations { get; private set; } = [];

    public IReadOnlyList<PlatformAuditEventListItem> RecentActivity { get; private set; } = [];

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which .notice tone StatusMessage renders in - false (the default)
    /// for an ordinary confirmation, true when it is actually result.Error from a failed
    /// mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        Organization = await _platformOrganizationService.GetOrganizationDetailAsync(id, cancellationToken);
        if (Organization is null)
        {
            return NotFound();
        }

        await LoadSupportingDataAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string name, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformOrganizationService.RenameOrganizationAsync(admin, id, name, cancellationToken);
            StatusMessage = result.Succeeded ? "Organization renamed." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostDeactivateAsync(int id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformOrganizationService.DeactivateOrganizationAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "Organization deactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostReactivateAsync(int id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _platformOrganizationService.ReactivateOrganizationAsync(admin, id, cancellationToken);
            StatusMessage = result.Succeeded ? "Organization reactivated." : result.Error;
            StatusIsError = !result.Succeeded;
        }
        catch (PlatformAccessDeniedException)
        {
            return NotFound();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    private async Task<IActionResult> ReloadAsync(int id, CancellationToken cancellationToken)
    {
        Organization = await _platformOrganizationService.GetOrganizationDetailAsync(id, cancellationToken);
        if (Organization is null)
        {
            return NotFound();
        }

        await LoadSupportingDataAsync(id, cancellationToken);
        return Page();
    }

    private async Task LoadSupportingDataAsync(int id, CancellationToken cancellationToken)
    {
        PendingInvitations = await _platformOrganizationService.GetPendingInvitationsAsync(id, cancellationToken);
        RecentActivity = await _platformOrganizationService.GetRecentAuditEventsAsync(id, cancellationToken: cancellationToken);
    }
}
