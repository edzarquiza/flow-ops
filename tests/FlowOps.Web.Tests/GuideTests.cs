using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// The Guide feature: the sidebar link, the seven static content pages, and the first-time
/// discovery cue's one-shot behavior. Registers its own disposable organizations (via the real,
/// unthrottled registration flow) rather than touching the shared demo organization, since the
/// discovery cue specifically depends on a genuinely brand-new account's very first page view.
/// </summary>
public sealed class GuideTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public GuideTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SidebarLink_IsVisibleToEveryRole()
    {
        foreach (var email in new[] { TestUsers.AdminEmail, TestUsers.ManagerEmail, TestUsers.AgentEmail, TestUsers.ViewerEmail })
        {
            var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
            await TestAuthentication.SignInAsync(client, email);

            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Contains("href=\"/Guide\"", body, StringComparison.Ordinal);
            Assert.Contains(">Guide<", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/Guide", "Understand how FlowOps works")]
    [InlineData("/Guide/GettingStarted", "Getting Started")]
    [InlineData("/Guide/CoreConcepts", "Core Concepts")]
    [InlineData("/Guide/WorkingWithWork", "Working with Work")]
    [InlineData("/Guide/SlaAndAttention", "SLA")]
    [InlineData("/Guide/ProjectsAndSprints", "Projects &amp; Sprints")]
    [InlineData("/Guide/TeamsAndWorkload", "Teams &amp; Workload")]
    [InlineData("/Guide/RolesAndPermissions", "Roles &amp; Permissions")]
    public async Task EachGuidePage_RendersForAnAuthenticatedMember(string path, string expectedHeading)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedHeading, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guide_AnonymousRequest_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Guide");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // What the AddGuideIntroducedAt migration's own backfill guarantees for every account
           // that already existed before the Guide feature shipped — simulated directly here since
           // this fixture's own seeded users are themselves created after migrations run (so, from
           // the column's own point of view, they are indistinguishable from a genuinely new
           // account unless explicitly marked, exactly like a real pre-existing production row was
           // marked by the migration's one-time UPDATE).
    public async Task UserWithGuideAlreadyIntroduced_NeverSeesTheDiscoveryCue()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.ViewerEmail);
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.GuideIntroducedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("New to FlowOps?", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrandNewAccount_SeesTheCueOnce_ThenNeverAgain()
    {
        var client = RegisterAsync("Guide Cue Admin", out _);

        var firstLoad = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, firstLoad.StatusCode);
        var firstBody = await firstLoad.Content.ReadAsStringAsync();
        Assert.Contains("New to FlowOps?", firstBody, StringComparison.Ordinal);
        Assert.Contains("Start with the Guide to learn how everything works.", firstBody, StringComparison.Ordinal);
        Assert.Contains("Open Guide", firstBody, StringComparison.Ordinal);

        // A second load of the exact same page — no navigation elsewhere, no dismissal click —
        // must never show it again: the server already consumed the one-shot flag on first render.
        var secondLoad = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, secondLoad.StatusCode);
        var secondBody = await secondLoad.Content.ReadAsStringAsync();
        Assert.DoesNotContain("New to FlowOps?", secondBody, StringComparison.Ordinal);
    }

    [Fact] // The cue is Dashboard-only by design (that's the true "first landing" page) — it must
           // never appear on another authenticated page even before the user has visited Dashboard.
    public async Task BrandNewAccount_VisitingAnotherPageFirst_NeverShowsTheCueThere()
    {
        var client = RegisterAsync("Guide Cue Other Page Admin", out _);

        var response = await client.GetAsync("/Organization/Members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("New to FlowOps?", body, StringComparison.Ordinal);
    }

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = UniqueEmail();

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

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@guidetest.local";
}
