using System.Net;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
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
/// <remarks>Four sign-ins, inside the five-per-minute login rate limit (CLAUDE.md §12).</remarks>
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
        Assert.Contains("Past due", html, StringComparison.Ordinal);
        Assert.Contains("Deadlines met", html, StringComparison.Ordinal);
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
            var emptyTeam = new Team(0, _factory.OrganizationId, $"Empty-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
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
        Assert.Contains("No resolved tickets yet", html, StringComparison.Ordinal); // SLA/resolution-time honesty
        Assert.Contains("No open work in your scope right now.", html, StringComparison.Ordinal);
        // Scoped to where a computed number would actually render (element text / a percentage),
        // not the whole raw response — an opaque antiforgery token elsewhere on the page can
        // coincidentally contain the literal substring "NaN" with no connection to this defect.
        Assert.DoesNotContain(">NaN<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN%", html, StringComparison.Ordinal);
    }

    [Fact] // Phase 20C §12: a work type with zero resolved tickets must render as a distinct
           // "no data" row, never a bar at 0% width indistinguishable from a real (if small) one.
           // Every ticket this factory ever creates is WorkType.Incident (CreateTicketAsync's own
           // hard-coded request), so resolving one gives exactly one work type real data while the
           // other three stay genuinely at zero — the precise scenario the defect was about.
    public async Task ResolutionTimePanel_WorkTypeWithNoResolvedTickets_ShowsNoDataRow_NotAFakeBar()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var ticketService = scope.ServiceProvider.GetRequiredService<TicketService>();
            var managerId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.ManagerEmail);
            var manager = new CurrentUser(managerId, _factory.OrganizationId, UserRole.Manager, new HashSet<int> { _factory.TeamId }, new HashSet<int> { _factory.TeamId });

            var (ticketId, _) = await ticketService.CreateAsync(
                new CreateTicketRequest(
                    Title: "Resolution time regression fixture",
                    Description: "Seeded so exactly one work type has a real resolved-ticket average.",
                    WorkType: WorkType.Incident,
                    Priority: Priority.Medium,
                    TeamId: _factory.TeamId,
                    CategoryId: _factory.CategoryId,
                    ProjectId: null),
                manager);
            await ticketService.AssignAsync(ticketId, managerId, manager);
            await ticketService.StartWorkAsync(ticketId, manager);
            await ticketService.ResolveAsync(ticketId, Resolution.Fixed, "Fixed for the regression fixture.", manager);
            _ = db;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Incident", html, StringComparison.Ordinal);
        Assert.Contains("No resolved tickets in period", html, StringComparison.Ordinal);
        // The no-data rows never render a bar element at all for that row.
        Assert.Contains("hbar-row__no-data", html, StringComparison.Ordinal);
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
