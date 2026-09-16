using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Platform;

/// <summary>Phase 24 (ADR-0023) / Phase 24A (ADR-0024): <c>/Platform/Users</c> over real HTTP,
/// including account approval.</summary>
/// <remarks>Several real login POSTs in this class (the deactivate/reactivate round trip, plus one
/// per registered fixture since Phase 24A). Safe regardless of count: <c>FlowOpsWebApplicationFactory</c>
/// raises the login rate limit for its own in-process test host (CLAUDE.md §12's real 5/min/IP limit
/// is unchanged in production).</remarks>
public sealed class PlatformUsersTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PlatformUsersTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PlatformAdmin_CanListAndInspectAUser()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant1", out var tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin1", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var userId = await GetUserIdAsync(tenantEmail);

        var listResponse = await platformClient.GetAsync("/Platform/Users/Index");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listHtml = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains(tenantEmail, listHtml, StringComparison.Ordinal);

        var detailResponse = await platformClient.GetAsync($"/Platform/Users/Details/{userId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync();
        Assert.Contains("PlatformUser Tenant1", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Admin", detailHtml, StringComparison.Ordinal);
    }

    [Fact] // The most important live scenario: deactivation actually blocks login, and
           // reactivation actually restores it.
    public async Task DeactivateThenReactivate_LoginFailsThenSucceedsAgain()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant2", out var tenantEmail);
        var secondAdminId = await AddSecondAdminAsync(tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin2", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{secondAdminId}");
        var deactivateToken = ExtractAntiForgeryToken(detailHtml);
        var deactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{secondAdminId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deactivateToken)]),
        });
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        var secondAdminEmail = await GetEmailAsync(secondAdminId);
        var loginClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(loginClient, "/Account/Login");
        var loginResponse = await loginClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", secondAdminEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        });
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode); // re-rendered with an error, not redirected
        // CLAUDE.md §12: the login page deliberately shows the same generic message for "no such
        // user," "wrong password," and "locked out" — never distinguishing them — so this asserts
        // failure, not the (deliberately undisclosed) reason.
        Assert.Contains("Invalid login attempt", loginBody, StringComparison.Ordinal);

        // Historical membership must remain — platform deactivation is not account deletion.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var membershipCount = await db.OrganizationMemberships.CountAsync(m => m.UserId == secondAdminId);
            Assert.Equal(1, membershipCount);
        }

        var reactivateHtml = await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{secondAdminId}");
        var reactivateToken = ExtractAntiForgeryToken(reactivateHtml);
        var reactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{secondAdminId}?handler=Reactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", reactivateToken)]),
        });
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);

        var loginClient2 = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken2 = await TestAuthentication.AntiForgeryTokenAsync(loginClient2, "/Account/Login");
        var loginResponse2 = await loginClient2.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", secondAdminEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken2),
            ]),
        });
        Assert.Equal(HttpStatusCode.Redirect, loginResponse2.StatusCode); // signed in successfully
    }

    [Fact] // ORG-RULE-12, reused: Platform Admin cannot deactivate the sole Admin of an organization.
    public async Task DeactivateSoleAdmin_IsBlocked()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant3", out var tenantEmail);
        var soleAdminId = await GetUserIdAsync(tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin3", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{soleAdminId}");
        var token = ExtractAntiForgeryToken(detailHtml);
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{soleAdminId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("only Admin", body, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == soleAdminId);
        Assert.True(user.IsActive);
    }

    [Fact]
    public async Task UserDetail_RendersRecentActivityAfterDeactivateAndReactivate()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant6", out var tenantEmail);
        var secondAdminId = await AddSecondAdminAsync(tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin6", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var token = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{secondAdminId}"));
        var deactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{secondAdminId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });
        var html = await deactivateResponse.Content.ReadAsStringAsync();

        Assert.Contains("UserDeactivated", html, StringComparison.Ordinal);
    }

    [Fact] // Phase 24A (ADR-0024): the core approval workflow end-to-end over real HTTP.
    public async Task PlatformAdmin_CanSeeAndApproveAPendingAccount()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformUser Pending1");
        var platformClient = RegisterAsync("PlatformUser Admin7", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        var pendingListHtml = await GetHtmlAsync(platformClient, "/Platform/Users/Pending");
        Assert.Contains(pendingEmail, pendingListHtml, StringComparison.Ordinal);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{pendingUserId}");
        Assert.Contains("Pending approval", detailHtml, StringComparison.Ordinal);
        var token = ExtractAntiForgeryToken(detailHtml);

        var approveResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{pendingUserId}?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var afterApproveHtml = await approveResponse.Content.ReadAsStringAsync();
        Assert.Contains("Active", afterApproveHtml, StringComparison.Ordinal);

        // The now-approved account can genuinely sign in — the point of the whole gate.
        var approvedClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(approvedClient, pendingEmail, Password);
        var dashboard = await approvedClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task Approve_AlreadyApprovedAccount_IsIdempotent()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant8", out var tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin8", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var userId = await GetUserIdAsync(tenantEmail);

        var token = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{userId}"));
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{userId}?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // idempotent success, not an error page
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Active", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_MissingAntiforgeryToken_IsRejected()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformUser Pending2");
        var platformClient = RegisterAsync("PlatformUser Admin9", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{pendingUserId}?handler=Approve");
        var response = await platformClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Approve_ForgedUserId_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformUser Admin10", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var token = await TestAuthentication.AntiForgeryTokenAsync(platformClient, "/Platform/Users/Index");
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{Guid.NewGuid()}?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // Phase 24A-Extension (ADR-0025): the core rejection workflow end-to-end over real HTTP.
    public async Task PlatformAdmin_CanRejectAPendingAccount_AndItCannotThenSignIn()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformUser Pending3");
        var platformClient = RegisterAsync("PlatformUser Admin11", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{pendingUserId}");
        var token = ExtractAntiForgeryToken(detailHtml);

        var rejectResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{pendingUserId}?handler=Reject")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
        var afterRejectHtml = await rejectResponse.Content.ReadAsStringAsync();
        Assert.Contains("Rejected", afterRejectHtml, StringComparison.Ordinal);

        // The rejected account genuinely cannot sign in even with correct credentials.
        var rejectedClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(rejectedClient, "/Account/Login");
        var loginResponse = await rejectedClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", pendingEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode); // re-rendered, never redirected
        Assert.Contains("was not approved", await loginResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reject_ActiveAccount_IsRejected_AccountUnaffected()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant9", out var tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin12", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var userId = await GetUserIdAsync(tenantEmail);

        var token = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, $"/Platform/Users/Details/{userId}"));
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{userId}?handler=Reject")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot be rejected", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Active", body, StringComparison.Ordinal); // still active — rejection did not apply
    }

    [Fact]
    public async Task Reject_MissingAntiforgeryToken_IsRejected()
    {
        var pendingEmail = RegisterWithoutApproving("PlatformUser Pending4");
        var platformClient = RegisterAsync("PlatformUser Admin13", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var pendingUserId = await GetUserIdAsync(pendingEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{pendingUserId}?handler=Reject");
        var response = await platformClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reject_ForgedUserId_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformUser Admin14", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var token = await TestAuthentication.AntiForgeryTokenAsync(platformClient, "/Platform/Users/Index");
        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{Guid.NewGuid()}?handler=Reject")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForgedUserId_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformUser Admin4", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var response = await platformClient.GetAsync($"/Platform/Users/Details/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_MissingAntiforgeryToken_IsRejected()
    {
        var tenantClient = RegisterAsync("PlatformUser Tenant5", out var tenantEmail);
        var userId = await GetUserIdAsync(tenantEmail);
        var platformClient = RegisterAsync("PlatformUser Admin5", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{userId}?handler=Deactivate");
        var response = await platformClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private async Task<string> GetEmailAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.Email!).SingleAsync();
    }

    private async Task<Guid> AddSecondAdminAsync(string ownerEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<FlowOps.Infrastructure.Identity.ApplicationUser>>();
        var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
        var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

        var email = $"{Guid.NewGuid():N}@platformusertest.local";
        var member = new FlowOps.Infrastructure.Identity.ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Second Admin",
            IsActive = true,
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };
        await userManager.CreateAsync(member, Password);
        db.OrganizationMemberships.Add(new FlowOps.Domain.Organizations.OrganizationMembership(0, ownerMembership.OrganizationId, member.Id, FlowOps.Domain.Tickets.UserRole.Admin, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return member.Id;
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
        var capturedEmail = $"{Guid.NewGuid():N}@platformusertest.local";

        var token = TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register").GetAwaiter().GetResult();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", capturedEmail),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName}'s Org"),
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

    /// <summary>Registers a genuinely Pending account (Phase 24A) — no approval, no sign-in —
    /// for tests of the approval workflow itself.</summary>
    private string RegisterWithoutApproving(string fullName)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@platformusertest.local";

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
