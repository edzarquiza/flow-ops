using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 30 (ADR-0035): Ticket Detail's "Why this needs attention" panel — present with real
/// evidence for a ticket the Attention Brief flags, entirely absent for a healthy or terminal one.
/// </summary>
public sealed class AttentionBriefPanelTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public AttentionBriefPanelTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Details_TicketNeedingAttention_ShowsTheAttentionBriefPanel()
    {
        var ticketId = await _factory.CreateAtRiskTicketAsync("Building access control offline");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync($"/Tickets/Details/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Why this needs attention", html, StringComparison.Ordinal);
        // The ticket is unassigned and Critical — UnassignedUrgent's own suggestion.
        Assert.Contains("Assign an owner.", html, StringComparison.Ordinal);
    }

    [Fact] // A freshly created, healthy ticket has nothing for the policy to explain.
    public async Task Details_HealthyTicket_HasNoAttentionBriefPanel()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.AgentEmail, "Keyboard replacement request");

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync($"/Tickets/Details/{ticketId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Why this needs attention", html, StringComparison.Ordinal);
    }
}
