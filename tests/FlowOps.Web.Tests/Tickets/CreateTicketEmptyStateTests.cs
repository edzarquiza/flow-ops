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

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 22 (PG-3): Create Ticket's empty state for an organization that isn't ready for tickets
/// yet — zero teams, or a team with zero categories — instead of a silently empty Category
/// dropdown. Every test registers its own disposable organization via the real, unthrottled
/// registration flow.
/// </summary>
public sealed class CreateTicketEmptyStateTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public CreateTicketEmptyStateTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_ZeroTeams_SeesEmptyStateWithSetUpAction()
    {
        var client = RegisterAsync("EmptyState ZeroTeams", out _);

        var response = await client.GetAsync("/Tickets/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("isn't ready for tickets yet", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Select a category", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_TeamButZeroCategories_SeesEmptyState()
    {
        var client = RegisterAsync("EmptyState ZeroCategories", out var email);

        // A team can only be created together with a category through the existing UI, so this
        // exercises the same empty state via direct persistence — a team that later loses its only
        // category to deactivation lands in exactly this state.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Email == email);
            var membership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == admin.Id);
            var team = new FlowOps.Domain.Directory.Team(0, membership.OrganizationId, "Empty Team", DateTimeOffset.UtcNow);
            db.Teams.Add(team);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/Tickets/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("isn't ready for tickets yet", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_ConfiguredOrganization_SeesNormalForm()
    {
        var client = RegisterAsync("EmptyState Configured", out _);
        await CreateTeamAsync(client);

        var response = await client.GetAsync("/Tickets/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("isn't ready for tickets yet", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Select a category", html, StringComparison.Ordinal);
    }

    [Fact] // A non-Admin with an unconfigured organization sees the same empty state, but never a
           // "Set up" action they cannot actually use (they cannot reach /Admin).
    public async Task NonAdmin_ZeroTeams_SeesEmptyStateWithoutASetUpAction()
    {
        var ownerClient = RegisterAsync("EmptyState NonAdminOwner", out var ownerEmail);
        var agentEmail = $"{Guid.NewGuid():N}@emptystatetest.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
            var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

            var agent = new ApplicationUser
            {
                UserName = agentEmail,
                Email = agentEmail,
                EmailConfirmed = true,
                DisplayName = "Non-Admin Agent",
                IsActive = true,
            };
            var createResult = await userManager.CreateAsync(agent, Password);
            Assert.True(createResult.Succeeded);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, agent.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var agentClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(agentClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", agentEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await agentClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var response = await agentClient.GetAsync("/Tickets/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("isn't ready for tickets yet", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ask an organization Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Set up a team", html, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private async Task CreateTeamAsync(HttpClient client)
    {
        var adminHtml = await GetHtmlAsync(client, "/Admin");
        var token = ExtractAntiForgeryToken(adminHtml);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=CreateTeam")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.TeamName", $"Team {Guid.NewGuid():N}"),
                new("Input.CategoryName", $"Category {Guid.NewGuid():N}"),
                new("Input.CategoryWorkType", "0"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        var capturedEmail = $"{Guid.NewGuid():N}@emptystatetest.local";

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
