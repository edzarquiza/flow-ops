using System.Net;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Platform;

/// <summary>
/// Phase 24 (ADR-0023): the platform-admin authorization boundary itself — <see cref="PlatformAuthorizationTests"/>
/// deliberately covers every role, not just "an ordinary Admin," since platform authority must be
/// unreachable from any organization role, not merely from non-Admins. Platform Admin status is
/// granted directly on the database in test setup (mirroring the real bootstrap CLI's own write —
/// see Program.cs's <c>grant-platform-admin</c> command — never through any HTTP endpoint, since
/// none exists).
/// </summary>
public sealed class PlatformAuthorizationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PlatformAuthorizationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PlatformAdmin_CanAccessPlatform()
    {
        var client = RegisterAsync("PlatformAuth Admin1", out var email);
        await GrantPlatformAdminAsync(email);

        var response = await client.GetAsync("/Platform/Index");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Platform/Index")]
    [InlineData("/Platform/Organizations/Index")]
    [InlineData("/Platform/Users/Index")]
    public async Task OrganizationAdmin_WithoutPlatformAuthority_IsDenied(string path)
    {
        // A genuine organization Admin — not a Platform Admin. This is the exact case ADR-0023
        // exists to prevent: Organization Admin must never automatically gain platform authority.
        var client = RegisterAsync("PlatformAuth OrgAdmin", out _);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task NonAdminOrganizationRoles_AreDenied(UserRole role)
    {
        var ownerClient = RegisterAsync("PlatformAuth Owner" + role, out var ownerEmail);
        var memberEmail = $"{Guid.NewGuid():N}@platformauthtest.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
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
            await userManager.CreateAsync(member, Password);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, member.Id, role, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var memberClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(memberClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", memberEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await memberClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var response = await memberClient.GetAsync("/Platform/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unauthenticated_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Platform/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // Forged organization/user IDs from a non-platform-admin must never mutate anything —
           // the handler itself denies before ever reaching the target lookup.
    public async Task NonPlatformAdmin_ForgedPlatformMutation_IsDenied()
    {
        var client = RegisterAsync("PlatformAuth Forger", out _);

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Platform/Organizations/Details/1?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // Same coarse gate, for Phase 24A's new Approve handler specifically.
    public async Task NonPlatformAdmin_ForgedApproval_IsDenied()
    {
        var client = RegisterAsync("PlatformAuth ApproveForger", out _);

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{Guid.NewGuid()}?handler=Approve")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // Same coarse gate, for Phase 24A-Extension's new Reject handler.
    public async Task NonPlatformAdmin_ForgedRejection_IsDenied()
    {
        var client = RegisterAsync("PlatformAuth RejectForger", out _);

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{Guid.NewGuid()}?handler=Reject")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Theory] // Neither Manager, Agent, nor Viewer — ordinary organization roles — may reject.
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task NonAdminOrganizationRoles_CannotReject(UserRole role)
    {
        var ownerClient = RegisterAsync("PlatformAuth RejectOwner" + role, out var ownerEmail);
        var memberEmail = $"{Guid.NewGuid():N}@platformauthtest.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
            var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

            var member = new ApplicationUser
            {
                UserName = memberEmail,
                Email = memberEmail,
                EmailConfirmed = true,
                DisplayName = "Non-Admin Member",
                IsActive = true,
                RegistrationApprovedAt = DateTimeOffset.UtcNow,
            };
            await userManager.CreateAsync(member, Password);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, member.Id, role, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var memberClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(memberClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", memberEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await memberClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var token = await TestAuthentication.AntiForgeryTokenAsync(memberClient, "/Account/Settings");
        var response = await memberClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Users/Details/{Guid.NewGuid()}?handler=Reject")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

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

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = $"{Guid.NewGuid():N}@platformauthtest.local";

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
