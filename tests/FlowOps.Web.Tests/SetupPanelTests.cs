using System.Net;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// ADR-0020: the dashboard's first-run workspace setup panel, over real HTTP. Every test registers
/// its own disposable organization via the real, unthrottled registration flow (the same pattern
/// <c>OrganizationManagement.MembersTests</c> uses) rather than touching the shared demo
/// organization, whose dashboard is covered separately by <see cref="DemoOrganization_NeverSeesSetupPanel"/>.
/// </summary>
/// <remarks>Phase 24A: registration no longer auto-signs-in, so <c>RegisterAsync</c> now also
/// performs one real login POST per call after approving the account directly. Safe regardless of
/// volume: <c>FlowOpsWebApplicationFactory</c> raises the login rate limit for its own in-process
/// test host (CLAUDE.md §12's real 5/min/IP limit is unchanged in production).</remarks>
public sealed class SetupPanelTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public SetupPanelTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task NewOrganization_Admin_SeesSetupPanelWithIncompleteItems()
    {
        var client = RegisterAsync("Setup Panel Admin", out _);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("setup__title", html, StringComparison.Ordinal);
        Assert.Contains("Welcome to", html, StringComparison.Ordinal);
        Assert.Contains("Set up your first team", html, StringComparison.Ordinal);
        Assert.Contains("Invite your team", html, StringComparison.Ordinal);
        Assert.Contains("Create your first project", html, StringComparison.Ordinal);
        Assert.Contains("Create your first ticket", html, StringComparison.Ordinal);
        // One primary button on the dashboard (the first incomplete step) — never more than one.
        Assert.Equal(1, CountOccurrences(html, "btn-primary"));
        // All four actions share one geometry class (visual refinement pass) — never per-button sizing.
        Assert.Equal(4, CountOccurrences(html, "setup__btn"));
    }

    [Fact] // ADR-0021: the onboarding action a real Admin actually reaches must not dead-end in
           // Access Denied — ties the Workspace Setup link directly to the authorization fix.
    public async Task NewOrganization_SetUpFirstTeamAction_NoLongerLeadsToAccessDenied()
    {
        var client = RegisterAsync("Setup Panel Admin AccessCheck", out _);

        var dashboard = await client.GetAsync("/");
        var html = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Admin\"", html, StringComparison.Ordinal);

        var adminResponse = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact] // End-to-end: the real /Admin team-creation form actually advances the checklist item —
           // not just "no longer denied," but genuinely functional.
    public async Task CreatingATeamViaAdmin_CompletesTheSetUpTeamChecklistItem()
    {
        var client = RegisterAsync("Setup Panel Team Creation", out _);

        var before = await client.GetAsync("/");
        var beforeHtml = await before.Content.ReadAsStringAsync();
        Assert.Contains("Create the team that will handle your organization's work.", beforeHtml, StringComparison.Ordinal);

        var adminPage = await client.GetAsync("/Admin");
        var adminHtml = await adminPage.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(adminHtml);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=CreateTeam")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.TeamName", "Service Desk"),
                new("Input.CategoryName", "Incidents"),
                new("Input.CategoryWorkType", "Incident"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var after = await client.GetAsync("/");
        var afterHtml = await after.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Create the team that will handle your organization's work.", afterHtml, StringComparison.Ordinal);
        Assert.Contains("2 of 5 complete", afterHtml, StringComparison.Ordinal);
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

    [Fact] // Onboarding spec §6: a genuinely fresh org, not just an empty scope within an
           // already-populated one, must never show a fabricated percentage.
    public async Task NewOrganization_Dashboard_ShowsHonestEmptyAnalytics()
    {
        var client = RegisterAsync("Setup Honesty Admin", out _);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("No resolved tickets yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">100%<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">NaN<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullyConfiguredOrganization_PanelIsAbsent()
    {
        var client = RegisterAsync("Setup Complete Admin", out var email);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Email == email);
            var membership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == admin.Id);
            var organizationId = membership.OrganizationId;

            var team = new Team(0, organizationId, $"Complete Team {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(team);
            await db.SaveChangesAsync();
            var category = new Category(0, team.Id, $"Complete Category {Guid.NewGuid():N}", WorkType.Incident, DateTimeOffset.UtcNow);
            db.Add(category);
            db.Add(new Project(0, organizationId, $"Complete Project {Guid.NewGuid():N}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            var secondUser = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@setuppanel.test.local",
                Email = $"{Guid.NewGuid():N}@setuppanel.test.local",
                DisplayName = "Second Member",
                IsActive = true,
            };
            db.Users.Add(secondUser);
            await db.SaveChangesAsync();
            db.OrganizationMemberships.Add(new OrganizationMembership(0, organizationId, secondUser.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            var ticketService = new FlowOps.Application.Tickets.TicketService(db, TimeProvider.System, TestEmail.Sender, TestEmail.Options);
            await ticketService.CreateAsync(
                new FlowOps.Application.Tickets.CreateTicketRequest("A complete-setup ticket", "A routine description.", WorkType.Incident, Priority.Medium, team.Id, category.Id, null),
                new CurrentUser(admin.Id, organizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>()));
        }

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("setup__title", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to", html, StringComparison.Ordinal);
    }

    [Fact] // This factory's shared seeded organization (TestReferenceData) is already fully
           // configured (team, category, ticket, multiple members) — the panel must not appear
           // even without the demo-organization short-circuit ever engaging, since this org is not
           // named "Demo Organization" and DemoOptions.Enabled is not set in this test host.
           // ADR-0020's actual demo-organization short-circuit (DemoOptions.Enabled + the seeded
           // "Demo Organization" name) is verified live against the real demo deployment instead —
           // see the final report's live-verification section.
    public async Task SharedTestOrganization_AlreadyConfigured_NeverSeesSetupPanel()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AdminEmail);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("setup__title", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdmin_DoesNotSeeSetupPanel()
    {
        var adminClient = RegisterAsync("Setup NonAdmin Owner", out var adminEmail);
        var agentEmail = $"{Guid.NewGuid():N}@setuppanel.test.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await db.Users.SingleAsync(u => u.Email == adminEmail);
            var membership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == admin.Id);

            var agent = new ApplicationUser
            {
                UserName = agentEmail,
                Email = agentEmail,
                EmailConfirmed = true,
                DisplayName = "Non-Admin Member",
                IsActive = true,
            };
            var createResult = await userManager.CreateAsync(agent, Password);
            Assert.True(createResult.Succeeded);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, membership.OrganizationId, agent.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var agentClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await TestAuthentication.AntiForgeryTokenAsync(agentClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", agentEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", token),
            ]),
        };
        var loginResponse = await agentClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var response = await agentClient.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("setup__title", html, StringComparison.Ordinal);
        _ = adminClient; // kept authenticated only to establish the organization; not otherwise used
    }

    [Fact] // Switching organizations changes which organization's setup state is shown — state is
           // derived per-request from the caller's *current* organization, never cached.
    public async Task SwitchingOrganizations_SetupStateFollowsTheSelectedOrganization()
    {
        var client = RegisterAsync("Setup Switch Admin", out var email);
        int firstOrganizationId, secondOrganizationId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Email == email);
            var firstMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == admin.Id);
            firstOrganizationId = firstMembership.OrganizationId;

            var secondOrganization = new Organization(0, $"Second Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Organizations.Add(secondOrganization);
            await db.SaveChangesAsync();
            secondOrganizationId = secondOrganization.Id;

            // Fully configured, so its own dashboard shows no setup panel.
            var team = new Team(0, secondOrganizationId, $"Second Org Team {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(team);
            await db.SaveChangesAsync();
            var category = new Category(0, team.Id, $"Second Org Category {Guid.NewGuid():N}", WorkType.Incident, DateTimeOffset.UtcNow);
            db.Add(category);
            db.Add(new Project(0, secondOrganizationId, $"Second Org Project {Guid.NewGuid():N}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            var secondMemberOfSecondOrg = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@setuppanel.test.local",
                Email = $"{Guid.NewGuid():N}@setuppanel.test.local",
                DisplayName = "Second Org Second Member",
                IsActive = true,
            };
            db.Users.Add(secondMemberOfSecondOrg);
            await db.SaveChangesAsync();
            db.OrganizationMemberships.Add(new OrganizationMembership(0, secondOrganizationId, admin.Id, UserRole.Admin, DateTimeOffset.UtcNow));
            db.OrganizationMemberships.Add(new OrganizationMembership(0, secondOrganizationId, secondMemberOfSecondOrg.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            var ticketService = new FlowOps.Application.Tickets.TicketService(db, TimeProvider.System, TestEmail.Sender, TestEmail.Options);
            await ticketService.CreateAsync(
                new FlowOps.Application.Tickets.CreateTicketRequest("Second org ticket", "A routine description.", WorkType.Incident, Priority.Medium, team.Id, category.Id, null),
                new CurrentUser(admin.Id, secondOrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>()));
        }

        var beforeSwitch = await client.GetAsync("/");
        Assert.Contains("setup__title", await beforeSwitch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var switchToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/");
        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, "/Organization/Switch")
        {
            Content = new FormUrlEncodedContent(
            [
                new("organizationId", secondOrganizationId.ToString()),
                new("__RequestVerificationToken", switchToken),
            ]),
        };
        var switchResponse = await client.SendAsync(switchRequest);
        Assert.Equal(HttpStatusCode.Redirect, switchResponse.StatusCode);

        var afterSwitch = await client.GetAsync("/");
        Assert.DoesNotContain("setup__title", await afterSwitch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        _ = firstOrganizationId;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = $"{Guid.NewGuid():N}@setuppanel.test.local";

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
