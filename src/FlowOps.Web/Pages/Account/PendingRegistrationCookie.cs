using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace FlowOps.Web.Pages.Account;

/// <summary>
/// Phase 24A-Extension (ADR-0025): correlates an unauthenticated browser tab on
/// <c>/Account/PendingApproval</c> to the one account it just registered — deliberately NOT the
/// Identity application cookie (no claims, no roles, never touches <c>SignInManager</c>, grants no
/// access to anything else), and deliberately not a raw user id in the query string (would be
/// logged, cached, and shareable). Mirrors <c>CurrentUserAccessor</c>'s own Data-Protection-protected
/// cookie pattern — a plain, non-sensitive, short-lived CONTEXT value, never an authorization grant.
/// Scoped to this one page's path only, so it is never even sent on any other request.
/// </summary>
internal static class PendingRegistrationCookie
{
    private const string CookieName = "FlowOps.PendingRegistration";
    private const string CookiePath = "/Account/PendingApproval";

    public static void Write(HttpContext httpContext, IDataProtectionProvider dataProtectionProvider, Guid userId)
    {
        var protector = dataProtectionProvider.CreateProtector(CookieName);
        var protectedValue = protector.Protect(userId.ToString());
        httpContext.Response.Cookies.Append(CookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            // Matches the application cookie's own SameAsRequest rationale (Program.cs): Secure
            // whenever the request itself was HTTPS, so this still works over plain HTTP in local
            // dev and every WebApplicationFactory-based test, none of which use TLS.
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            // Short-lived: long enough for a Platform Admin to notice and act, never a permanent
            // credential. Once the account leaves Pending, the page stops needing it at all.
            Expires = DateTimeOffset.UtcNow.AddHours(2),
        });
    }

    public static Guid? Read(HttpContext httpContext, IDataProtectionProvider dataProtectionProvider)
    {
        var protector = dataProtectionProvider.CreateProtector(CookieName);
        var cookieValue = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookieValue))
        {
            return null;
        }

        try
        {
            var unprotected = protector.Unprotect(cookieValue);
            return Guid.TryParse(unprotected, out var userId) ? userId : null;
        }
        catch (CryptographicException)
        {
            // Tampered, corrupted, or produced by a since-rotated key — treated as "no context at
            // all" (never as a hint), exactly as safe as a cookie that was never set.
            return null;
        }
    }
}
