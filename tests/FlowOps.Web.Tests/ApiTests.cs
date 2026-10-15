using System.Net;
using System.Text.Json;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// ADR-0037: the read-only <c>/api/v1</c> JSON API. The API adds no rules of its own, so these tests
/// pin the three things that could still go wrong at this boundary: anonymous callers get a 401 (not
/// a redirect), every role sees only what its existing service allows, and — the one that matters
/// most — one organization can never read another's tickets through it.
/// </summary>
public sealed class ApiTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public ApiTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email);
        return client;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [Theory]
    [InlineData("/api/v1/tickets")]
    [InlineData("/api/v1/tickets/1")]
    [InlineData("/api/v1/attention")]
    [InlineData("/api/v1/workload")]
    public async Task AnonymousCaller_Gets401_NotARedirectToTheLoginPage(string path)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact] // The cookie events must only change /api — every page keeps its normal redirect.
    public async Task AnonymousCaller_OnAnOrdinaryPage_StillRedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Tickets");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_ListsTickets_AsPagedJson_WithEnumsAsNames()
    {
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync("/api/v1/tickets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = await JsonAsync(response);
        var root = json.RootElement;
        Assert.True(root.GetProperty("totalCount").GetInt32() >= 1);
        Assert.Equal(1, root.GetProperty("pageNumber").GetInt32());

        var seeded = root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == _factory.SeededTicketId);
        Assert.Equal("High", seeded.GetProperty("priority").GetString());
        Assert.Equal("Incident", seeded.GetProperty("workType").GetString());
        Assert.Equal(TestReferenceData.TeamName, seeded.GetProperty("teamName").GetString());
    }

    [Fact]
    public async Task Admin_ReadsOneTicket_ByIdentifier()
    {
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync($"/api/v1/tickets/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await JsonAsync(response);
        Assert.Equal("VPN client will not connect", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnknownTicket_Is404()
    {
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync("/api/v1/tickets/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidFilterValue_Is400()
    {
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync("/api/v1/tickets?status=not-a-status");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // A Viewer who is not on the team sees an honest empty list, not an error and not other teams' work.
    public async Task Viewer_NotOnTheTeam_SeesNoTickets()
    {
        var client = await SignedInAsync(TestUsers.ViewerEmail);

        var list = await client.GetAsync("/api/v1/tickets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = await JsonAsync(list);
        Assert.Equal(0, listJson.RootElement.GetProperty("totalCount").GetInt32());

        var detail = await client.GetAsync($"/api/v1/tickets/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact] // The same rule the Team Workload page enforces: an Agent sees their own workload only.
    public async Task Workload_Agent_IsOwnWorkloadOnly_WithNoTeamRows()
    {
        var client = await SignedInAsync(TestUsers.AgentEmail);

        var response = await client.GetAsync("/api/v1/workload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await JsonAsync(response);
        Assert.True(json.RootElement.GetProperty("ownWorkloadOnly").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("teams").EnumerateArray());
    }

    [Fact]
    public async Task Workload_Admin_SeesTheTeamTable()
    {
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync("/api/v1/workload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await JsonAsync(response);
        Assert.False(json.RootElement.GetProperty("ownWorkloadOnly").GetBoolean());
        Assert.Contains(
            json.RootElement.GetProperty("teams").EnumerateArray(),
            team => team.GetProperty("teamName").GetString() == TestReferenceData.TeamName);
    }

    [Fact]
    public async Task Attention_ReturnsTheAtRiskList_WithItsSignals()
    {
        var title = $"Overdue api ticket {Guid.NewGuid():N}";
        var ticketId = await _factory.CreateAtRiskTicketAsync(title);
        var client = await SignedInAsync(TestUsers.AdminEmail);

        var response = await client.GetAsync("/api/v1/attention");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await JsonAsync(response);
        var item = json.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == ticketId);
        Assert.NotEmpty(item.GetProperty("signals").EnumerateArray());
    }

    // ---- Tenant isolation ---------------------------------------------------------------------

    [Fact] // The boundary that matters most: another organization's work is invisible through every endpoint.
    public async Task AnotherOrganizationsTickets_AreNeverReturned()
    {
        var foreign = await SeedForeignOrganizationAsync();
        var admin = await SignedInAsync(TestUsers.AdminEmail);

        // Positive control: this caller can read their own organization's ticket, so the checks
        // below are not passing merely because the API returns nothing at all.
        var own = await admin.GetAsync($"/api/v1/tickets/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var detail = await admin.GetAsync($"/api/v1/tickets/{foreign.TicketId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var list = await admin.GetAsync("/api/v1/tickets?includeFinished=true");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(foreign.Title, listBody, StringComparison.Ordinal);

        var attention = await admin.GetAsync("/api/v1/attention");
        Assert.DoesNotContain(foreign.Title, await attention.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var workload = await admin.GetAsync("/api/v1/workload");
        Assert.DoesNotContain(foreign.TeamName, await workload.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // And the other direction: the foreign organization's own user cannot see ours.
        var outsider = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(outsider, foreign.Email);
        var crossRead = await outsider.GetAsync($"/api/v1/tickets/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
        var ownRead = await outsider.GetAsync($"/api/v1/tickets/{foreign.TicketId}");
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
    }

    // ---- Documentation ------------------------------------------------------------------------

    [Fact]
    public async Task OpenApiDocument_DescribesTheFourEndpoints_AndNothingElse()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await JsonAsync(response);
        var paths = json.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(["/api/v1/attention", "/api/v1/tickets", "/api/v1/tickets/{id}", "/api/v1/workload"], paths);
    }

    [Fact] // Swagger UI needs inline script/style; that loosening must stay scoped to /swagger alone.
    public async Task SwaggerUi_IsServed_WithItsOwnCsp_AndEveryOtherPageKeepsTheStrictOne()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var swagger = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Contains("'unsafe-inline'", swagger.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        var login = await client.GetAsync("/Account/Login");
        Assert.DoesNotContain("'unsafe-inline'", login.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        var api = await client.GetAsync("/api/v1/tickets");
        Assert.DoesNotContain("'unsafe-inline'", api.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private sealed record ForeignOrganization(string Email, int TicketId, string Title, string TeamName);

    /// <summary>A second, fully separate organization: its own team, category, member and ticket.</summary>
    private async Task<ForeignOrganization> SeedForeignOrganizationAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"outsider-{suffix}@other.flowops.local";
        var teamName = $"Foreign Team {suffix}";
        var title = $"Foreign ticket {suffix}";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var organization = new Organization(0, $"Other Org {suffix}", DateTimeOffset.UtcNow);
        db.Add(organization);
        await db.SaveChangesAsync();

        var team = new Team(0, organization.Id, teamName, DateTimeOffset.UtcNow);
        db.Add(team);
        await db.SaveChangesAsync();

        var category = new Category(0, team.Id, "Foreign Category", WorkType.Incident, DateTimeOffset.UtcNow);
        db.Add(category);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            IsActive = true,
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };
        var created = await userManager.CreateAsync(user, TestUsers.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        db.Add(new OrganizationMembership(0, organization.Id, user.Id, UserRole.Admin, DateTimeOffset.UtcNow));
        db.Add(new TeamMember(team.Id, user.Id, isTeamManager: false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var ticketService = scope.ServiceProvider.GetRequiredService<TicketService>();
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest(title, "Belongs to a different organization.", WorkType.Incident, Priority.Critical, team.Id, category.Id, null),
            new CurrentUser(user.Id, organization.Id, UserRole.Admin, new HashSet<int> { team.Id }, new HashSet<int>()));

        return new ForeignOrganization(email, ticketId, title, teamName);
    }
}
