using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Platform;

/// <summary>Phase 24 (ADR-0023): <c>/Platform/Organizations</c> over real HTTP — most importantly,
/// the live consequence of deactivation: an ordinary tenant Admin genuinely loses the ability to
/// operate their own organization, and historical data survives the round trip.</summary>
public sealed class PlatformOrganizationsTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PlatformOrganizationsTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PlatformAdmin_CanListAndInspectAnOrganization()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant1", out _);
        var teamId = await CreateTeamAsync(tenantClient);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);
        var platformClient = RegisterAsync("PlatformOrg Admin1", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var listResponse = await platformClient.GetAsync("/Platform/Organizations/Index");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var detailResponse = await platformClient.GetAsync($"/Platform/Organizations/Details/{organizationId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var html = await detailResponse.Content.ReadAsStringAsync();
        Assert.Contains("Active", html, StringComparison.Ordinal);
    }

    [Fact] // The most important live scenario: deactivation actually stops tenant operation, and
           // reactivation actually restores it, with zero data loss either way.
    public async Task DeactivateThenReactivate_TenantOperationStopsAndResumes_NoDataLoss()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant2", out var tenantEmail);
        var teamId = await CreateTeamAsync(tenantClient);
        var categoryId = await GetCategoryIdForTeamAsync(teamId);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);
        var ticketId = await CreateTicketAsync(tenantClient, categoryId);

        var platformClient = RegisterAsync("PlatformOrg Admin2", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}");
        var deactivateToken = ExtractAntiForgeryToken(detailHtml);
        var deactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deactivateToken)]),
        });
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        // The tenant's own dashboard is now unreachable as an authorized page — CurrentUserAccessor
        // no longer resolves any membership into the deactivated organization.
        var tenantDashboard = await tenantClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, tenantDashboard.StatusCode);
        Assert.Contains("/Account/AccessDenied", tenantDashboard.Headers.Location?.ToString());

        // Historical data is fully intact — verified directly against the database, which is the
        // authoritative place to check "nothing was deleted."
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId);
            Assert.False(organization.IsActive);
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
            Assert.Equal(teamId, ticket.TeamId);
            var membershipCount = await db.OrganizationMemberships.CountAsync(m => m.OrganizationId == organizationId);
            Assert.True(membershipCount > 0);
        }

        var reactivateDetailHtml = await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}");
        var reactivateToken = ExtractAntiForgeryToken(reactivateDetailHtml);
        var reactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Reactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", reactivateToken)]),
        });
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);

        var tenantDashboardAfter = await tenantClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, tenantDashboardAfter.StatusCode);
    }

    [Fact] // Deactivating an inactive organization (or reactivating an active one) fails safely.
    public async Task DeactivateAlreadyInactiveOrganization_FailsSafely()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant3", out _);
        var teamId = await CreateTeamAsync(tenantClient);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);
        var platformClient = RegisterAsync("PlatformOrg Admin3", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var token1 = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}"));
        await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token1)]),
        });

        var html2 = await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}");
        var token2 = ExtractAntiForgeryToken(html2);
        var secondResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token2)]),
        });

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var body = await secondResponse.Content.ReadAsStringAsync();
        Assert.Contains("already inactive", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForgedOrganizationId_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformOrg Admin4", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var response = await platformClient.GetAsync("/Platform/Organizations/Details/999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanRenameAnOrganization()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant6", out _);
        var teamId = await CreateTeamAsync(tenantClient);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);
        var platformClient = RegisterAsync("PlatformOrg Admin6", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var detailHtml = await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}");
        var token = ExtractAntiForgeryToken(detailHtml);
        var renameResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Rename")
        {
            Content = new FormUrlEncodedContent([new("name", "Renamed Organization"), new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var body = await renameResponse.Content.ReadAsStringAsync();
        Assert.Contains("Renamed Organization", body, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId);
        Assert.Equal("Renamed Organization", organization.Name);
    }

    [Fact]
    public async Task OrganizationDetail_RendersPendingInvitationsAndRecentActivity()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant7", out _);
        var teamId = await CreateTeamAsync(tenantClient);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);

        var inviteHtml = await GetHtmlAsync(tenantClient, "/Organization/Members");
        var inviteToken = ExtractAntiForgeryToken(inviteHtml);
        var invitedEmail = $"{Guid.NewGuid():N}@platformorgtest.local";
        var inviteResponse = await tenantClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=Invite")
        {
            Content = new FormUrlEncodedContent(
            [
                new("InviteInput.Email", invitedEmail),
                new("InviteInput.Role", "Agent"),
                new("__RequestVerificationToken", inviteToken),
            ]),
        });
        Assert.Equal(HttpStatusCode.OK, inviteResponse.StatusCode);

        var platformClient = RegisterAsync("PlatformOrg Admin7", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        var detailToken = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, $"/Platform/Organizations/Details/{organizationId}"));
        var deactivateResponse = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", detailToken)]),
        });
        var html = await deactivateResponse.Content.ReadAsStringAsync();

        Assert.Contains(invitedEmail, html, StringComparison.Ordinal);
        Assert.Contains("OrganizationDeactivated", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedOrganizationId_Rename_ReturnsNotFound()
    {
        var platformClient = RegisterAsync("PlatformOrg Admin8", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);
        var token = ExtractAntiForgeryToken(await GetHtmlAsync(platformClient, "/Platform/Organizations/Index"));

        var response = await platformClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Organizations/Details/999999999?handler=Rename")
        {
            Content = new FormUrlEncodedContent([new("name", "New Name"), new("__RequestVerificationToken", token)]),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_MissingAntiforgeryToken_IsRejected()
    {
        var tenantClient = RegisterAsync("PlatformOrg Tenant5", out _);
        var teamId = await CreateTeamAsync(tenantClient);
        var organizationId = await GetOrganizationIdForTeamAsync(teamId);
        var platformClient = RegisterAsync("PlatformOrg Admin5", out var platformEmail);
        await GrantPlatformAdminAsync(platformEmail);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Platform/Organizations/Details/{organizationId}?handler=Deactivate");
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

    private async Task<int> GetOrganizationIdForTeamAsync(int teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Teams.Where(t => t.Id == teamId).Select(t => t.OrganizationId).SingleAsync();
    }

    private async Task<int> GetCategoryIdForTeamAsync(int teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Categories.Where(c => c.TeamId == teamId).Select(c => c.Id).SingleAsync();
    }

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

    private async Task<int> CreateTicketAsync(HttpClient client, int categoryId)
    {
        var createHtml = await GetHtmlAsync(client, "/Tickets/Create");
        var token = ExtractAntiForgeryToken(createHtml);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Title", "Server room AC failure"),
                new("Input.Description", "The server room AC unit has failed."),
                new("Input.WorkType", "Incident"),
                new("Input.Priority", "High"),
                new("Input.CategoryId", categoryId.ToString()),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        return int.Parse(location.Split('/').Last());
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
        var capturedEmail = $"{Guid.NewGuid():N}@platformorgtest.local";

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
