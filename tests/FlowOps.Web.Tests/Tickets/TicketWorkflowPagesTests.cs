using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 6 workflow over real HTTP: an authorized actor drives a ticket through the state machine,
/// the resulting status and audit history render, an unauthorized action is refused, and an
/// illegal transition produces a form error rather than an exception page.
/// </summary>
/// <remarks>
/// Two sign-ins, well inside the five-per-minute login rate limit (CLAUDE.md §12). Each test
/// creates its own ticket so no test depends on another's state.
/// </remarks>
public sealed class TicketWorkflowPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketWorkflowPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// Assign → StartWork → Resolve as the agent, then an attempt to Close that the policy must
    /// refuse because TICKET-WF-08 reserves closing for a Manager/Admin or the requester — and
    /// here the requester is the manager, not this agent.
    /// </summary>
    [Fact]
    public async Task Agent_DrivesWorkflow_SeesStatusAndHistory_ButCannotClose()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Monitor flickers intermittently");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        await PostAsync(client, ticketId, "Assign", HttpStatusCode.Redirect);
        await PostAsync(client, ticketId, "StartWork", HttpStatusCode.Redirect);

        var afterStart = await GetDetailAsync(client, ticketId);
        Assert.Contains("In Progress", afterStart, StringComparison.Ordinal);

        await PostAsync(
            client,
            ticketId,
            "Resolve",
            HttpStatusCode.Redirect,
            [
                new("Input.ResolutionCode", "Fixed"),
                new("Input.ResolutionNotes", "Reseated the display cable and confirmed with the user."),
            ]);

        var afterResolve = await GetDetailAsync(client, ticketId);
        Assert.Contains("Resolved", afterResolve, StringComparison.Ordinal);

        // Audit history is rendered, newest first, naming the actor.
        Assert.Contains("Audit history", afterResolve, StringComparison.Ordinal);
        Assert.Contains("Created", afterResolve, StringComparison.Ordinal);
        Assert.Contains("Assigned", afterResolve, StringComparison.Ordinal);
        Assert.Contains("Status changed", afterResolve, StringComparison.Ordinal); // plain-language event title
        Assert.DoesNotContain("StatusChanged", afterResolve, StringComparison.Ordinal);
        Assert.Contains(TestUsers.AgentEmail, afterResolve, StringComparison.Ordinal);

        // The Close button is not offered — TICKET-WF-08 reserves closing for a Manager/Admin or
        // the requester, and this agent is neither.
        Assert.DoesNotContain("handler=Close", afterResolve, StringComparison.Ordinal);

        // But hiding a button is a UX affordance, never the security boundary (CLAUDE.md §2
        // rule 8). Post Close anyway, with a genuine antiforgery token taken from another page
        // the agent legitimately has — the server must still refuse it.
        await PostAsync(
            client,
            ticketId,
            "Close",
            HttpStatusCode.Redirect,
            expectAccessDenied: true,
            tokenPath: "/Tickets/Create");

        // The Ticket Detail rail (docs/ui/design-system.md §6) always spells out all six workflow
        // stage names for context — including "Closed" — on every ticket, whatever its actual
        // status. A bare `DoesNotContain("Closed")` would therefore fail on any resolved-but-not-
        // closed ticket regardless of whether Close was ever attempted, so the real assertion —
        // the ticket's *actual* stage is Resolved, not Closed — is scoped to the rail's own
        // accessible label instead, which names the ticket's current stage specifically.
        var afterCloseAttempt = await GetDetailAsync(client, ticketId);
        Assert.Contains("Resolved", afterCloseAttempt, StringComparison.Ordinal);
        Assert.Contains("Workflow stage: Resolved.", afterCloseAttempt, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow stage: Closed.", afterCloseAttempt, StringComparison.Ordinal);
    }

    [Fact] // An illegal transition is a form error, not an unhandled exception page.
    public async Task IllegalTransition_RendersFormError_AndLeavesTicketUnchanged()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Keyboard key sticking");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        // Admin passes every policy check, so only the aggregate's status guard can reject —
        // which is what makes this a domain-rule error rather than an authorization refusal.
        await TestAuthentication.SignInAsync(client, TestUsers.AdminEmail);

        var response = await PostRawAsync(
            client,
            ticketId,
            "Resolve",
            [
                new("Input.ResolutionCode", "Fixed"),
                new("Input.ResolutionNotes", "Attempting to resolve a ticket that is still Open."),
            ]);

        // Re-rendered page carrying the domain rule's own message (without the developer-facing code), not a 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TICKET-WF-07", html, StringComparison.Ordinal);
        Assert.Contains("Cannot resolve", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stack trace", html, StringComparison.OrdinalIgnoreCase);

        var detail = await GetDetailAsync(client, ticketId);
        Assert.Contains("Open", detail, StringComparison.Ordinal);
    }

    private async Task<string> GetDetailAsync(HttpClient client, int ticketId)
    {
        var response = await client.GetAsync($"/Tickets/Details/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task PostAsync(
        HttpClient client,
        int ticketId,
        string handler,
        HttpStatusCode expected,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        bool expectAccessDenied = false,
        string? tokenPath = null)
    {
        var response = await PostRawAsync(client, ticketId, handler, fields, tokenPath);

        Assert.Equal(expected, response.StatusCode);

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        if (expectAccessDenied)
        {
            Assert.Contains("/Account/AccessDenied", location, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"/Tickets/Details/{ticketId}", location, StringComparison.Ordinal);
        }
    }

    /// <param name="tokenPath">Where to source the antiforgery token. Defaults to the ticket's own
    /// detail page; a caller passes a different page when the detail page deliberately offers no
    /// form, to prove the server refuses the action rather than relying on the missing button.</param>
    private async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        int ticketId,
        string handler,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        string? tokenPath = null)
    {
        var path = $"/Tickets/Details/{ticketId}";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, tokenPath ?? path);

        var form = new List<KeyValuePair<string, string>>(fields ?? [])
        {
            new("__RequestVerificationToken", token),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?handler={handler}")
        {
            Content = new FormUrlEncodedContent(form),
        };

        return await client.SendAsync(request);
    }
}
