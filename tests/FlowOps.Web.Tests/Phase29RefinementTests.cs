using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 29B: the layout contracts worth keeping (there is no browser test in the suite, so the
/// stylesheet rules that fixed real defects are pinned here) and the rendering changes that are
/// observable over HTTP: Dashboard order and attention limit, user-facing Admin copy, and a Ticket
/// Detail timeline without empty-dash or duplicate comment entries.
/// </summary>
public sealed class Phase29RefinementTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public Phase29RefinementTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact] // Root cause of the Dashboard / Ticket Detail misalignment: a global sibling-panel margin.
    public async Task Stylesheet_HasNoGlobalSiblingPanelMargin_AndTheChartsGridHasEqualColumns()
    {
        var css = await _factory.CreateClient().GetStringAsync("/flowops.css");

        Assert.DoesNotMatch(@"\.panel\s*\+\s*\.panel\s*\{", css);
        Assert.Matches(@"\.dashboard-charts\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)\s+minmax\(0,\s*1fr\)", css);
        Assert.Matches(@"\.dashboard-charts\s*\{[^}]*align-items:\s*stretch", css);
        Assert.DoesNotContain(".dashboard-row-trend", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".dashboard-row-half", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_ShowsSummaryFirst_LimitsAttentionToThreeRows_AndUsesOneChartsGrid()
    {
        for (var i = 0; i < 4; i++)
        {
            await _factory.CreateAtRiskTicketAsync($"Attention limit {Guid.NewGuid():N}");
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);
        var html = await client.GetStringAsync("/");

        var summary = html.IndexOf(">Summary<", StringComparison.Ordinal);
        var attention = html.IndexOf("What needs attention", StringComparison.Ordinal);
        Assert.True(summary >= 0 && attention > summary, "the KPI summary comes before the attention list");

        var charts = html.IndexOf("dashboard-charts", StringComparison.Ordinal);
        Assert.True(charts > attention, "the analytical grid follows the attention list");
        Assert.DoesNotContain("dashboard-row-trend", html, StringComparison.Ordinal);

        // At most three rows in the attention list, with a link to the full page.
        var attentionSection = html[attention..charts];
        Assert.InRange(Regex.Matches(attentionSection, "class=\"q-row").Count, 1, 3);
        Assert.Contains("View all", attentionSection, StringComparison.Ordinal);

        // Ticket volume | status, then deadline status | workload — in that order inside one grid.
        var chartsHtml = html[charts..];
        var order = new[] { "Ticket volume over time", "Tickets by status", "Deadline status", "Workload by team" }
            .Select(h => chartsHtml.IndexOf(h, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order().ToArray(), order);
    }

    [Fact]
    public async Task Admin_ShowsPlainCopy_WithNoInternalRuleOrAdrReferences()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AdminEmail);

        var response = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("AUTH-RULE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ADR-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("coarse gate", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not yet built", html, StringComparison.Ordinal);
        Assert.Contains("Admin access is required", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketTimeline_HasNoEmptyDashEntries_AndNoDuplicateCommentAddedEvents()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, $"Timeline noise {Guid.NewGuid():N}");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        await Post(client, ticketId, "Assign");
        await Post(client, ticketId, "StartWork");
        await Post(client, ticketId, "AddComment", new KeyValuePair<string, string>("Input.CommentBody", "Investigating the fault."));

        var html = await client.GetStringAsync($"/Tickets/Details/{ticketId}");

        Assert.Contains("Investigating the fault.", html, StringComparison.Ordinal);   // the comment itself is there
        Assert.DoesNotContain("Comment added", html, StringComparison.Ordinal);        // its audit twin is not
        Assert.DoesNotContain("class=\"tl-detail\">—", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"tl-detail\">\n                                —", html, StringComparison.Ordinal);
        Assert.Contains("Status changed", html, StringComparison.Ordinal);
    }

    private static async Task Post(HttpClient client, int ticketId, string handler, params KeyValuePair<string, string>[] fields)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, $"/Tickets/Details/{ticketId}");
        var form = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Tickets/Details/{ticketId}?handler={handler}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
