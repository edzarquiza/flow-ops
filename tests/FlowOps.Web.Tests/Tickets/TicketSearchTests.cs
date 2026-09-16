using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Server-side search on Work Queue and At-Risk, over real HTTP against real PostgreSQL — the
/// search box narrows the existing paginated/ranked query, preserves the term across pagination
/// links, and shows a deliberate empty state when nothing matches.
/// </summary>
/// <remarks>Four sign-ins, inside the five-per-minute login rate limit (CLAUDE.md §12) — a fresh
/// <see cref="FlowOpsWebApplicationFactory"/> per test class, per the pattern other ticket-page
/// test classes already use.</remarks>
public sealed class TicketSearchTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketSearchTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WorkQueueSearch_FiltersToMatchingTicket()
    {
        var uniqueTitle = $"VPN gateway unreachable {Guid.NewGuid():N}";
        await _factory.CreateTicketAsync(TestUsers.AgentEmail, uniqueTitle);
        await _factory.CreateTicketAsync(TestUsers.AgentEmail, "Printer jam on 3rd floor");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync($"/Tickets?search={Uri.EscapeDataString(uniqueTitle)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(uniqueTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Printer jam on 3rd floor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkQueueSearch_NoMatches_ShowsEmptyStateWithClearSearchLink()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync($"/Tickets?search=no-such-ticket-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No tickets found", html, StringComparison.Ordinal);
        Assert.Contains("Clear search", html, StringComparison.Ordinal);
    }

    [Fact] // Section 7: pagination links must not lose the active search term.
    public async Task WorkQueueSearch_PaginationLinksPreserveTheSearchTerm()
    {
        const string marker = "FindableWorkQueueSearchMarker";
        for (var i = 0; i < 30; i++)
        {
            await _factory.CreateTicketAsync(TestUsers.AgentEmail, $"{marker} {i}");
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync($"/Tickets?search={marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"search={marker}", html, StringComparison.Ordinal);
        Assert.Contains("pageNumber=2", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtRiskSearch_FiltersToMatchingTicket()
    {
        var uniqueTitle = $"Payroll outage {Guid.NewGuid():N}";
        await _factory.CreateAtRiskTicketAsync(uniqueTitle);
        await _factory.CreateAtRiskTicketAsync("Unrelated at-risk ticket");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync($"/Tickets/AtRisk?search={Uri.EscapeDataString(uniqueTitle)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(uniqueTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated at-risk ticket", html, StringComparison.Ordinal);
    }
}
