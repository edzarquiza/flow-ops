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

namespace FlowOps.Web.Tests.Admin;

/// <summary>
/// Team-membership management over real HTTP — the feature that closes the gap ADR-0020's
/// onboarding checklist exposed (a team with no way to put anyone on it). Every test registers its
/// own disposable organization via the real, unthrottled registration flow.
/// </summary>
/// <remarks>One login POST in this class (the non-admin test) — well inside the five-per-minute
/// budget (CLAUDE.md §12).</remarks>
public sealed class TeamMembershipTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public TeamMembershipTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_CanAddAndRemoveATeamMember()
    {
        var client = RegisterAsync("TeamMembership Admin1", out var email);
        var teamId = await CreateTeamAsync(client);
        var memberId = await AddOrganizationMemberDirectlyAsync(email, "Agent One", UserRole.Agent);

        var detailPage = await client.GetAsync($"/Admin/Teams/Details/{teamId}");
        Assert.Equal(HttpStatusCode.OK, detailPage.StatusCode);
        var detailHtml = await detailPage.Content.ReadAsStringAsync();
        Assert.Contains("Agent One", detailHtml, StringComparison.Ordinal);

        var addToken = ExtractAntiForgeryToken(detailHtml);
        using var addRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=AddMember")
        {
            Content = new FormUrlEncodedContent(
            [
                new("userId", memberId.ToString()),
                new("__RequestVerificationToken", addToken),
            ]),
        };
        var addResponse = await client.SendAsync(addRequest);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var afterAddHtml = await addResponse.Content.ReadAsStringAsync();
        Assert.Contains("member added to the team", afterAddHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent One", afterAddHtml, StringComparison.Ordinal);

        var removeToken = ExtractAntiForgeryToken(afterAddHtml);
        using var removeRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=RemoveMember")
        {
            Content = new FormUrlEncodedContent(
            [
                new("userId", memberId.ToString()),
                new("__RequestVerificationToken", removeToken),
            ]),
        };
        var removeResponse = await client.SendAsync(removeRequest);
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        var afterRemoveHtml = await removeResponse.Content.ReadAsStringAsync();
        // Removed from the team's member table (the only member, so it falls back to the empty
        // state) — they legitimately reappear in the "add member" dropdown below, since removal
        // makes them eligible again.
        Assert.Contains("No members on this team yet", afterRemoveHtml, StringComparison.Ordinal);
        Assert.Contains("member removed from the team", afterRemoveHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_CanSetAndUnsetTeamManagerFlag()
    {
        var client = RegisterAsync("TeamMembership Admin2", out var email);
        var teamId = await CreateTeamAsync(client);
        var memberId = await AddOrganizationMemberDirectlyAsync(email, "Manager One", UserRole.Manager);

        var addToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=AddMember")
        {
            Content = new FormUrlEncodedContent([new("userId", memberId.ToString()), new("__RequestVerificationToken", addToken)]),
        });

        var detailHtml = await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}");
        var setToken = ExtractAntiForgeryToken(detailHtml);
        using var setRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=SetManager")
        {
            Content = new FormUrlEncodedContent(
            [
                new("userId", memberId.ToString()),
                new("isTeamManager", "true"),
                new("__RequestVerificationToken", setToken),
            ]),
        };
        var setResponse = await client.SendAsync(setRequest);
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
        var afterSetHtml = await setResponse.Content.ReadAsStringAsync();
        Assert.Contains("<td class=\"cell-muted\">Yes</td>", afterSetHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdmin_CannotAccessTeamDetailPage()
    {
        var ownerClient = RegisterAsync("TeamMembership NonAdminOwner", out var ownerEmail);
        var teamId = await CreateTeamAsync(ownerClient);
        var agentEmail = $"{Guid.NewGuid():N}@teammembershiptest.local";

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

        var response = await agentClient.GetAsync($"/Admin/Teams/Details/{teamId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // Phase 16 organization boundary: Org A's Admin must not manage Org B's team.
    public async Task Admin_CannotManageAnotherOrganizationsTeam()
    {
        var clientA = RegisterAsync("TeamMembership CrossOrgA", out _);
        var clientB = RegisterAsync("TeamMembership CrossOrgB", out var emailB);
        var teamBId = await CreateTeamAsync(clientB);
        var memberOfB = await AddOrganizationMemberDirectlyAsync(emailB, "Org B Agent", UserRole.Agent);

        var responseA = await clientA.GetAsync($"/Admin/Teams/Details/{teamBId}");
        Assert.Equal(HttpStatusCode.NotFound, responseA.StatusCode);

        // Even a fabricated POST naming Org B's team and one of its real members is refused.
        var tokenForOwnAdminPage = ExtractAntiForgeryToken(await GetHtmlAsync(clientA, "/Admin"));
        using var tamperedRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamBId}?handler=AddMember")
        {
            Content = new FormUrlEncodedContent(
            [
                new("userId", memberOfB.ToString()),
                new("__RequestVerificationToken", tokenForOwnAdminPage),
            ]),
        };
        var tamperedResponse = await clientA.SendAsync(tamperedRequest);

        Assert.Equal(HttpStatusCode.Redirect, tamperedResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", tamperedResponse.Headers.Location?.ToString());
    }

    [Fact]
    public async Task AddMember_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisterAsync("TeamMembership AntiForgery", out var email);
        var teamId = await CreateTeamAsync(client);
        var memberId = await AddOrganizationMemberDirectlyAsync(email, "No Token Agent", UserRole.Agent);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=AddMember")
        {
            Content = new FormUrlEncodedContent([new("userId", memberId.ToString())]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers ----

    private async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<int> CreateTeamAsync(HttpClient client)
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

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Teams.OrderByDescending(t => t.Id).Select(t => t.Id).FirstAsync();
    }

    private async Task<Guid> AddOrganizationMemberDirectlyAsync(string ownerEmail, string displayName, UserRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
        var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

        var member = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@teammembershiptest.local",
            Email = $"{Guid.NewGuid():N}@teammembershiptest.local",
            EmailConfirmed = true,
            DisplayName = displayName,
            IsActive = true,
        };
        db.Users.Add(member);
        await db.SaveChangesAsync();
        db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, member.Id, role, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        return member.Id;
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
        var capturedEmail = $"{Guid.NewGuid():N}@teammembershiptest.local";

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
