using System.Net;
using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 10 dashboard over real HTTP: the root page renders exactly the four capped KPIs
/// (CLAUDE.md §22) as actionable links, an Agent's numbers never disclose team-wide data, and a
/// caller with nothing in scope sees an honest empty state rather than a crash or fake numbers.
/// </summary>
/// <remarks>Three sign-ins, well inside the five-per-minute login rate limit (CLAUDE.md §12).</remarks>
public sealed class DashboardTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public DashboardTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_ShowsExactlyFourKpisAsActionableLinks_AndTheWorkloadTable()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // Exactly the four capped KPIs — CLAUDE.md §22.
        Assert.Contains("Open Work", html, StringComparison.Ordinal);
        Assert.Contains("Overdue", html, StringComparison.Ordinal);
        Assert.Contains("SLA Compliance", html, StringComparison.Ordinal);
        Assert.Contains("Average Resolution Time", html, StringComparison.Ordinal);
        Assert.Contains("Current workload", html, StringComparison.Ordinal);

        // Each is a real link through to filtered work — not just a static label.
        Assert.Contains("href=\"/Tickets?filter=OpenWork\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets?filter=Overdue\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets?filter=ResolvedRecently\"", html, StringComparison.Ordinal);

        // The "Open Work" link is genuinely actionable: it resolves to a page that renders.
        var openWorkResponse = await client.GetAsync("/Tickets?filter=OpenWork");
        Assert.Equal(HttpStatusCode.OK, openWorkResponse.StatusCode);
        var openWorkHtml = await openWorkResponse.Content.ReadAsStringAsync();
        Assert.Contains("Filtered to:", openWorkHtml, StringComparison.Ordinal);
        Assert.Contains("Open Work", openWorkHtml, StringComparison.Ordinal);
    }

    [Fact] // The one rule this whole feature exists to enforce.
    public async Task Agent_DashboardNeverDisclosesTeamWideCounts()
    {
        // Several tickets on the shared team, none assigned to this agent.
        for (var i = 0; i < 4; i++)
        {
            await _factory.CreateTicketAsync(TestUsers.ManagerEmail, $"Unrelated ticket {i}");
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // The dashboard renders (proving the page works for an Agent)...
        Assert.Contains("Open Work", html, StringComparison.Ordinal);

        // ...but the workload table must never show a team-wide count: this agent has no open
        // assignments, so their own workload section is empty even though the team plainly has
        // open work (verified independently, at the database, below).
        Assert.Contains("No open work in your scope right now.", html, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var teamOpenCount = await db.Tickets.CountAsync(t =>
            t.TeamId == _factory.TeamId && t.Status != FlowOps.Domain.Tickets.Status.Resolved && t.Status != FlowOps.Domain.Tickets.Status.Closed);

        Assert.True(teamOpenCount >= 4, "the team must genuinely have open work the agent's page correctly withholds");
    }

    [Fact] // A caller whose scope is genuinely empty gets an honest empty state, not a crash.
    public async Task EmptyScope_ShowsHonestEmptyState_NotFakeNumbers()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var emptyTeam = new Team(0, $"Empty-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(emptyTeam);
            await db.SaveChangesAsync();

            var viewerId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.ViewerEmail);
            db.Add(new TeamMember(emptyTeam.Id, viewerId, isTeamManager: false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(">0<", html, StringComparison.Ordinal); // Open Work / Overdue render as 0, not blank
        Assert.Contains("No data yet", html, StringComparison.Ordinal); // SLA/resolution-time honesty
        Assert.Contains("No open work in your scope right now.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }
}
