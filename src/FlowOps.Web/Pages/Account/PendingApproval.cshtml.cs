using FlowOps.Application.Accounts;
using FlowOps.Domain.Accounts;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// Phase 24A-Extension (ADR-0025): the post-registration waiting room. Reachable anonymously — the
/// caller who lands here was deliberately never signed in (Phase 24A) — and identifies "my own
/// just-registered account" solely through <see cref="PendingRegistrationCookie"/>, never through
/// any client-supplied id (spec §11/§12: this must never become a general user-status lookup).
/// </summary>
/// <remarks>
/// <see cref="OnGetStatusAsync"/> is the small JSON endpoint the page's own script polls every few
/// seconds — deliberately plain server-rendered Razor Pages + a status handler, not SignalR/
/// WebSockets/SSE (spec §2: no new real-time infrastructure). It returns only
/// <c>{ "status": "Pending" | "Active" | "Rejected" | "Unknown" }</c> — never an email, id, role, or
/// any other detail (spec §10).
/// </remarks>
[AllowAnonymous]
public sealed class PendingApprovalModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public PendingApprovalModel(UserManager<ApplicationUser> userManager, IDataProtectionProvider dataProtectionProvider)
    {
        _userManager = userManager;
        _dataProtectionProvider = dataProtectionProvider;
    }

    /// <summary>Null when no valid registration context exists (cookie missing/expired/tampered, or
    /// a direct visit with no prior registration) — the view renders a generic waiting state either
    /// way, never an error, since this page must stay safe to view without authentication.</summary>
    public AccountStatus? Status { get; private set; }

    public async Task OnGetAsync()
    {
        Status = await ResolveStatusAsync();
    }

    /// <summary>
    /// <c>GET /Account/PendingApproval?handler=Status</c> — polled client-side every few seconds.
    /// Read-only (no antiforgery needed, matching every other GET in this codebase); identifies the
    /// caller purely from <see cref="PendingRegistrationCookie"/>, so a caller can only ever learn
    /// the status of the one account their own browser registered, never another user's.
    /// </summary>
    public async Task<IActionResult> OnGetStatusAsync()
    {
        var status = await ResolveStatusAsync();
        return new JsonResult(new StatusResponse(status?.ToString() ?? "Unknown"));
    }

    private async Task<AccountStatus?> ResolveStatusAsync()
    {
        var userId = PendingRegistrationCookie.Read(HttpContext, _dataProtectionProvider);
        if (userId is null)
        {
            return null;
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        return user is null ? null : AccountStatusResolver.Resolve(user);
    }

    private sealed record StatusResponse(string Status);
}
