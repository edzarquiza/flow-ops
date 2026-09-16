using System.Net;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Authentication;

/// <summary>
/// ADR-0021: <c>/Admin</c>'s coarse gate now checks the caller's <c>OrganizationMembership.Role</c>
/// (via <see cref="FlowOps.Application.Tickets.CurrentUserAccessor"/>) instead of a stale ASP.NET
/// Core Identity role claim that only <c>DemoDataSeeder</c>/<c>TestUsers</c> ever assigned. Every
/// test here registers its own disposable organization through the real, unthrottled registration
/// flow (the same pattern <c>OrganizationManagement.MembersTests</c> uses) so its Admin genuinely
/// has no Identity role claim — the exact case the old policy silently failed for.
/// </summary>
/// <remarks>Phase 24A: registration no longer auto-signs-in, so every <c>RegisterAsync</c> call now
/// also performs one real login POST after approving the account directly (the standard
/// <c>TestAuthentication.ApproveRegistrationAsync</c> shortcut). <c>FlowOpsWebApplicationFactory</c>
/// raises the login rate limit for its own in-process test host precisely so this remains safe
/// regardless of how many accounts a class registers (CLAUDE.md §12's real 5/min/IP limit is
/// unchanged in production).</remarks>
public sealed class AdminAuthorizationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public AdminAuthorizationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact] // The exact regression ADR-0020's Workspace Setup panel exposed.
    public async Task RealAdmin_WithNoIdentityRoleClaim_CanAccessAdmin()
    {
        var client = RegisterAsync("Admin Auth Test Owner", out var email);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            var roleClaims = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user);
            Assert.Empty(roleClaims); // sanity: real registration assigns no Identity role claim
        }

        var response = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RealNonAdminMember_CannotAccessAdmin()
    {
        var ownerClient = RegisterAsync("Admin Auth Test Owner2", out var ownerEmail);
        var memberEmail = $"{Guid.NewGuid():N}@adminauthtest.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
            var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

            var member = new ApplicationUser
            {
                UserName = memberEmail,
                Email = memberEmail,
                EmailConfirmed = true,
                DisplayName = "Non-Admin Member",
                IsActive = true,
            };
            var createResult = await userManager.CreateAsync(member, Password);
            Assert.True(createResult.Succeeded);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, member.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var memberClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await TestAuthentication.AntiForgeryTokenAsync(memberClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", memberEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", token),
            ]),
        };
        var loginResponse = await memberClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var response = await memberClient.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
        _ = ownerClient;
    }

    [Fact] // Current organization determines the role — Admin somewhere else must not leak in.
    public async Task AdminInOneOrganization_IsNotAdminInAnother_WhileCurrentOrganizationIsTheOther()
    {
        var client = RegisterAsync("Admin Auth Test MultiOrg", out var email);

        int otherOrganizationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);

            var otherOrganization = new Organization(0, $"Other Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Organizations.Add(otherOrganization);
            await db.SaveChangesAsync();
            otherOrganizationId = otherOrganization.Id;

            // Agent, not Admin, in this second organization.
            db.OrganizationMemberships.Add(new OrganizationMembership(0, otherOrganizationId, user.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var switchToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/");
        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, "/Organization/Switch")
        {
            Content = new FormUrlEncodedContent(
            [
                new("organizationId", otherOrganizationId.ToString()),
                new("__RequestVerificationToken", switchToken),
            ]),
        };
        var switchResponse = await client.SendAsync(switchRequest);
        Assert.Equal(HttpStatusCode.Redirect, switchResponse.StatusCode);

        var response = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = $"{Guid.NewGuid():N}@adminauthtest.local";

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
}
