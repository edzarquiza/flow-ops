using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Authentication;

/// <summary>
/// Bug fix: <c>LoginModel.OnPostAsync</c> used to <c>LocalRedirect(returnUrl)</c> unconditionally
/// on a successful sign-in. <c>returnUrl</c> is not always something the caller deliberately chose
/// this time around — the login form has no explicit <c>action</c>, so a browser resubmits it back
/// to whatever URL (query string included) the login page happened to be loaded from, which is
/// exactly what ASP.NET Core's own cookie-auth challenge sets whenever an earlier, unrelated,
/// anonymous request bounced off a protected page. For a caller with no organization membership at
/// all, following that straight into a tenant-scoped page always re-fails
/// <c>CurrentUserAccessor.GetCurrentUserAsync</c> a second time and lands on Access Denied on the
/// very first post-login request — a state the caller had no way to anticipate, having just typed a
/// correct password. These tests cover the fixed landing-resolution logic
/// (<c>LoginModel.ResolveLandingDestinationAsync</c>) directly, plus the ordinary per-role paths
/// (CLAUDE.md §15) to confirm the fix does not touch them.
/// </summary>
public sealed class LoginLandingDestinationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public LoginLandingDestinationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData(TestUsersEmailKind.Admin)]
    [InlineData(TestUsersEmailKind.Manager)]
    [InlineData(TestUsersEmailKind.Agent)]
    [InlineData(TestUsersEmailKind.Viewer)]
    public async Task Login_EveryOrganizationRole_LandsOnDashboard_NoAccessDenied(TestUsersEmailKind kind)
    {
        var email = Email(kind);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var loginResponse = await PostLoginAsync(client, email, TestUsers.Password, returnUrl: null);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.ToString());

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var body = await home.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Access Denied", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_PlatformAdminWithNoOrganization_NoReturnUrl_RedirectsToPlatformIndex()
    {
        var email = await CreatePlatformAdminWithNoOrganizationAsync();
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var loginResponse = await PostLoginAsync(client, email, PlatformOnlyPassword, returnUrl: null);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/Platform", loginResponse.Headers.Location?.ToString());
    }

    /// <summary>
    /// The exact regression this fix targets: a stale/inherited <c>returnUrl</c> pointing at a
    /// tenant-scoped page (here, the app root itself — the simplest page that requires an
    /// organization membership to resolve) must not be honored blindly for an identity with no
    /// organization membership at all, since that always re-fails and shows Access Denied. The
    /// caller still lands somewhere real (Platform, since this identity holds platform authority),
    /// not an error.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/Tickets/AtRisk")]
    [InlineData("/Admin")]
    public async Task Login_PlatformAdminWithNoOrganization_StaleTenantReturnUrl_RedirectsToPlatformIndexInstead(string staleReturnUrl)
    {
        var email = await CreatePlatformAdminWithNoOrganizationAsync();
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var loginResponse = await PostLoginAsync(client, email, PlatformOnlyPassword, staleReturnUrl);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/Platform", loginResponse.Headers.Location?.ToString());

        var landing = await client.GetAsync(loginResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
    }

    /// <summary>A genuine /Platform/* returnUrl is still honored as-is for a Platform Admin — this
    /// fix narrows the "don't trust returnUrl" behavior to exactly the case that fails, never to
    /// every returnUrl indiscriminately.</summary>
    [Fact]
    public async Task Login_PlatformAdminWithNoOrganization_PlatformReturnUrl_IsHonored()
    {
        var email = await CreatePlatformAdminWithNoOrganizationAsync();
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var loginResponse = await PostLoginAsync(client, email, PlatformOnlyPassword, "/Platform/Users/Index");

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/Platform/Users/Index", loginResponse.Headers.Location?.ToString());
    }

    /// <summary>
    /// Security regression guard (item 7 / §12): a genuinely unauthorized destination for an
    /// organization member's own role must still show Access Denied after login — this fix must
    /// never weaken that boundary, only the separate "no organization context yet" failure mode.
    /// </summary>
    [Fact]
    public async Task Login_OrganizationMember_UnauthorizedReturnUrl_StillShowsAccessDenied()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        // Agent has real organization membership (TestReferenceData) but is not an Admin — /Admin
        // must remain denied, exactly as it was before this fix, via the page's own role check.
        var loginResponse = await PostLoginAsync(client, TestUsers.AgentEmail, TestUsers.Password, "/Admin");

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/Admin", loginResponse.Headers.Location?.ToString());

        var adminPage = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, adminPage.StatusCode);
        Assert.Contains("/Account/AccessDenied", adminPage.Headers.Location?.ToString());
    }

    /// <summary>A caller with neither organization membership nor platform authority has genuinely
    /// nowhere to land — the existing Forbid() on the app root remains the correct outcome
    /// (unchanged), just reached directly instead of via whatever unrelated page returnUrl named.</summary>
    [Fact]
    public async Task Login_NoOrganizationAndNoPlatformAuthority_StillShowsAccessDenied()
    {
        var email = await CreateOrphanedUserAsync();
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var loginResponse = await PostLoginAsync(client, email, PlatformOnlyPassword, "/Tickets/AtRisk");

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.ToString());

        var landing = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.Contains("/Account/AccessDenied", landing.Headers.Location?.ToString());
    }

    public enum TestUsersEmailKind
    {
        Admin,
        Manager,
        Agent,
        Viewer,
    }

    private const string PlatformOnlyPassword = "Platform-Only-Passw0rd!1";

    private static string Email(TestUsersEmailKind kind) => kind switch
    {
        TestUsersEmailKind.Admin => TestUsers.AdminEmail,
        TestUsersEmailKind.Manager => TestUsers.ManagerEmail,
        TestUsersEmailKind.Agent => TestUsers.AgentEmail,
        TestUsersEmailKind.Viewer => TestUsers.ViewerEmail,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password, string? returnUrl)
    {
        var loginPath = returnUrl is null ? "/Account/Login" : $"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, loginPath);

        using var request = new HttpRequestMessage(HttpMethod.Post, loginPath)
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", password),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    /// <summary>A Platform Admin who belongs to no organization at all — the exact identity shape
    /// this fix exists for. Created directly on the database (mirroring the real bootstrap CLI, as
    /// every other Platform test class already does for platform-admin setup), never through any
    /// HTTP endpoint, since none exists.</summary>
    private async Task<string> CreatePlatformAdminWithNoOrganizationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var email = $"{Guid.NewGuid():N}@platformonly.local";
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<FlowOps.Infrastructure.Identity.ApplicationUser>>();

        var user = new FlowOps.Infrastructure.Identity.ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Platform Only Admin",
            IsActive = true,
            IsPlatformAdmin = true,
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };

        var result = await userManager.CreateAsync(user, PlatformOnlyPassword);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return email;
    }

    /// <summary>Neither an organization member nor a platform administrator — a genuinely
    /// access-less account, e.g. one whose sole membership was later removed.</summary>
    private async Task<string> CreateOrphanedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var email = $"{Guid.NewGuid():N}@orphaned.local";
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<FlowOps.Infrastructure.Identity.ApplicationUser>>();

        var user = new FlowOps.Infrastructure.Identity.ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Orphaned User",
            IsActive = true,
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };

        var result = await userManager.CreateAsync(user, PlatformOnlyPassword);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return email;
    }
}
