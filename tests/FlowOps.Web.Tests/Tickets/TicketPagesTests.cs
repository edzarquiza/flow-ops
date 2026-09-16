using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// The Phase 5 vertical slice over real HTTP against real PostgreSQL: create a ticket as an
/// authenticated agent, land on its detail page, and find it in the paginated queue — plus the
/// authorization boundaries around all three.
/// </summary>
/// <remarks>
/// Four sign-ins, deliberately at the login rate limit's budget of five per minute per IP
/// (CLAUDE.md §12) — see <see cref="TestAuthentication"/>. Adding a fifth signing-in test to this
/// class would start returning 429 rather than exercising the pages.
/// </remarks>
public sealed class TicketPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    /// <summary>CREATE → DETAIL → PAGINATED QUEUE, as one real browser-shaped flow.</summary>
    [Fact]
    public async Task CreateTicket_AsAgent_RedirectsToDetailAndAppearsInQueue()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Tickets/Create");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Title", "Laptop will not power on"),
                new("Input.Description", "The laptop shows no lights when the power button is pressed."),
                new("Input.WorkType", "Incident"),
                new("Input.Priority", "High"),
                // Phase 11 (C-3): TeamId is no longer a user-facing field — CreateModel derives
                // it server-side from the chosen category (TICKET-INV-02).
                new("Input.CategoryId", _factory.CategoryId.ToString()),
                new("__RequestVerificationToken", token),
            ]),
        };

        // CREATE → redirect to the new ticket's detail page.
        var createResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);

        var location = createResponse.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/Tickets/", location, StringComparison.Ordinal);

        // DETAIL → the ticket renders, with a database-generated reference.
        var detailResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detailHtml = await detailResponse.Content.ReadAsStringAsync();
        Assert.Contains("Laptop will not power on", detailHtml, StringComparison.Ordinal);
        Assert.Contains("The laptop shows no lights", detailHtml, StringComparison.Ordinal);
        Assert.Contains(TestReferenceData.TeamName, detailHtml, StringComparison.Ordinal);
        Assert.Matches(@"FO-\d{6}", detailHtml);

        // QUEUE → the new ticket is listed for the agent who created it.
        var queueResponse = await client.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);

        var queueHtml = await queueResponse.Content.ReadAsStringAsync();
        Assert.Contains("Laptop will not power on", queueHtml, StringComparison.Ordinal);
        Assert.Contains("Work queue", queueHtml, StringComparison.Ordinal);
    }

    [Fact] // AUTH-RULE-02: Viewer may not create, and sees nothing outside their (empty) teams.
    public async Task Viewer_CannotReachCreateForm_AndCannotSeeAnotherTeamsTicket()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var createResponse = await client.GetAsync("/Tickets/Create");
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", createResponse.Headers.Location?.ToString());

        // Not a member of the ticket's team: indistinguishable from the ticket not existing.
        // Compared against a genuinely nonexistent id so the 404 is proved to be the
        // authorization outcome and not merely an unmatched route.
        var detailResponse = await client.GetAsync($"/Tickets/Details/{_factory.SeededTicketId}");
        var missingResponse = await client.GetAsync($"/Tickets/Details/{int.MaxValue}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        Assert.Equal(missingResponse.StatusCode, detailResponse.StatusCode);

        var queueResponse = await client.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        Assert.Contains(
            "No tickets are visible to you right now",
            await queueResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact] // AUTH-RULE-02 "View tickets: all" — Admin is in no team yet still sees the ticket.
    public async Task Admin_CanViewTicketOutsideTheirTeamMemberships()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AdminEmail);

        var response = await client.GetAsync($"/Tickets/Details/{_factory.SeededTicketId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "VPN client will not connect",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact] // Membership-based visibility: the Manager belongs to the team, so the ticket is listed.
    public async Task Manager_SeesTeamTicketInQueue()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await client.GetAsync("/Tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("VPN client will not connect", html, StringComparison.Ordinal);
        // Phase 11: the queue row dropped the Category column (constant within a single-team
        // queue, no information per row) — Team is the equivalent membership-scope signal now.
        Assert.Contains(TestReferenceData.TeamName, html, StringComparison.Ordinal);
    }
}
