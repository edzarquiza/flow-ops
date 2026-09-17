using System.ComponentModel.DataAnnotations;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// ADR-0027: the receiving end of an Admin-generated password-reset link
/// (<c>Organization/Members.cshtml.cs</c>'s <c>OnPostGenerateResetLinkAsync</c>) — the same
/// "generate a link, someone else copies and sends it, this page is the anonymous landing spot"
/// shape <see cref="AcceptInvitationModel"/> already established for invitations.
/// No email capability exists anywhere in this app; this page never assumes one.
/// </summary>
[AllowAnonymous]
public sealed class ResetPasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;

    public ResetPasswordModel(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [BindProperty(SupportsGet = true)]
    public string UserId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Succeeded { get; private set; }

    public void OnGet()
    {
    }

    /// <summary>
    /// One generic failure message for every rejection — no such user, a malformed/expired/
    /// already-used token, or a weak password all land the same way. Distinguishing them would
    /// let a caller probe for which user ids exist, the same account-enumeration concern
    /// <see cref="LoginModel"/>'s own remarks already document for sign-in failures.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.FindByIdAsync(UserId);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "This reset link is no longer valid. Ask your organization admin for a new one.");
            return Page();
        }

        var result = await _userManager.ResetPasswordAsync(user, Token, Input.NewPassword);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "This reset link is no longer valid. Ask your organization admin for a new one.");
            return Page();
        }

        Succeeded = true;
        return Page();
    }

    public sealed class InputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "Password and confirmation do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
