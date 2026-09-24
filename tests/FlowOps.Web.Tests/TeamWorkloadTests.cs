using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// ADR-0036: <c>/TeamWorkload/Index</c> over real HTTP — per-role rendering differences (Agent
/// sees no team table, only their own summary; Manager/Admin see their in-scope team; Viewer, who
/// is not a member of the seeded team, sees the honest empty state), the sidebar link's own
/// visibility, and that the summary/team-table drill-through links carry the correct route values.
/// </summary>
public sealed class TeamWorkloadTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TeamWorkloadTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SidebarLink_IsVisibleToEveryRole()
    {
        foreach (var email in new[] { TestUsers.AdminEmail, TestUsers.ManagerEmail, TestUsers.AgentEmail, TestUsers.ViewerEmail })
        {
            var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
            await TestAuthentication.SignInAsync(client, email);

            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Contains("href=\"/TeamWorkload\"", html, StringComparison.Ordinal);
            Assert.Contains("Team Workload", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manager_SeesOwnTeamsWorkloadTable()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync("/TeamWorkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Team workload", html, StringComparison.Ordinal);
        Assert.Contains(TestReferenceData.TeamName, html, StringComparison.Ordinal);
        Assert.Contains("View members", html, StringComparison.Ordinal);
    }

    [Fact] // Admin sees the seeded team's workload despite not being a member of it — AllTeams scope.
    public async Task Admin_SeesTeamEvenWithoutBeingAMember()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AdminEmail);

        var response = await client.GetAsync("/TeamWorkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(TestReferenceData.TeamName, html, StringComparison.Ordinal);
    }

    [Fact] // The one rule this whole feature exists to enforce, mirroring DashboardTests' own.
    public async Task Agent_SeesOwnWorkloadOnly_NeverATeamTable()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/TeamWorkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Your own current workload.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Team workload", html, StringComparison.Ordinal);
        Assert.DoesNotContain(TestReferenceData.TeamName, html, StringComparison.Ordinal);
    }

    [Fact] // Viewer is not a member of the seeded team, so their in-scope team set is genuinely
           // empty — an honest empty state, not a crash or a leaked row.
    public async Task Viewer_NotAMemberOfAnyTeam_SeesHonestEmptyState()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var response = await client.GetAsync("/TeamWorkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("No open work in your scope right now.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(TestReferenceData.TeamName, html, StringComparison.Ordinal);
    }

    [Fact] // Drill-through route values, not just the label — the same discipline DashboardTests
           // applies to its own KPI links.
    public async Task TeamRow_DrillThroughLinks_CarryTheTeamId()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync("/TeamWorkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"teamId={_factory.TeamId}", html, StringComparison.Ordinal);

        // The team's own Open-count link is genuinely actionable: it resolves to a working,
        // narrowed Work Queue.
        var drillThroughResponse = await client.GetAsync($"/Tickets?filter=OpenWork&teamId={_factory.TeamId}");
        Assert.Equal(HttpStatusCode.OK, drillThroughResponse.StatusCode);
        var drillThroughHtml = await drillThroughResponse.Content.ReadAsStringAsync();
        Assert.Contains("Filtered to:", drillThroughHtml, StringComparison.Ordinal);
    }

    [Fact] // Expanding a team is a real, bookmarkable server round trip (?expandedTeam=), not a
           // client-side toggle — proven by requesting that exact query string directly.
    public async Task ExpandingATeam_RendersItsMemberWorkloadTable()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync($"/TeamWorkload?expandedTeam={_factory.TeamId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("current workload by member", html, StringComparison.Ordinal);
        Assert.Contains("Hide members", html, StringComparison.Ordinal);
    }

    [Fact] // A Manager expanding a team they do not manage must not leak that team's member data —
           // the PageModel's own TicketAccessDeniedException catch, exercised over real HTTP.
    public async Task ExpandingATeamNotManaged_DoesNotLeakMemberData()
    {
        int otherTeamId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOps.Infrastructure.Persistence.FlowOpsDbContext>();
            var otherTeam = new FlowOps.Domain.Directory.Team(0, _factory.OrganizationId, $"Unmanaged-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(otherTeam);
            await db.SaveChangesAsync();
            otherTeamId = otherTeam.Id;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync($"/TeamWorkload?expandedTeam={otherTeamId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("current workload by member", html, StringComparison.Ordinal);
    }
}
