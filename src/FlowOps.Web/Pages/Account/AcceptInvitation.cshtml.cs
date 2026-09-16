using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Organizations;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// Phase 18: the public invitation-acceptance page (Step 11/27). Reachable anonymously — an
/// unauthenticated visitor needs to see what the invitation offers before deciding whether to sign
/// in or create an account — but every actual acceptance handler still resolves the acting user
/// from the authenticated principal or from the account it itself just created, never from
/// anything the visitor typed.
/// </summary>
[AllowAnonymous]
public sealed class AcceptInvitationModel : PageModel
{
    private readonly InvitationService _invitationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AcceptInvitationModel(InvitationService invitationService, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _invitationService = invitationService;
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public NewAccountInputModel NewAccount { get; set; } = new();

    public InvitationDetails Details { get; private set; } = InvitationDetails.NotFound();

    /// <summary>True when the current visitor is signed in as an account whose email does not
    /// match the invitation — a display-only check (Step 12); the real, authoritative check
    /// happens server-side inside <see cref="InvitationService.AcceptForCurrentUserAsync"/>.</summary>
    public bool SignedInAsWrongAccount { get; private set; }

    public bool InvitedEmailAlreadyHasAccount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Details = await _invitationService.GetInvitationDetailsAsync(Token, cancellationToken);
        await LoadAcceptanceContextAsync();
        return Page();
    }

    /// <summary>The signed-in caller accepts with their own account.</summary>
    public async Task<IActionResult> OnPostAcceptAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        Details = await _invitationService.GetInvitationDetailsAsync(Token, cancellationToken);

        var result = await _invitationService.AcceptForCurrentUserAsync(Token, user.Id, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, DescribeFailure(result.Outcome));
            await LoadAcceptanceContextAsync();
            return Page();
        }

        return LocalRedirect(Url.Content("~/"));
    }

    /// <summary>An anonymous visitor whose email has no FlowOps account yet creates one and joins
    /// in a single step (Step 13) — the invited email and role are never taken from this form.</summary>
    public async Task<IActionResult> OnPostCreateAccountAsync(CancellationToken cancellationToken)
    {
        Details = await _invitationService.GetInvitationDetailsAsync(Token, cancellationToken);

        if (!ModelState.IsValid)
        {
            await LoadAcceptanceContextAsync();
            return Page();
        }

        var result = await _invitationService.AcceptForNewUserAsync(Token, NewAccount.FullName, NewAccount.Password, cancellationToken);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            if (result.Errors.Count == 0)
            {
                ModelState.AddModelError(string.Empty, DescribeFailure(result.Outcome));
            }

            await LoadAcceptanceContextAsync();
            return Page();
        }

        var user = await _userManager.FindByIdAsync(result.UserId!.Value.ToString());
        await _signInManager.SignInAsync(user!, isPersistent: false);

        return LocalRedirect(Url.Content("~/"));
    }

    private async Task LoadAcceptanceContextAsync()
    {
        if (Details.State != InvitationState.Valid)
        {
            return;
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentEmail = User.Identity.Name;
            SignedInAsWrongAccount = !string.Equals(currentEmail, Details.InvitedEmail, StringComparison.OrdinalIgnoreCase);
            return;
        }

        InvitedEmailAlreadyHasAccount = Details.InvitedEmail is not null
            && await _userManager.FindByEmailAsync(Details.InvitedEmail) is not null;
    }

    private static string DescribeFailure(AcceptInvitationOutcome outcome) => outcome switch
    {
        AcceptInvitationOutcome.NotFound => "This invitation is no longer valid.",
        AcceptInvitationOutcome.Expired => "This invitation has expired.",
        AcceptInvitationOutcome.AlreadyUsed => "This invitation has already been used.",
        AcceptInvitationOutcome.EmailMismatch => "This invitation was sent to a different email address. Sign out and use the invited account, or create a new account with that email.",
        _ => "This invitation could not be accepted.",
    };

    public sealed class NewAccountInputModel
    {
        [Required]
        [StringLength(200, MinimumLength = 1, ErrorMessage = "Full name is required.")]
        [Display(Name = "Full name")]
        public string FullName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "Password and confirmation do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
