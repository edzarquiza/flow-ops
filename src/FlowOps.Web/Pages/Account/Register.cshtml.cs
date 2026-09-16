using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// Phase 17: public registration. "Create Account → Create Organization → Creator becomes Admin" as
/// a single submission — see <see cref="AccountService.RegisterAsync"/> and ADR-0016 for the
/// consistency strategy across <c>ApplicationUser</c>/<c>Organization</c>/
/// <c>OrganizationMembership</c>. No email verification, no invitation flow (both explicitly out
/// of scope for this phase).
/// </summary>
/// <remarks>
/// Phase 24A (ADR-0024): registration no longer signs the caller in. The account it creates starts
/// Pending — genuinely unable to authenticate (see <c>AccountService.RegisterAsync</c>'s own
/// lockout) until a Platform Admin approves it — so establishing a session here would grant normal
/// application access before that approval exists, defeating the entire point of the gate.
/// </remarks>
/// <remarks>
/// Phase 24A-Extension (ADR-0025): writes the <see cref="PendingRegistrationCookie"/> so the
/// PendingApproval page the caller is redirected to can poll its own account's status without ever
/// being authenticated.
/// </remarks>
[AllowAnonymous]
public sealed class RegisterModel : PageModel
{
    private readonly AccountService _accountService;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public RegisterModel(AccountService accountService, IDataProtectionProvider dataProtectionProvider)
    {
        _accountService = accountService;
        _dataProtectionProvider = dataProtectionProvider;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _accountService.RegisterAsync(
            new RegisterRequest(Input.FullName, Input.Email, Input.Password, Input.OrganizationName),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return Page();
        }

        PendingRegistrationCookie.Write(HttpContext, _dataProtectionProvider, result.UserId!.Value);
        return RedirectToPage("/Account/PendingApproval");
    }

    public sealed class InputModel
    {
        [Required]
        [StringLength(200, MinimumLength = 1, ErrorMessage = "Full name is required.")]
        [Display(Name = "Full name")]
        public string FullName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "Password and confirmation do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required]
        [StringLength(200, MinimumLength = 1, ErrorMessage = "Organization name is required.")]
        [Display(Name = "Organization name")]
        public string OrganizationName { get; set; } = string.Empty;
    }
}
