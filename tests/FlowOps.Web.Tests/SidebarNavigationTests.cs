using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase F1-B (hardening): the sidebar's "Create Ticket" link is now gated by
/// <c>TicketAccessPolicy.CanCreate</c> — the same policy <c>Tickets/Create.cshtml.cs</c> itself
/// already enforces server-side (AUTH-RULE-02: Viewer may not create). Before this phase, a Viewer
/// saw the link despite being blocked from submitting it. These tests verify the rendered sidebar
/// HTML for all four roles, not just the source-level gate.
/// </summary>
public sealed class SidebarNavigationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public SidebarNavigationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Viewer_DoesNotSeeCreateTicketInSidebar()
    {
        var html = await SidebarHtmlAsync(TestUsers.ViewerEmail);
        Assert.DoesNotContain("Create Ticket", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TestUsers.AgentEmail)]
    [InlineData(TestUsers.ManagerEmail)]
    [InlineData(TestUsers.AdminEmail)]
    public async Task RolesThatCanCreate_SeeCreateTicketInSidebar(string email)
    {
        var html = await SidebarHtmlAsync(email);
        Assert.Contains("Create Ticket", html, StringComparison.Ordinal);
    }

    [Fact] // Restrained grouping, not a redesign: the two eyebrow labels exist and nothing else changed.
    public async Task Admin_SeesWorkAndAdministrationGroupLabels()
    {
        var html = await SidebarHtmlAsync(TestUsers.AdminEmail);

        Assert.Contains("fo-sidebar__nav-label\">Work<", html, StringComparison.Ordinal);
        Assert.Contains("fo-sidebar__nav-label\">Administration<", html, StringComparison.Ordinal);
        // Grouping is additive only — every existing link, route, and the single nav landmark
        // (one <nav aria-label="Primary navigation">) are all still present exactly as before.
        Assert.Contains("aria-label=\"Primary navigation\"", html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<nav\\b"));
    }

    [Fact] // An Agent has nothing under "Administration" — the label must not render orphaned.
    public async Task Agent_DoesNotSeeAdministrationGroupLabel()
    {
        var html = await SidebarHtmlAsync(TestUsers.AgentEmail);

        Assert.Contains("fo-sidebar__nav-label\">Work<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fo-sidebar__nav-label\">Administration<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Members", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Admin<", html, StringComparison.Ordinal);
    }

    private async Task<string> SidebarHtmlAsync(string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
