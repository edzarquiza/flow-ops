using System.ComponentModel.DataAnnotations;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace FlowOps.Web.Pages.Account;

/// <summary>ADR-0008 / CLAUDE.md §12: cookie authentication via ASP.NET Core Identity's own
/// <see cref="SignInManager{TUser}"/> — no hand-rolled hashing, no JWT.</summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, isPersistent: false, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        // CLAUDE.md §12: authentication failure must not reveal which credential component was
        // wrong. The same generic message covers "no such user," "wrong password," and "locked
        // out" — distinguishing them would leak which accounts exist or are currently locked.
        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return Page();
    }

    public sealed class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
