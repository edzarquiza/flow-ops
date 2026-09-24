using System.Net;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Projects;

/// <summary>
/// Phase 29D over real HTTP: the sidebar's "Projects" nav group — a collapsible disclosure that
/// never navigates on its own, its own project links, tenant isolation, active/inactive filtering,
/// the auto-expand-and-mark-active behavior on a project's own pages, and the accessibility
/// attributes the group renders. Every test registers its own disposable organization via the
/// real, unthrottled registration flow, the same pattern <see cref="ProjectPlanningPagesTests"/>
/// already uses.
/// </summary>
public sealed class SidebarProjectsNavigationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public SidebarProjectsNavigationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_ShowsTheProjectsGroupCollapsed_NotInsideAnyProject()
    {
        var (client, _, _) = await RegisterAsync("Sidebar Dashboard");

        var html = await GetAsync(client, "/");

        Assert.Contains("<details class=\"fo-sidebar__nav-group\" data-inside-project=\"false\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("open=\"open\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"false\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClickingTheProjectsSummary_HasNoHrefAndCannotNavigate_TheToggleIsPureDisclosure()
    {
        var (client, _, _) = await RegisterAsync("Sidebar NoNav");

        var html = await GetAsync(client, "/");

        // The parent control is a <summary>, never an <a> — there is nothing for a click on it to
        // navigate to; expand/collapse is the only thing it can do.
        var summaryStart = html.IndexOf("<summary aria-expanded=", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0, "Projects group summary not found.");
        var summaryTag = html[summaryStart..html.IndexOf('>', summaryStart)];
        Assert.DoesNotContain("href=", summaryTag, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page", summaryTag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SidebarLists_OnlyActiveProjects_OrderedByName_WithACrossOrgProjectNeverAppearing()
    {
        var (client, organizationId, _) = await RegisterAsync("Sidebar Listing");
        var (otherClient, _, _) = await RegisterAsync("Sidebar Listing Other Org");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            db.Projects.Add(new FlowOps.Domain.Catalog.Project(0, organizationId, "Zebra Rollout", DateTimeOffset.UtcNow));
            db.Projects.Add(new FlowOps.Domain.Catalog.Project(0, organizationId, "Alpha Rollout", DateTimeOffset.UtcNow));
            db.Projects.Add(new FlowOps.Domain.Catalog.Project(0, organizationId, "Deactivated Rollout", DateTimeOffset.UtcNow, isActive: false));
            await db.SaveChangesAsync();
        }

        var html = await GetAsync(client, "/");
        var otherHtml = await GetAsync(otherClient, "/");

        var alphaIndex = html.IndexOf("Alpha Rollout", StringComparison.Ordinal);
        var zebraIndex = html.IndexOf("Zebra Rollout", StringComparison.Ordinal);
        Assert.True(alphaIndex >= 0 && zebraIndex >= 0, "Both active projects should be listed.");
        Assert.True(alphaIndex < zebraIndex, "Projects should be ordered by name ascending.");
        Assert.DoesNotContain("Deactivated Rollout", html, StringComparison.Ordinal);

        // Tenant isolation: another organization's projects never appear in this caller's sidebar.
        Assert.DoesNotContain("Alpha Rollout", otherHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Zebra Rollout", otherHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectLink_PointsToTheExistingOverviewRoute()
    {
        var (client, organizationId, _) = await RegisterAsync("Sidebar Route");
        int projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var project = new FlowOps.Domain.Catalog.Project(0, organizationId, "Laptop Refresh 2026", DateTimeOffset.UtcNow);
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        var html = await GetAsync(client, "/");

        Assert.Contains($"href=\"/Projects/Details/{projectId}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsideAProject_TheGroupAutoExpands_AndThatProjectIsMarkedActive_TheOtherIsNot()
    {
        var (client, organizationId, _) = await RegisterAsync("Sidebar Active");
        int projectAId;
        int projectBId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var projectA = new FlowOps.Domain.Catalog.Project(0, organizationId, "Laptop Refresh 2026", DateTimeOffset.UtcNow);
            var projectB = new FlowOps.Domain.Catalog.Project(0, organizationId, "Billing Portal Stabilization", DateTimeOffset.UtcNow);
            db.Projects.AddRange(projectA, projectB);
            await db.SaveChangesAsync();
            projectAId = projectA.Id;
            projectBId = projectB.Id;
        }

        var html = await GetAsync(client, $"/Projects/Details/{projectAId}");

        Assert.Contains("<details class=\"fo-sidebar__nav-group\" data-inside-project=\"true\" open=\"open\">", html, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"true\"", html, StringComparison.Ordinal);

        var linkAStart = html.IndexOf($"href=\"/Projects/Details/{projectAId}\"", StringComparison.Ordinal);
        // The rendered anchor for project A carries aria-current="page"; project B's does not.
        var anchorAStart = html.LastIndexOf("<a", linkAStart, StringComparison.Ordinal);
        var anchorAEnd = html.IndexOf('>', linkAStart);
        Assert.Contains("aria-current=\"page\"", html[anchorAStart..anchorAEnd], StringComparison.Ordinal);

        var linkBStart = html.IndexOf($"href=\"/Projects/Details/{projectBId}\"", StringComparison.Ordinal);
        var anchorBStart = html.LastIndexOf("<a", linkBStart, StringComparison.Ordinal);
        var anchorBEnd = html.IndexOf('>', linkBStart);
        Assert.DoesNotContain("aria-current=\"page\"", html[anchorBStart..anchorBEnd], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoActiveProjects_ShowsNoEmptySubmenuText_ButViewAllProjectsRemainsReachable()
    {
        var (client, _, _) = await RegisterAsync("Sidebar Empty");

        var html = await GetAsync(client, "/");

        Assert.Contains("<details class=\"fo-sidebar__nav-group\" data-inside-project=\"false\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No projects", html, StringComparison.Ordinal);
        Assert.Contains("View all projects", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Projects\"", html, StringComparison.Ordinal);

        // The management page remains directly reachable regardless of what the sidebar shows.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Projects")).StatusCode);
    }

    [Fact] // The full text of an existing project's own management page is untouched by this
           // phase — the sidebar is a new, separate affordance, not a replacement.
    public async Task ProjectsManagementPage_RemainsFullyReachable_AndListsEveryProject()
    {
        var (client, organizationId, _) = await RegisterAsync("Sidebar Management");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            db.Projects.Add(new FlowOps.Domain.Catalog.Project(0, organizationId, "Laptop Refresh 2026", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var html = await GetAsync(client, "/Projects");

        Assert.Contains("Laptop Refresh 2026", html, StringComparison.Ordinal);
    }

    [Fact] // ADR-0023: a Platform Admin with no organization membership has nothing tenant-scoped
           // to show — the Projects nav group must not render dead-looking links for them, the same
           // gating every other tenant-scoped sidebar item already uses.
    public async Task PlatformAdminWithNoOrganization_SeesNoProjectsGroupAtAll()
    {
        var email = await CreatePlatformAdminWithNoOrganizationAsync();
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email, Password);

        var html = await GetAsync(client, "/Platform/Index");

        Assert.DoesNotContain("fo-sidebar__nav-group", html, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private async Task<string> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<(HttpClient Client, int OrganizationId, string Email)> RegisterAsync(string fullName)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@sidebarprojectstest.local";

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        var register = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", email),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName} Org"),
                new("__RequestVerificationToken", token),
            ]),
        });
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        await TestAuthentication.SignInAsync(client, email, Password);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        var organizationId = (await db.OrganizationMemberships.SingleAsync(m => m.UserId == user.Id)).OrganizationId;

        return (client, organizationId, email);
    }

    /// <summary>Granted directly via <see cref="ApplicationUser.IsPlatformAdmin"/> (the same way
    /// every other Platform test class already does for platform-admin setup), never through any
    /// HTTP endpoint, since none exists.</summary>
    private async Task<string> CreatePlatformAdminWithNoOrganizationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var email = $"{Guid.NewGuid():N}@sidebarplatformonly.local";
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Platform Only Admin",
            IsActive = true,
            IsPlatformAdmin = true,
        };
        Assert.True((await userManager.CreateAsync(user, Password)).Succeeded);

        return email;
    }
}
