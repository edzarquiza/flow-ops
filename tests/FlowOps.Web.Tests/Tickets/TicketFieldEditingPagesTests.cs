using System.Net;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 30C (ADR-0033) over real HTTP: editing an existing ticket's Priority/Category/Team/Due
/// date from its Details page — the affordances render only for an authorized actor on a
/// non-terminal ticket (Due date excepted), a hidden button is never the real boundary (a forged
/// POST is still refused), and a caller-supplied category/team id from another organization is
/// still refused even with a genuine token.
/// </summary>
public sealed class TicketFieldEditingPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketFieldEditingPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Manager_CanChangePriority_AndTheTimelineRecordsIt()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Keyboard sticking on row 3");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        Assert.Contains("handler=ChangePriority", await GetDetailAsync(client, ticketId), StringComparison.Ordinal);

        await PostAsync(client, ticketId, "ChangePriority", [new("Input.NewPriority", "Critical")]);

        var html = await GetDetailAsync(client, ticketId);
        Assert.Contains("Critical priority", html, StringComparison.Ordinal);
        Assert.Contains("Priority changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_CanChangeCategory_AndTheTimelineRecordsIt()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Badge reader offline at east entrance");
        int secondCategoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var category = new Category(0, _factory.TeamId, "Access Control", FlowOps.Domain.Tickets.WorkType.Incident, DateTimeOffset.UtcNow);
            db.Add(category);
            await db.SaveChangesAsync();
            secondCategoryId = category.Id;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        await PostAsync(client, ticketId, "ChangeCategory", [new("Input.NewCategoryId", secondCategoryId.ToString())]);

        var html = await GetDetailAsync(client, ticketId);
        Assert.Contains("Access Control", html, StringComparison.Ordinal);
        Assert.Contains("Category changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manager_CanChangeTeam_WhichAlsoMovesCategoryAndClearsAssignee()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "VPN drops every few minutes");
        int otherTeamCategoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var otherTeam = new Team(0, _factory.OrganizationId, $"Network Team {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(otherTeam);
            await db.SaveChangesAsync();
            var otherCategory = new Category(0, otherTeam.Id, "Network Issues", FlowOps.Domain.Tickets.WorkType.Incident, DateTimeOffset.UtcNow);
            db.Add(otherCategory);
            await db.SaveChangesAsync();
            otherTeamCategoryId = otherCategory.Id;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);
        await PostAsync(client, ticketId, "Assign", []);

        await PostAsync(client, ticketId, "ChangeTeam", [new("Input.NewTeamCategoryId", otherTeamCategoryId.ToString())]);

        // The Manager is not a member of the destination team, so they can no longer view this
        // ticket at all afterward (AUTH-RULE-04/CanView) — exactly the same "moved out of my own
        // reach" outcome a real click-through move would also produce. Verified via the database
        // directly rather than re-fetching the page as this now-unauthorized caller.
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var ticket = await db2.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(otherTeamCategoryId, ticket.CategoryId);
        Assert.Null(ticket.AssigneeId);
        var events = await db2.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticketId).Select(e => e.EventType).ToListAsync();
        Assert.Contains(FlowOps.Domain.Tickets.TicketEventType.TeamChanged, events);
        Assert.Contains(FlowOps.Domain.Tickets.TicketEventType.CategoryChanged, events);
    }

    [Fact]
    public async Task Manager_CanSetAndClearTheDueDate()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Projector bulb needs replacing");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        await PostAsync(client, ticketId, "ChangeDueDate", [new("Input.NewDueDate", "2027-01-15T09:00")]);
        Assert.Contains("2027-01-15", await GetDetailAsync(client, ticketId), StringComparison.Ordinal);

        await PostAsync(client, ticketId, "ChangeDueDate", [new("Input.NewDueDate", "")]);
        var html = await GetDetailAsync(client, ticketId);
        Assert.DoesNotContain("2027-01-15", html, StringComparison.Ordinal);
    }

    [Fact] // Priority/Category/Team edits are not offered on a terminal ticket, but Due date still is.
    public async Task OnAResolvedTicket_PriorityCategoryTeamEditsAreHidden_ButDueDateStillWorks()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Monitor arm loose");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);
        await PostAsync(client, ticketId, "Assign", []);
        await PostAsync(client, ticketId, "StartWork", []);
        await PostAsync(client, ticketId, "Resolve", [new("Input.ResolutionCode", "Fixed"), new("Input.ResolutionNotes", "Tightened the mounting screw.")]);

        var html = await GetDetailAsync(client, ticketId);
        Assert.DoesNotContain("handler=ChangePriority", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ChangeCategory", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ChangeTeam", html, StringComparison.Ordinal);
        Assert.Contains("handler=ChangeDueDate", html, StringComparison.Ordinal);

        await PostAsync(client, ticketId, "ChangeDueDate", [new("Input.NewDueDate", "2027-02-01T09:00")]);
        Assert.Contains("2027-02-01", await GetDetailAsync(client, ticketId), StringComparison.Ordinal);
    }

    [Fact] // A hidden button is never the real boundary (CLAUDE.md §2 rule 8). An Agent who is not
           // this ticket's assignee has no transition authority at all (TicketAccessPolicy.CanTransition),
           // so none of the edit affordances are offered — a Viewer is not used here since they are
           // not a member of the seeded team at all and would simply 404, testing nothing new.
    public async Task NonAssigneeAgent_DoesNotSeeEditButtons_AndAForgedPostIsStillRefused()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Wall outlet sparking near desk 12");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var html = await GetDetailAsync(client, ticketId);
        Assert.DoesNotContain("handler=ChangePriority", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ChangeCategory", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ChangeTeam", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ChangeDueDate", html, StringComparison.Ordinal);

        var response = await PostRawAsync(client, ticketId, "ChangePriority", [new("Input.NewPriority", "Critical")], tokenPath: "/Tickets/Create");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString(), StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.NotEqual(FlowOps.Domain.Tickets.Priority.Critical, ticket.Priority);
    }

    [Fact] // A category id from another organization is refused even with a genuine token.
    public async Task ChangeCategory_WithACategoryFromAnotherOrganization_IsRefused()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, "Chair wheel broken");
        int foreignCategoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var foreignOrg = new FlowOps.Domain.Organizations.Organization(0, $"Other Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(foreignOrg);
            await db.SaveChangesAsync();
            var foreignTeam = new Team(0, foreignOrg.Id, "Foreign Team", DateTimeOffset.UtcNow);
            db.Add(foreignTeam);
            await db.SaveChangesAsync();
            var foreignCategory = new Category(0, foreignTeam.Id, "Foreign Category", FlowOps.Domain.Tickets.WorkType.Incident, DateTimeOffset.UtcNow);
            db.Add(foreignCategory);
            await db.SaveChangesAsync();
            foreignCategoryId = foreignCategory.Id;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var response = await PostRawAsync(client, ticketId, "ChangeCategory", [new("Input.NewCategoryId", foreignCategoryId.ToString())]);

        // A DomainRuleException is a form error (Page(), not a redirect) — the message is on this
        // POST response's own body, not a subsequent GET (ModelState does not survive a new request).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("must belong to the ticket", responseBody, StringComparison.OrdinalIgnoreCase);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var ticket = await db2.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(_factory.CategoryId, ticket.CategoryId);
    }

    // ---- helpers ----

    private async Task<string> GetDetailAsync(HttpClient client, int ticketId)
    {
        var response = await client.GetAsync($"/Tickets/Details/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task PostAsync(HttpClient client, int ticketId, string handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var response = await PostRawAsync(client, ticketId, handler, fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"/Tickets/Details/{ticketId}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        int ticketId,
        string handler,
        IEnumerable<KeyValuePair<string, string>> fields,
        string? tokenPath = null)
    {
        var path = $"/Tickets/Details/{ticketId}";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, tokenPath ?? path);

        var form = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?handler={handler}")
        {
            Content = new FormUrlEncodedContent(form),
        };

        return await client.SendAsync(request);
    }
}
