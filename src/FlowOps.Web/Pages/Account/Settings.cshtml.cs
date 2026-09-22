using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Domain.Accounts;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// Phase 17: profile/settings for the authenticated caller — full name, email, password, and
/// account deletion. Every handler below resolves the acting user from
/// <c>UserManager.GetUserAsync(User)</c> (the authenticated principal), never from a bound id —
/// there is no id field anywhere in any <c>InputModel</c> here, so there is nothing for a client to
/// substitute (STEP 14's IDOR requirement is satisfied structurally, not by a runtime check).
/// </summary>
public sealed class SettingsModel : PageModel
{
    private readonly AccountService _accountService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public SettingsModel(AccountService accountService, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _accountService = accountService;
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public NameInputModel NameInput { get; set; } = new();

    [BindProperty]
    public EmailInputModel EmailInput { get; set; } = new();

    [BindProperty]
    public PasswordInputModel PasswordInput { get; set; } = new();

    /// <summary>Server-side re-check of the confirmation checkbox — the browser's native HTML5
    /// `required` attribute on it is a UX affordance, never the actual guard, since a direct POST
    /// bypasses any client-side control entirely.</summary>
    [BindProperty]
    public bool ConfirmDelete { get; set; }

    /// <summary>Phase 29C: the option chosen in the Appearance form (its exact name: System, Light or Dark).</summary>
    [BindProperty]
    public string? Appearance { get; set; }

    /// <summary>The user's saved appearance, used to pre-select the radio and as the preview's reset point.</summary>
    public AppearancePreference CurrentAppearance { get; private set; } = AppearancePreference.Dark;

    public string CurrentEmail { get; private set; } = string.Empty;

    public string CurrentDisplayName { get; private set; } = string.Empty;

    /// <summary>The confirmation shown after a save. It must survive the redirect that follows every
    /// successful POST, so it is TempData — as a plain property it was lost on that redirect and no
    /// confirmation ever appeared (found while adding Appearance, whose "saved" feedback depends on it).</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        LoadFromUser(user);
        return Page();
    }

    /// <summary>Saves the caller's own appearance. Only the three defined names are accepted (an
    /// exact match — not a number, not another casing); anything else changes nothing.</summary>
    public async Task<IActionResult> OnPostSetAppearanceAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        ModelState.Clear();
        if (Appearance is null
            || !Enum.GetNames<AppearancePreference>().Contains(Appearance, StringComparer.Ordinal)
            || !await _accountService.SetAppearanceAsync(user.Id, Enum.Parse<AppearancePreference>(Appearance), cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "Choose System, Light or Dark.");
            LoadFromUser(user);
            return Page();
        }

        StatusMessage = "Appearance saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateNameAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        ModelState.Clear(); // discard stray binding-time errors from the other two forms on this page (see class remarks)
        if (!TryValidateModel(NameInput, nameof(NameInput)))
        {
            LoadFromUser(user);
            return Page();
        }

        try
        {
            var result = await _accountService.UpdateDisplayNameAsync(user.Id, NameInput.FullName, cancellationToken);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                LoadFromUser(user);
                return Page();
            }
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadFromUser(user);
            return Page();
        }

        StatusMessage = "Your name has been updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangeEmailAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        ModelState.Clear(); // discard stray binding-time errors from the other two forms on this page (see class remarks)
        if (!TryValidateModel(EmailInput, nameof(EmailInput)))
        {
            LoadFromUser(user);
            return Page();
        }

        try
        {
            var result = await _accountService.ChangeEmailAsync(user.Id, EmailInput.NewEmail, cancellationToken);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                LoadFromUser(user);
                return Page();
            }
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadFromUser(user);
            return Page();
        }

        // Email/username changed underneath the current cookie's claims — Identity's own refresh
        // mechanism (not manual claim manipulation, per STEP 7) reissues the cookie so this session
        // stays valid and the next security-stamp check does not treat it as stale.
        var refreshed = await _userManager.FindByIdAsync(user.Id.ToString());
        await _signInManager.RefreshSignInAsync(refreshed!);

        StatusMessage = "Your email has been updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        ModelState.Clear(); // discard stray binding-time errors from the other two forms on this page (see class remarks)
        if (!TryValidateModel(PasswordInput, nameof(PasswordInput)))
        {
            LoadFromUser(user);
            return Page();
        }

        try
        {
            var result = await _accountService.ChangePasswordAsync(
                user.Id, PasswordInput.CurrentPassword, PasswordInput.NewPassword, cancellationToken);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                LoadFromUser(user);
                return Page();
            }
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadFromUser(user);
            return Page();
        }

        // ChangePasswordAsync already bumped the security stamp — refresh the cookie now rather
        // than let the next request's stamp check treat this very session as stale.
        var refreshed = await _userManager.FindByIdAsync(user.Id.ToString());
        await _signInManager.RefreshSignInAsync(refreshed!);

        StatusMessage = "Your password has been changed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAccountAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        if (!ConfirmDelete)
        {
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, "Confirm that you understand this cannot be undone before deleting your account.");
            LoadFromUser(user);
            return Page();
        }

        DeleteAccountResult result;
        try
        {
            result = await _accountService.DeleteAccountAsync(user.Id, cancellationToken);
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            LoadFromUser(user);
            return Page();
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty,
                "You are the only Admin of " + string.Join(", ", result.BlockedOrganizationNames) +
                ". Assign another Admin in every organization you administer before deleting your account.");
            LoadFromUser(user);
            return Page();
        }

        // Invalidate this session immediately — do not wait on the security-stamp validator's own
        // interval (STEP 9: "do not leave a valid cookie that can continue accessing FlowOps").
        await _signInManager.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }

    private void LoadFromUser(ApplicationUser user)
    {
        CurrentEmail = user.Email ?? string.Empty;
        CurrentDisplayName = user.DisplayName;
        NameInput.FullName = user.DisplayName;
        CurrentAppearance = user.Appearance;
    }

    public sealed class NameInputModel
    {
        [Required]
        [StringLength(200, MinimumLength = 1, ErrorMessage = "Full name is required.")]
        [Display(Name = "Full name")]
        public string FullName { get; set; } = string.Empty;
    }

    public sealed class EmailInputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Email")]
        public string NewEmail { get; set; } = string.Empty;
    }

    public sealed class PasswordInputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "New password and confirmation do not match.")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }
}
