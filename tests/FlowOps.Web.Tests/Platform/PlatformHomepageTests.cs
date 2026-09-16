using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Platform;

/// <summary>Phase 24B: the redesigned Platform homepage over real HTTP — organizations, users, and
/// pending approvals must already be visible at <c>/Platform</c> itself, and Approve must work
/// directly from there through the exact same <c>PlatformUserService.ApproveUserAsync</c> every
/// other approval entry point uses.</summary>
public sealed class PlatformHomepageTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PlatformHomepageTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PlatformAdmin_SeesOverviewOrganizationsAndUsers_WithoutNavigatingAnywhere()
    {
        var tenantClient = RegisterAsync("PlatformHome Tenant1", out var tenantEmail);
        _ = tenantClient;
        var platformClient = RegisterAsync("PlatformHome Admin1", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var response = await platformClient.GetAsync("/Platform/Index");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Platform Administration", html, StringComparison.Ordinal);
        Assert.Contains("Organizations", html, StringComparison.Ordinal);
        Assert.Contains("Users", html, StringComparison.Ordinal);
        Assert.Contains("Tickets", html, StringComparison.Ordinal);
        Assert.Contains("Platform health", html, StringComparison.Ordinal);
        // The tenant registered above must already be visible here, with no click-through.
        Assert.Contains("PlatformHome Tenant1 Org", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingAccount_AppearsOnHomepage_AndCanBeApprovedDirectlyThere()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformHome Pending1");
        var platformClient = RegisterAsync("PlatformHome Admin2", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        var homeHtml = await GetHtmlAsync(platformClient, "/Platform/Index");
        Assert.Contains("Pending approvals", homeHtml, StringComparison.Ordinal);
        Assert.Contains(pendingEmail, homeHtml, StringComparison.Ordinal);
        // The org name contains an apostrophe (Razor HTML-encodes it, e.g. "&#x27;"), so this
        // checks the encoding-stable prefix rather than the exact raw string.
        Assert.Contains("PlatformHome Pending1", homeHtml, StringComparison.Ordinal);

        var token = ExtractAntiForgeryToken(homeHtml);
        var approveResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Index?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("id", pendingUserId.ToString()), new("__RequestVerificationToken", token)]),
        });
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var afterHtml = await approveResponse.Content.ReadAsStringAsync();

        // The approved account no longer appears among pending accounts.
        Assert.DoesNotContain(pendingEmail + "</td>", afterHtml, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == pendingUserId);
        Assert.NotNull(user.RegistrationApprovedAt);
        Assert.True(user.IsActive);

        // The now-approved account can genuinely sign in.
        var approvedClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(approvedClient, pendingEmail, Password);
        var dashboard = await approvedClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task NoPendingAccounts_ShowsCompactEmptyState_NotAnEmptySection()
    {
        var platformClient = RegisterAsync("PlatformHome Admin3", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var html = await GetHtmlAsync(platformClient, "/Platform/Index");

        Assert.Contains("Pending approvals", html, StringComparison.Ordinal);
        Assert.Contains("All accounts are currently approved.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_MissingAntiforgeryToken_IsRejected()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformHome Pending2");
        var platformClient = RegisterAsync("PlatformHome Admin4", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Platform/Index?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("id", pendingUserId.ToString())]),
        };
        var response = await platformClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Cleanup, not part of the assertion: this class shares one WebApplicationFactory/database
        // across all its tests (IClassFixture), and the rejected POST above deliberately left this
        // account genuinely Pending — approving it here keeps sibling tests (e.g. the "no pending
        // accounts" empty-state test) from observing state this test alone created.
        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, pendingEmail);
    }

    [Fact]
    public async Task Approve_ForgedUserId_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformHome Admin5", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var token = await TestAuthentication.AntiForgeryTokenAsync(platformClient, "/Platform/Index");
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Index?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("id", Guid.NewGuid().ToString()), new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // The coarse gate — a tenant Organization Admin must never reach the homepage's data.
    public async Task OrganizationAdmin_WithoutPlatformAuthority_CannotAccessHomepage()
    {
        var client = RegisterAsync("PlatformHome OrgAdmin", out _);

        var response = await client.GetAsync("/Platform/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // Same coarse gate, for the homepage's own Approve handler specifically — a forged
           // mutation from a non-platform-admin must never reach PlatformUserService at all.
    public async Task NonPlatformAdmin_ForgedHomepageApproval_IsDenied()
    {
        var client = RegisterAsync("PlatformHome Forger", out _);

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Index?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("id", Guid.NewGuid().ToString()), new("__RequestVerificationToken", token)]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    // ---- helpers ----

    private async Task GrantPlatformAdminAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.IsPlatformAdmin = true;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> GetUserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No antiforgery token found in response.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = $"{Guid.NewGuid():N}@platformhometest.local";

        var token = TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register").GetAwaiter().GetResult();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", capturedEmail),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName} Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // to PendingApproval, not the dashboard

        TestAuthentication.ApproveRegistrationAsync(_factory.Services, capturedEmail).GetAwaiter().GetResult();
        TestAuthentication.SignInAsync(client, capturedEmail, Password).GetAwaiter().GetResult();

        email = capturedEmail;
        return client;
    }

    /// <summary>Registers a genuinely Pending account — no approval, no sign-in — for tests of the
    /// homepage's Pending Approvals section itself.</summary>
    private string RegisterWithoutApproving(string fullName)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@platformhometest.local";

        var token = TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register").GetAwaiter().GetResult();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", email),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName}'s Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        return email;
    }
}
