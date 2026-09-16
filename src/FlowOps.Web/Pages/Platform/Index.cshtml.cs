using FlowOps.Application.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform;

/// <summary>
/// Phase 24B: Platform Administration's operational command center — an overview, pending
/// approvals, and bounded organization/user summaries, all visible without navigating anywhere
/// (the mega-spec's own "do not make the Platform Admin click through to see anything useful").
/// Still deliberately not a dashboard product (ADR-0023's non-goal): four counts, two short lists,
/// and one action, all through the existing <see cref="PlatformOrganizationService"/>/
/// <see cref="PlatformUserService"/> — nothing new is queried that those services didn't already
/// know how to answer. Gated by <see cref="PlatformUserAccessor"/> alone, never by
/// <c>CurrentUserAccessor</c>/<c>OrganizationMembership</c>.
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>How many rows each bounded homepage list shows before "View all →" — a summary,
    /// not the full paginated page (spec §18/§35: never claim to be "all" when it isn't).</summary>
    public const int ListSize = 8;

    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly PlatformOrganizationService _platformOrganizationService;
    private readonly PlatformUserService _platformUserService;

    public IndexModel(
        PlatformUserAccessor platformUserAccessor,
        PlatformOrganizationService platformOrganizationService,
        PlatformUserService platformUserService)
    {
        _platformUserAccessor = platformUserAccessor;
        _platformOrganizationService = platformOrganizationService;
        _platformUserService = platformUserService;
    }

    public string PlatformAdminName { get; private set; } = string.Empty;

    public PlatformOrganizationStatusSummary OrganizationSummary { get; private set; } = new(0, 0);

    public PlatformUserStatusSummary UserSummary { get; private set; } = new(0, 0, 0, 0);

    public PlatformTicketSummary TicketSummary { get; private set; } = new(0, 0);

    public IReadOnlyList<PlatformPendingAccountListItem> PendingAccounts { get; private set; } = [];

    public IReadOnlyList<PlatformOrganizationListItem> Organizations { get; private set; } = [];

    public IReadOnlyList<PlatformUserListItem> RecentUsers { get; private set; } = [];

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A: which .notice tone StatusMessage renders in - false (the default)
    /// for an ordinary confirmation, true when it is actually result.Error from a failed
    /// mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        PlatformAdminName = admin.DisplayName;
        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// Approve, right from the homepage's Pending Approvals section. The one mutation this page
    /// performs — through the exact same <see cref="PlatformUserService.ApproveUserAsync"/> every
    /// other approval entry point (<c>/Platform/Users/Details</c>, <c>/Platform/Users/Pending</c>)
    /// already uses; nothing here touches <c>ApplicationUser</c> directly or re-implements the
    /// lifecycle rule.
    /// </summary>
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

        PlatformAdminName = admin.DisplayName;
        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>Reject, right from the homepage — the same <see cref="PlatformUserService.RejectUserAsync"/>
    /// every other reject entry point uses. Legal only from Pending (ADR-0025); never a hard delete.</summary>
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

        PlatformAdminName = admin.DisplayName;
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        OrganizationSummary = await _platformOrganizationService.GetOrganizationStatusSummaryAsync(cancellationToken);
        UserSummary = await _platformUserService.GetUserStatusSummaryAsync(cancellationToken);
        TicketSummary = await _platformOrganizationService.GetTicketSummaryAsync(cancellationToken);
        PendingAccounts = await _platformUserService.GetPendingAccountsAsync(ListSize, cancellationToken);
        Organizations = await _platformOrganizationService.GetPriorityOrganizationsAsync(ListSize, cancellationToken);
        RecentUsers = await _platformUserService.GetRecentUsersAsync(ListSize, cancellationToken);
    }
}
