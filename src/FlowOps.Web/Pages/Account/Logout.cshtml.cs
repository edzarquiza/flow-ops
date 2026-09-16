using FlowOps.Application.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly CurrentUserAccessor _currentUserAccessor;

    public LogoutModel(SignInManager<ApplicationUser> signInManager, CurrentUserAccessor currentUserAccessor)
    {
        _signInManager = signInManager;
        _currentUserAccessor = currentUserAccessor;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _signInManager.SignOutAsync();

        // Phase 19 (Step 9): the current-organization cookie is tied to the authenticated
        // session, not the browser — cleared here so a different account signing in on the same
        // browser can never inherit whichever organization this session last selected.
        _currentUserAccessor.ClearSelectedOrganization();

        return RedirectToPage("/Account/Login");
    }
}
