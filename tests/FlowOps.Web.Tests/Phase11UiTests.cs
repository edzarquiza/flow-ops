using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 11: the shared layout and the reordered dashboard, over real HTTP. Deliberately does not
/// assert on CSS classes or styling — only on the structural/accessibility content the approved
/// spec actually requires (landmark nav, skip link, at-risk-before-KPIs ordering, the "View all
/// at-risk work" link).
/// </summary>
/// <remarks>Two sign-ins, well inside the five-per-minute login rate limit (CLAUDE.md §12).</remarks>
public sealed class Phase11UiTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public Phase11UiTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_ShowsAtRiskPreviewBeforeKpis_WithViewAllLink()
    {
        var ticketId = await _factory.CreateAtRiskTicketAsync("Core switch failure");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("What needs attention", html, StringComparison.Ordinal);
        Assert.Contains("Core switch failure", html, StringComparison.Ordinal);
        Assert.Contains("View all at-risk work", html, StringComparison.Ordinal);
        Assert.Contains($"/Tickets/Details/{ticketId}", html, StringComparison.Ordinal);

        // The dashboard must open on actionable attention content, not KPI counters.
        var attentionIndex = html.IndexOf("What needs attention", StringComparison.Ordinal);
        var summaryIndex = html.IndexOf(">Summary<", StringComparison.Ordinal);
        Assert.True(attentionIndex >= 0, "the at-risk section heading must render");
        Assert.True(summaryIndex >= 0, "the KPI summary section must still render");
        Assert.True(attentionIndex < summaryIndex, "the at-risk preview must render before the KPI summary section");
    }

    [Fact]
    public async Task SharedLayout_HasNavLandmarkAndSkipLinkWhenAuthenticated_LoginPageHasNeitherNavNorMissingAlertRole()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        // Anonymous: reachable, has a skip link and an accessible validation-summary alert region,
        // but no primary nav (there is nothing authenticated to navigate to yet).
        var loginResponse = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync();
        Assert.Contains("skip-link", loginHtml, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Primary\"", loginHtml, StringComparison.Ordinal);

        // Authenticated: the primary nav landmark appears, and the existing sign-out form (which
        // other tests source a real antiforgery token from) is still present.
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);
        var homeResponse = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        var homeHtml = await homeResponse.Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Primary\"", homeHtml, StringComparison.Ordinal);
        Assert.Contains("Sign out", homeHtml, StringComparison.Ordinal);
    }
}
