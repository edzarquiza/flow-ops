using System.Net;
using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 9 comments and the merged activity timeline over real HTTP: an authorized actor posts a
/// comment and sees it rendered alongside the ticket's audit events, a Viewer never sees the form
/// or an internal comment, and a forged POST from a Viewer is still refused server-side.
/// </summary>
/// <remarks>
/// Five sign-ins total across this class, exactly the five-per-minute login rate limit budget
/// (CLAUDE.md §12) — the Agent's two comment-posting assertions are combined into one signed-in
/// session for that reason, not because they are the same concern. Each test creates its own
/// ticket so no test depends on another's state.
/// </remarks>
public sealed class TicketCommentPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketCommentPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact] // TICKET-ENT-05: both a public and an internal comment render for the Agent who posted them.
    public async Task Agent_PostsPublicAndInternalComments_BothRenderInTheMergedHistory()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Scanner jammed on 2nd floor");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await PostCommentAsync(client, ticketId, "Checked the scanner, ordering a replacement part.", isInternal: false);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"/Tickets/Details/{ticketId}", response.Headers.Location?.ToString());

        await PostCommentAsync(client, ticketId, "Internal: escalate to network team if not renewed by Friday.", isInternal: true);

        var detail = await GetDetailAsync(client, ticketId);

        // Both comments rendered in the same merged history section that already shows the
        // Created event — the public one and the internal one, both visible to the Agent who
        // posted them (TICKET-ENT-05: internal comments are visible to Agent/Manager/Admin).
        Assert.Contains("Checked the scanner, ordering a replacement part.", detail, StringComparison.Ordinal);
        Assert.Contains("Internal: escalate to network team if not renewed by Friday.", detail, StringComparison.Ordinal);
        Assert.Contains("Created", detail, StringComparison.Ordinal);
        Assert.Contains(TestUsers.AgentEmail, detail, StringComparison.Ordinal);
    }

    [Fact] // TICKET-ENT-05 / AUTH-RULE-04: a Viewer never sees the form, and an internal comment
           // posted by someone else is excluded from what a Viewer's page even receives.
    public async Task Viewer_HasNoCommentForm_AndDoesNotSeeInternalComments()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Badge reader offline at main entrance");

        // The shared Viewer test user is deliberately not a member of the shared team (so other
        // tests can prove "cannot see the ticket at all") — but that means it can never reach
        // this ticket's detail page either. To isolate "can view, but not comment or see internal
        // content" from "cannot view at all", this test alone grants membership, scoped to this
        // class's own ephemeral container and touching no shared fixture code.
        await GrantViewerTeamMembershipAsync();

        // An agent posts one public and one internal comment first.
        var agentClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(agentClient, TestUsers.AgentEmail);
        await PostCommentAsync(agentClient, ticketId, "Public note: security notified.", isInternal: false);
        await PostCommentAsync(agentClient, ticketId, "Internal note: badge reader is out of warranty.", isInternal: true);

        var viewerClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(viewerClient, TestUsers.ViewerEmail);

        var detail = await GetDetailAsync(viewerClient, ticketId);

        // No comment form is offered (AUTH-RULE-02 "Comment (public)": Viewer = ✗).
        Assert.DoesNotContain("handler=AddComment", detail, StringComparison.Ordinal);

        // The public comment is visible; the internal one is not present at all in the markup —
        // it was excluded at the query, not merely styled away.
        Assert.Contains("Public note: security notified.", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal note: badge reader is out of warranty.", detail, StringComparison.Ordinal);
    }

    [Fact] // Hiding the form is an affordance, never the boundary (CLAUDE.md §2 rule 8): a forged
           // POST from a Viewer, using a genuine token from another page, is still refused.
    public async Task Viewer_ForgedCommentPost_IsRejectedServerSide()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Conference room projector flickering");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var response = await PostCommentAsync(
            client,
            ticketId,
            "A Viewer should never be able to post this.",
            isInternal: false,
            tokenPath: "/"); // the post-login home page: it renders a sign-out form for any
                             // authenticated role, so it is a genuine token source even for a
                             // Viewer, who cannot reach /Tickets/Create or /Tickets/AtRisk's own
                             // forms.

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());

        var detail = await GetDetailAsync(client, ticketId);
        Assert.DoesNotContain("A Viewer should never be able to post this.", detail, StringComparison.Ordinal);
    }

    [Fact] // TICKET-ENT-05: an empty comment is a form error, not an unhandled exception page.
    public async Task EmptyComment_RendersFormError_AndLeavesTicketUnchanged()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Wi-Fi drops intermittently in lobby");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var response = await PostCommentAsync(client, ticketId, "   ", isInternal: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, not redirected
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("TICKET-ENT-05", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack trace", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Anonymous cannot reach the comment handler either.
    public async Task AnonymousCommentPost_RedirectsToLogin()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Elevator makes unusual noise");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Tickets/Details/{ticketId}?handler=AddComment")
        {
            Content = new FormUrlEncodedContent([new("Input.CommentBody", "Anonymous comment")]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// Adds the shared Viewer test user to the shared team, scoped to this factory's own
    /// ephemeral database only — it does not modify <c>TestReferenceData</c> or any other test
    /// class's fixture, since each class gets its own <see cref="FlowOpsWebApplicationFactory"/>
    /// instance and its own container.
    /// </summary>
    private async Task GrantViewerTeamMembershipAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var viewerId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.ViewerEmail);

        db.Add(new TeamMember(_factory.TeamId, viewerId, isTeamManager: false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<string> GetDetailAsync(HttpClient client, int ticketId)
    {
        var response = await client.GetAsync($"/Tickets/Details/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <param name="tokenPath">Where to source the antiforgery token. Defaults to the ticket's own
    /// detail page; a caller passes a different page when the detail page offers no comment form,
    /// to prove the server refuses the action rather than relying on the missing button.</param>
    private async Task<HttpResponseMessage> PostCommentAsync(
        HttpClient client,
        int ticketId,
        string body,
        bool isInternal,
        string? tokenPath = null)
    {
        var path = $"/Tickets/Details/{ticketId}";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, tokenPath ?? path);

        var form = new List<KeyValuePair<string, string>>
        {
            new("Input.CommentBody", body),
            new("__RequestVerificationToken", token),
        };

        if (isInternal)
        {
            form.Add(new("Input.CommentIsInternal", "true"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?handler=AddComment")
        {
            Content = new FormUrlEncodedContent(form),
        };

        return await client.SendAsync(request);
    }
}
