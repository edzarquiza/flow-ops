using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Platform;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Accounts;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace FlowOps.Web.Pages.Account;

/// <summary>ADR-0008 / CLAUDE.md §12: cookie authentication via ASP.NET Core Identity's own
/// <see cref="SignInManager{TUser}"/> — no hand-rolled hashing, no JWT.</summary>
/// <remarks>
/// Phase 24A-Extension (ADR-0025): a Pending or Rejected account gets its own specific message
/// (spec §23) — but only once the submitted password has already been verified correct
/// (<see cref="UserManager{TUser}.CheckPasswordAsync"/>, independent of <c>PasswordSignInAsync</c>'s
/// own lockout-driven result). A caller who does not already hold the correct password for a real
/// account still sees the one generic "Invalid login attempt" message, exactly as before — this is
/// what keeps the more specific messages from becoming an account-enumeration oracle: they are only
/// ever shown to someone who has already proven, by supplying the right password, that they are
/// either the account's own owner or someone who already obtained its credentials by other means.
/// </remarks>
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly DemoOptions _demoOptions;
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly PlatformUserAccessor _platformUserAccessor;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        DemoOptions demoOptions,
        CurrentUserAccessor currentUserAccessor,
        PlatformUserAccessor platformUserAccessor)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _demoOptions = demoOptions;
        _currentUserAccessor = currentUserAccessor;
        _platformUserAccessor = platformUserAccessor;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    /// <summary>CLAUDE.md §14: "credentials supplied via environment variables, displayed on the
    /// login page, never in git." Empty whenever Demo:Enabled is false — the ordinary case.</summary>
    public bool DemoEnabled => _demoOptions.Enabled;

    public string DemoPersonaPassword => _demoOptions.PersonaPassword ?? string.Empty;

    public IReadOnlyList<DemoPersona> DemoPersonas => FlowOps.Application.Demo.DemoPersonas.All;

    /// <summary>
    /// Bug fix: an already-authenticated caller visiting this page (most commonly: the same
    /// browser has one tab signed in as one account — e.g. a Platform Admin approving a new
    /// signup — and a second tab lands here after that signup's own approval-page redirect;
    /// cookies are shared across tabs, so the second tab is "authenticated" too) used to see the
    /// bare login form, but wrapped in _Layout.cshtml's normal authenticated sidebar shell —
    /// showing the *other* account's identity around a form that looks like a fresh, anonymous
    /// sign-in. Redirecting immediately to that identity's own landing destination (the same
    /// resolution a fresh sign-in itself uses) is the standard, unsurprising behavior: this page
    /// is "sign in," not "sign in as someone else while already signed in." Never weakens
    /// authorization — it is still that same identity's own authorized destination, exactly as if
    /// they had navigated there directly.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;

        if (User.Identity?.IsAuthenticated == true)
        {
            return await ResolveLandingDestinationAsync(returnUrl);
        }

        return Page();
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
            return await ResolveLandingDestinationAsync(returnUrl);
        }

        ModelState.AddModelError(string.Empty, await DescribeFailureAsync());
        return Page();
    }

    /// <summary>
    /// Bug fix: a bare <c>LocalRedirect(returnUrl)</c> here trusted whatever the login page's own
    /// query string happened to carry — including a stale <c>returnUrl</c> left over from an
    /// earlier, unrelated anonymous bounce off a protected page (the login form has no explicit
    /// action, so the browser resubmits back to whatever URL — query string included — the login
    /// page itself was loaded from; ASP.NET Core's own cookie-auth challenge is what put a
    /// <c>ReturnUrl</c> there in the first place). For a caller with no organization membership at
    /// all, following that straight into a tenant-scoped page (or even the app root) always
    /// re-fails <see cref="CurrentUserAccessor.GetCurrentUserAsync(System.Security.Claims.ClaimsPrincipal,System.Threading.CancellationToken)"/>
    /// a second time and lands on Access Denied — an organization-context problem the caller had
    /// no way to see coming, since they had just typed a correct password. <see cref="IndexModel"/>
    /// already special-cases exactly this ("no organization, but real platform authority" lands on
    /// <c>/Platform</c> rather than a bare Forbid) — but only for its own default destination, never
    /// for a <c>returnUrl</c> that bypasses it entirely. This resolves the same authoritative
    /// organization/platform context <em>before</em> redirecting, so the very first post-login
    /// response already lands somewhere that context can reach.
    ///
    /// An organization member's own <c>returnUrl</c> is trusted exactly as before: their own
    /// destination page still independently enforces its own role check
    /// (<see cref="Domain.Organizations.OrganizationAccessPolicy"/>, <c>TicketAccessPolicy</c>,
    /// etc.), so a genuinely unauthorized destination for their role still shows Access Denied —
    /// this fix changes only the "no organization context yet" case, never weakens an existing
    /// authorization boundary, and never substitutes a role check with its own.
    /// </summary>
    private async Task<IActionResult> ResolveLandingDestinationAsync(string? returnUrl)
    {
        if (await _currentUserAccessor.GetCurrentUserAsync(User) is not null)
        {
            return LocalRedirect(returnUrl ?? Url.Content("~/"));
        }

        var platformAdmin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User);
        if (platformAdmin is not null)
        {
            // A genuine /Platform/* returnUrl is still safe to honor as-is — those pages authorize
            // via PlatformUserAccessor themselves, never CurrentUserAccessor, so they never hit the
            // failure mode this fix targets. Anything else (absent, or a tenant-scoped page this
            // identity has no membership for) goes to the one destination a Platform Admin with no
            // organization at all can actually reach.
            if (returnUrl is not null && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/Platform", StringComparison.OrdinalIgnoreCase))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToPage("/Platform/Index");
        }

        // Neither an organization member nor a platform administrator: there is genuinely no
        // destination this identity can reach. The app root's own Forbid() is the existing,
        // correct outcome for this case (CLAUDE.md §7's Access Denied experience is unchanged) —
        // routed there directly rather than through whatever unrelated page returnUrl named, so the
        // caller sees one consistent, explained denial rather than an arbitrary page's own.
        return LocalRedirect(Url.Content("~/"));
    }

    /// <summary>
    /// CLAUDE.md §12's generic "Invalid login attempt" remains the answer for "no such user,"
    /// "wrong password," and a plain deactivated (Inactive) account — distinguishing those would
    /// leak which accounts exist or are currently locked. Pending/Rejected get their own message
    /// (spec §23), but only once the password itself has already been verified correct — see this
    /// class's own remarks for why that is safe.
    /// </summary>
    private async Task<string> DescribeFailureAsync()
    {
        const string generic = "Invalid login attempt.";

        var user = await _userManager.FindByEmailAsync(Input.Email);
        if (user is null || !await _userManager.CheckPasswordAsync(user, Input.Password))
        {
            return generic;
        }

        return AccountStatusResolver.Resolve(user) switch
        {
            AccountStatus.Pending => "Your account is awaiting approval by a FlowOps administrator.",
            AccountStatus.Rejected => "Your account was not approved by a FlowOps administrator.",
            _ => generic,
        };
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
