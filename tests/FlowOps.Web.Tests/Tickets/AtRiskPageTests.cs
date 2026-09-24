using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 8 at-risk queue over real HTTP: ranked tickets with their signals spelled out, scoped to
/// what the caller may already view, behind the same authentication gate as every other page.
/// </summary>
/// <remarks>Two sign-ins, inside the five-per-minute login rate limit (CLAUDE.md §12).</remarks>
public sealed class AtRiskPageTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public AtRiskPageTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AtRisk_ShowsRankedTicketsWithTheirSignals()
    {
        // Unassigned, Critical, and old enough to trip UnassignedUrgent and breach its SLA.
        var ticketId = await _factory.CreateAtRiskTicketAsync("Payroll system is offline");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/Tickets/AtRisk");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("At-risk work", html, StringComparison.Ordinal);
        Assert.Contains("Payroll system is offline", html, StringComparison.Ordinal);
        // Phase 11: this now lives in the page's subtitle sentence rather than a standalone
        // table caption, so it is no longer sentence-initial.
        Assert.Contains("ranked most urgent first", html, StringComparison.OrdinalIgnoreCase);

        // Signals are rendered as words, in the row's metadata line and the ranked severity mark —
        // severity is never conveyed by colour alone (§22).
        Assert.Contains("Critical", html, StringComparison.Ordinal);
        Assert.Contains("unassigned", html, StringComparison.OrdinalIgnoreCase);

        // And the row links through to the ticket it is about.
        Assert.Contains($"/Tickets/Details/{ticketId}", html, StringComparison.Ordinal);
    }

    [Fact] // Phase 30 (ADR-0035): the attention brief disclosure — evidence and a suggested next step.
    public async Task AtRisk_ShowsAttentionBriefDisclosure()
    {
        await _factory.CreateAtRiskTicketAsync("Core switch stack is down");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/Tickets/AtRisk");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("View attention brief", html, StringComparison.Ordinal);
        // Assign an owner is the UnassignedUrgent suggestion — the ticket is unassigned & Critical.
        Assert.Contains("Assign an owner.", html, StringComparison.Ordinal);
    }

    [Fact] // A caller outside the ticket's team is told nothing about it.
    public async Task AtRisk_OutOfTeamCaller_SeesNothing()
    {
        await _factory.CreateAtRiskTicketAsync("Datacentre UPS alarm");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var response = await client.GetAsync("/Tickets/AtRisk");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("No work is at risk right now", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Datacentre UPS alarm", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Why it needs attention", html, StringComparison.Ordinal);
    }
}

/// <summary>
/// The at-risk page sits behind the same authentication gate as every other ticket page. Uses the
/// plain factory with no container, like the other anonymous tests: the cookie challenge happens
/// in middleware before any query runs.
/// </summary>
public sealed class AtRiskAnonymousAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AtRiskAnonymousAccessTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(_ => { });

    [Fact]
    public async Task AtRisk_Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Tickets/AtRisk");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }
}
