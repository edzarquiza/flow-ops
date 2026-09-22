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
        // Phase 11: the link label folds the total count in directly ("View all N at-risk items")
        // rather than repeating it in a separate sentence above the button.
        Assert.Contains("at-risk item", html, StringComparison.Ordinal);
        Assert.Contains($"/Tickets/Details/{ticketId}", html, StringComparison.Ordinal);

        // Phase 29B: the KPI summary comes first (visible without scrolling), then the attention list.
        var attentionIndex = html.IndexOf("What needs attention", StringComparison.Ordinal);
        var summaryIndex = html.IndexOf(">Summary<", StringComparison.Ordinal);
        Assert.True(attentionIndex >= 0, "the at-risk section heading must render");
        Assert.True(summaryIndex >= 0, "the KPI summary section must still render");
        Assert.True(summaryIndex < attentionIndex, "the KPI summary must render before the at-risk preview");
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
