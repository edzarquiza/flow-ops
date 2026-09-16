using FlowOps.Application.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Organization;

/// <summary>
/// Phase 19 (ADR-0018/Step 5): the one, dedicated organization-switch operation. POST-only — a
/// switch changes server-side state, so it is never reachable via a plain link/GET. Always
/// redirects to a single fixed, local destination (Step 13) rather than accepting any
/// caller-supplied return path, which removes the open-redirect surface entirely rather than
/// merely restricting it.
/// </summary>
public sealed class SwitchModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly UserManager<ApplicationUser> _userManager;

    public SwitchModel(CurrentUserAccessor currentUserAccessor, UserManager<ApplicationUser> userManager)
    {
        _currentUserAccessor = currentUserAccessor;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnPostAsync(int organizationId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        // Deliberately ignores the boolean result: whether the switch succeeded (the caller is a
        // real member) or failed (they are not — or the id does not exist at all; the two are
        // indistinguishable by design, per Step 6/29's anti-enumeration requirement), the response
        // is identical — a redirect to the dashboard, which then simply reflects whichever
        // organization is actually current. Nothing here reveals which case occurred.
        await _currentUserAccessor.TrySwitchOrganizationAsync(user.Id, organizationId, cancellationToken);

        return LocalRedirect(Url.Content("~/"));
    }
}
