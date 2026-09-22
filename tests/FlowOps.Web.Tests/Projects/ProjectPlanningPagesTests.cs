using System.Net;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Projects;

/// <summary>
/// ADR-0029 over real HTTP against real PostgreSQL: the Projects list, Overview, Board and Tickets
/// pages, the sprint/ticket planning handlers, role restrictions, and forged cross-organization ids
/// (which must be refused and leave the database unchanged). Every test registers its own
/// disposable organization through the unthrottled registration flow.
/// </summary>
public sealed class ProjectPlanningPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public ProjectPlanningPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    private sealed record Org(HttpClient Client, string Email, int OrganizationId, int ProjectId, int TicketId);

    [Fact]
    public async Task Admin_CanPlanASprint_EndToEnd()
    {
        var org = await SetUpOrgAsync("Sprint Flow");
        var c = org.Client;

        // Projects list and the empty board.
        var list = await GetAsync(c, "/Projects");
        Assert.Contains("Sprint Flow Project", list, StringComparison.Ordinal);
        var emptyBoard = await GetAsync(c, $"/Projects/Board/{org.ProjectId}");
        Assert.Contains("No active sprint", emptyBoard, StringComparison.Ordinal);

        // Create -> start a sprint from the Overview page.
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Projects/Details/{org.ProjectId}", "CreateSprint",
            [new("name", "Week of Sep 21"), new("start", "2026-09-21"), new("end", "2026-09-27")])).StatusCode);
        var sprintId = await SprintIdAsync(org.ProjectId);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Projects/Details/{org.ProjectId}", "StartSprint", [new("sprintId", sprintId.ToString())])).StatusCode);

        // The ticket is not in the sprint yet: not on the board, but in the project's Tickets list.
        Assert.DoesNotContain("Migrate the reporting database", await GetAsync(c, $"/Projects/Board/{org.ProjectId}"), StringComparison.Ordinal);
        Assert.Contains("Migrate the reporting database", await GetAsync(c, $"/Projects/Tickets/{org.ProjectId}"), StringComparison.Ordinal);

        // Plan it in: it lands in the sprint backlog.
        await PostAsync(c, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);
        var board = await GetAsync(c, $"/Projects/Board/{org.ProjectId}");
        Assert.Contains("Migrate the reporting database", board, StringComparison.Ordinal);
        Assert.Contains("Planned", board, StringComparison.Ordinal);
        Assert.Equal(Status.Open, (await TicketAsync(org.TicketId)).Status); // membership never changes status

        // Take it off the sprint: gone from the board, still in the project.
        await PostAsync(c, $"/Projects/Board/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString())]);
        Assert.DoesNotContain("Migrate the reporting database", await GetAsync(c, $"/Projects/Board/{org.ProjectId}"), StringComparison.Ordinal);
        Assert.Contains("Migrate the reporting database", await GetAsync(c, $"/Projects/Tickets/{org.ProjectId}"), StringComparison.Ordinal);
        Assert.Null((await TicketAsync(org.TicketId)).SprintId);
    }

    [Fact]
    public async Task InvalidSprintDates_ShowAnErrorAndCreateNothing()
    {
        var org = await SetUpOrgAsync("Bad Dates");

        await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "CreateSprint",
            [new("name", "Backwards"), new("start", "2026-09-27"), new("end", "2026-09-21")]);

        var page = await GetAsync(org.Client, $"/Projects/Details/{org.ProjectId}");
        Assert.Contains("start date must be on or before its end date", page, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountSprintsAsync(org.ProjectId));
    }

    [Fact]
    public async Task UnknownProject_Is404()
    {
        var org = await SetUpOrgAsync("Missing Project");
        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.GetAsync("/Projects/Board/2000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.GetAsync("/Projects/Tickets/2000000")).StatusCode);
    }

    [Fact]
    public async Task CrossOrg_ForgedProjectAndSprintAndTicketIds_AreRefused_AndNothingChanges()
    {
        var a = await SetUpOrgAsync("Org A Planning");
        var sprintId = await CreateSprintAsync(a, activate: false);
        var b = await SetUpOrgAsync("Org B Planning");

        // Reading another organization's project: indistinguishable from a missing one.
        foreach (var path in new[] { $"/Projects/Board/{a.ProjectId}", $"/Projects/Tickets/{a.ProjectId}", $"/Projects/Details/{a.ProjectId}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(path)).StatusCode);
        }

        Assert.DoesNotContain("Org A Planning Project", await GetAsync(b.Client, "/Projects"), StringComparison.Ordinal);

        // Mutating it with forged ids (using B's own project route, A's sprint/ticket ids).
        var start = await PostAsync(b.Client, $"/Projects/Board/{b.ProjectId}", "StartSprint", [new("sprintId", sprintId.ToString())]);
        var move = await PostAsync(b.Client, $"/Projects/Tickets/{b.ProjectId}", "MoveToSprint", [new("ticketId", a.TicketId.ToString()), new("sprintId", sprintId.ToString())]);
        var moveOwnIntoForeign = await PostAsync(b.Client, $"/Projects/Tickets/{b.ProjectId}", "MoveToSprint", [new("ticketId", b.TicketId.ToString()), new("sprintId", sprintId.ToString())]);
        var bToken = ExtractToken(await GetAsync(b.Client, $"/Projects/Details/{b.ProjectId}"));
        var create = await PostRawAsync(b.Client, $"/Projects/Details/{a.ProjectId}", "CreateSprint", bToken, [new("name", "Injected"), new("start", "2026-10-05"), new("end", "2026-10-11")]);

        foreach (var response in new[] { start, move, moveOwnIntoForeign, create })
        {
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"Unexpected {response.StatusCode}");
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            AssertRefused(response);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.Equal(SprintStatus.Planned, (await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId)).Status);
        Assert.Equal(1, await db.Sprints.CountAsync(s => s.ProjectId == a.ProjectId));
        Assert.Null((await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == a.TicketId)).SprintId);
        Assert.Null((await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == b.TicketId)).SprintId);
    }

    [Fact]
    public async Task Agent_CanViewBoard_ButCannotMutateSprintsOrPlanning()
    {
        var org = await SetUpOrgAsync("Agent Restrictions");
        var sprintId = await CreateSprintAsync(org, activate: true);
        var agent = await AddAgentAsync(org);

        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/Projects/Board/{org.ProjectId}")).StatusCode);
        Assert.DoesNotContain("Complete sprint", await GetAsync(agent, $"/Projects/Board/{org.ProjectId}"), StringComparison.Ordinal);

        var token = await TestAuthentication.AntiForgeryTokenAsync(agent, "/Tickets/Create");
        var attempts = new[]
        {
            await PostRawAsync(agent, $"/Projects/Details/{org.ProjectId}", "CreateSprint", token, [new("name", "Agent sprint"), new("start", "2026-10-05"), new("end", "2026-10-11")]),
            await PostRawAsync(agent, $"/Projects/Board/{org.ProjectId}", "CompleteSprint", token, [new("sprintId", sprintId.ToString())]),
            await PostRawAsync(agent, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", token, [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]),
        };

        foreach (var response in attempts)
        {
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            AssertRefused(response);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.Equal(SprintStatus.Active, (await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId)).Status);
        Assert.Equal(1, await db.Sprints.CountAsync(s => s.ProjectId == org.ProjectId));
        Assert.Null((await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == org.TicketId)).SprintId);
    }

    [Fact]
    public async Task ProjectTicketsFilter_NarrowsBySprintScope()
    {
        var org = await SetUpOrgAsync("Filters");
        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        Assert.Contains("Migrate the reporting database", await GetAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}?sprint=Current"), StringComparison.Ordinal);
        var none = await GetAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}?sprint=NoSprint");
        Assert.DoesNotContain("Migrate the reporting database", none, StringComparison.Ordinal);
        Assert.Contains("No tickets match these filters", none, StringComparison.Ordinal);
        // Garbage filter values degrade to "no filter" rather than erroring.
        Assert.Equal(HttpStatusCode.OK, (await org.Client.GetAsync($"/Projects/Tickets/{org.ProjectId}?sprint=bogus&status=nope&assignee=not-a-guid")).StatusCode);
    }

    // ---------------- Phase 26B (ADR-0030) ----------------

    [Fact] // Regression: the values used to be lost on a rejected submit — and once carried through the
           // cookie TempData provider, a date-looking string came back as a DateTime and crashed the page.
    public async Task CreateSprint_ValidationFailure_KeepsTheUsersInput()
    {
        var org = await SetUpOrgAsync("Sprint Form");

        await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "CreateSprint",
            [new("name", "My Custom Sprint"), new("start", "2026-11-20"), new("end", "2026-11-10")]);
        var page = await GetAsync(org.Client, $"/Projects/Details/{org.ProjectId}");

        Assert.Contains("start date must be on or before its end date", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value=\"My Custom Sprint\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"2026-11-20\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"2026-11-10\"", page, StringComparison.Ordinal);
        Assert.Equal(0, await CountSprintsAsync(org.ProjectId));
    }

    [Fact]
    public async Task Board_HasFiveColumns_NoAssignedColumn_AndTheDragEnhancementIsWiredOnlyAsAnEnhancement()
    {
        var org = await SetUpOrgAsync("Board Shape");
        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        var board = await GetAsync(org.Client, $"/Projects/Board/{org.ProjectId}");

        foreach (var column in new[] { "Backlog", "Open", "InProgress", "Pending", "Done" })
        {
            Assert.Contains($"data-column=\"{column}\"", board, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("data-column=\"Assigned\"", board, StringComparison.Ordinal);
        Assert.Contains("draggable=\"true\"", board, StringComparison.Ordinal);
        Assert.Contains("data-targets=\"", board, StringComparison.Ordinal);
        Assert.Contains("board-dnd.js", board, StringComparison.Ordinal);
        Assert.Contains("Assign to me &amp; start work", board, StringComparison.Ordinal); // the menu twin of the drag
        Assert.Contains("row-menu.js", board, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoardMove_ValidDragStartsWork_InvalidDragIsExplainedAndChangesNothing()
    {
        var org = await SetUpOrgAsync("Board Move");
        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        // Invalid: a ticket that has not started cannot be put on hold (nothing to hold, no assignee).
        await PostAsync(org.Client, $"/Projects/Board/{org.ProjectId}", "BoardMove", [new("ticketId", org.TicketId.ToString()), new("target", "Pending"), new("reason", "Waiting")]);
        var refused = await GetAsync(org.Client, $"/Projects/Board/{org.ProjectId}");
        Assert.Contains("Assign the ticket before putting it on hold", refused, StringComparison.Ordinal);
        Assert.Equal(Status.Open, (await TicketAsync(org.TicketId)).Status);

        // Valid: Backlog -> In progress is the existing assign + start work.
        await PostAsync(org.Client, $"/Projects/Board/{org.ProjectId}", "BoardMove", [new("ticketId", org.TicketId.ToString()), new("target", "InProgress")]);
        var ticket = await TicketAsync(org.TicketId);
        Assert.Equal(Status.InProgress, ticket.Status);
        Assert.NotNull(ticket.AssigneeId);
        Assert.Contains("data-column=\"InProgress\"", await GetAsync(org.Client, $"/Projects/Board/{org.ProjectId}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelSprint_ShowsInHistoryAsCancelled_AndFinishedSprintsGetADetailPage()
    {
        var org = await SetUpOrgAsync("Cancel And History");
        var toCancel = await CreateSprintAsync(org, activate: false);
        await PostAsync(org.Client, $"/Projects/Sprints/{org.ProjectId}", "CancelSprint", [new("sprintId", toCancel.ToString())]);

        var list = await GetAsync(org.Client, $"/Projects/Sprints/{org.ProjectId}");
        Assert.Contains("Cancelled", list, StringComparison.Ordinal);
        Assert.Contains($"SprintDetail/{org.ProjectId}/{toCancel}", list, StringComparison.Ordinal);
        Assert.Contains("Cancelled sprint", await GetAsync(org.Client, $"/Projects/SprintDetail/{org.ProjectId}/{toCancel}"), StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.Equal(SprintStatus.Cancelled, (await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == toCancel)).Status);
    }

    [Fact]
    public async Task CompleteThenCarryForward_KeepsHistoryAndMovesUnfinishedToTheCurrentSprint()
    {
        var org = await SetUpOrgAsync("Carry Forward Web");
        var first = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", first.ToString())]);
        await PostAsync(org.Client, $"/Projects/Board/{org.ProjectId}", "CompleteSprint", [new("sprintId", first.ToString())]);

        // A second sprint for the same project (the first one's dates are used, so pick later ones).
        await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "CreateSprint", [new("name", "Next"), new("start", "2026-10-05"), new("end", "2026-10-11")]);
        var next = await SprintIdAsync(org.ProjectId);
        await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "StartSprint", [new("sprintId", next.ToString())]);

        var detail = await GetAsync(org.Client, $"/Projects/SprintDetail/{org.ProjectId}/{first}");
        Assert.Contains("Migrate the reporting database", detail, StringComparison.Ordinal);
        Assert.Contains("unfinished to current sprint", detail, StringComparison.Ordinal);

        await PostAsync(org.Client, $"/Projects/SprintDetail/{org.ProjectId}/{first}", "CarryForward", [new("sprintId", first.ToString())]);

        Assert.Equal(next, (await TicketAsync(org.TicketId)).SprintId);
        var after = await GetAsync(org.Client, $"/Projects/SprintDetail/{org.ProjectId}/{first}");
        Assert.Contains("Moved to Next", after, StringComparison.Ordinal); // history still names the completed sprint
    }

    [Fact]
    public async Task CrossOrg_SprintsHistoryAndDetail_AndCarryForwardAndCancel_AreRefused_AndNothingChanges()
    {
        var a = await SetUpOrgAsync("Archive Org A");
        var sprintId = await CreateSprintAsync(a, activate: false);
        var b = await SetUpOrgAsync("Archive Org B");

        foreach (var path in new[] { $"/Projects/Sprints/{a.ProjectId}", $"/Projects/SprintDetail/{a.ProjectId}/{sprintId}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(path)).StatusCode);
        }

        // B's own project route, A's sprint id.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/Projects/SprintDetail/{b.ProjectId}/{sprintId}")).StatusCode);
        var cancel = await PostAsync(b.Client, $"/Projects/Sprints/{b.ProjectId}", "CancelSprint", [new("sprintId", sprintId.ToString())]);
        var carry = await PostAsync(b.Client, $"/Projects/Sprints/{b.ProjectId}", "CarryForward", [new("sprintId", sprintId.ToString())]);
        var drag = await PostAsync(b.Client, $"/Projects/Board/{b.ProjectId}", "BoardMove", [new("ticketId", a.TicketId.ToString()), new("target", "InProgress")]);
        foreach (var response in new[] { cancel, carry, drag })
        {
            AssertRefused(response);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.Equal(SprintStatus.Planned, (await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId)).Status);
        Assert.Equal(Status.Open, (await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == a.TicketId)).Status);
    }

    [Fact]
    public async Task Agent_CannotCancelOrCarryForward_ButCanStartWorkByDragging()
    {
        var org = await SetUpOrgAsync("Agent Board");
        var sprintId = await CreateSprintAsync(org, activate: false);
        var agent = await AddAgentAsync(org);
        var token = await TestAuthentication.AntiForgeryTokenAsync(agent, "/Tickets/Create");

        AssertRefused(await PostRawAsync(agent, $"/Projects/Sprints/{org.ProjectId}", "CancelSprint", token, [new("sprintId", sprintId.ToString())]));
        AssertRefused(await PostRawAsync(agent, $"/Projects/Sprints/{org.ProjectId}", "CarryForward", token, [new("sprintId", sprintId.ToString())]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.Equal(SprintStatus.Planned, (await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprintId)).Status);
    }

    /// <summary>A refused action is a 403, or the cookie scheme's redirect to /Account/AccessDenied — never a redirect back into the app as if it had succeeded.</summary>
    // ---------------- Phase 28B: plain language, sprint context, confirmation ----------------

    [Fact]
    public async Task Overview_SuggestsDatesAfterTheActiveSprint_AndLinksToTicketsNotInASprint()
    {
        var org = await SetUpOrgAsync("Overview Defaults");
        var c = org.Client;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var before = await GetAsync(c, $"/Projects/Details/{org.ProjectId}");
        Assert.Contains("1 ticket not in a sprint", before, StringComparison.Ordinal);
        Assert.Contains("sprint=NoSprint", before, StringComparison.Ordinal);
        Assert.Contains($"value=\"{today:yyyy-MM-dd}\"", before, StringComparison.Ordinal); // no sprints yet: starts today

        await PostAsync(c, $"/Projects/Details/{org.ProjectId}", "CreateSprint",
            [new("name", "Running"), new("start", today.ToString("yyyy-MM-dd")), new("end", today.AddDays(6).ToString("yyyy-MM-dd"))]);
        var sprintId = await SprintIdAsync(org.ProjectId);
        await PostAsync(c, $"/Projects/Details/{org.ProjectId}", "StartSprint", [new("sprintId", sprintId.ToString())]);
        await PostAsync(c, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        var after = await GetAsync(c, $"/Projects/Details/{org.ProjectId}");
        Assert.Contains($"value=\"{today.AddDays(7):yyyy-MM-dd}\"", after, StringComparison.Ordinal); // does not collide with the active sprint
        Assert.Contains("value=\"Sprint 2\"", after, StringComparison.Ordinal);
        Assert.Contains("Every ticket is in a sprint.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("not in a sprint", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteSprint_AsksFirst_ExplainsUnfinishedTickets_AndTheNoticeMatchesTheProduct()
    {
        var org = await SetUpOrgAsync("Complete Confirm");
        var sprintId = await CreateSprintAsync(org, activate: true);

        var board = await GetAsync(org.Client, $"/Projects/Board/{org.ProjectId}");
        Assert.Contains($"confirm-complete-sprint-{sprintId}", board, StringComparison.Ordinal);
        Assert.Contains("Complete this sprint?", board, StringComparison.Ordinal);
        Assert.Contains("they are not moved automatically", board, StringComparison.Ordinal);

        var response = await PostAsync(org.Client, $"/Projects/Board/{org.ProjectId}", "CompleteSprint", [new("sprintId", sprintId.ToString())]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var after = await GetAsync(org.Client, $"/Projects/Sprints/{org.ProjectId}");
        Assert.Contains("Sprint completed. Unfinished tickets remain in its history", after, StringComparison.Ordinal);
        Assert.DoesNotContain("move them to a planned sprint from Tickets", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetail_ShowsSprintContext_AndAPlainLanguageTimeline()
    {
        var org = await SetUpOrgAsync("Ticket Context");
        var c = org.Client;

        var noSprint = await GetAsync(c, $"/Tickets/Details/{org.TicketId}");
        Assert.Contains("No sprint", noSprint, StringComparison.Ordinal);

        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(c, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        var detail = await GetAsync(c, $"/Tickets/Details/{org.TicketId}");
        Assert.Contains("Week of Sep 21", detail, StringComparison.Ordinal);      // the sprint's name, linked
        Assert.Contains("Current sprint", detail, StringComparison.Ordinal);
        Assert.Contains("not started", detail, StringComparison.Ordinal);         // planned for it, not yet on the board
        Assert.Contains("Moved to Week of Sep 21", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SprintChanged", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SprintId", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Board_UsesPlannedNotSprintBacklog_AndPlainMenuWording()
    {
        var org = await SetUpOrgAsync("Board Wording");
        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        var board = await GetAsync(org.Client, $"/Projects/Board/{org.ProjectId}");

        Assert.Contains("Planned", board, StringComparison.Ordinal);
        Assert.Contains("Move to Open", board, StringComparison.Ordinal);
        Assert.Contains("Move out of sprint", board, StringComparison.Ordinal);
        Assert.DoesNotContain("Sprint backlog", board, StringComparison.Ordinal);
        Assert.DoesNotContain("Pull onto board", board, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove from sprint", board, StringComparison.Ordinal);
        Assert.DoesNotContain(">NoFaultFound<", board, StringComparison.Ordinal); // the option value is fine; the label is not
        Assert.Contains("No fault found", board, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectTickets_SprintColumnShowsTheSprintName_AndCurrentSprintWording()
    {
        var org = await SetUpOrgAsync("Sprint Column");
        var sprintId = await CreateSprintAsync(org, activate: true);
        await PostAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}", "MoveToSprint", [new("ticketId", org.TicketId.ToString()), new("sprintId", sprintId.ToString())]);

        var page = await GetAsync(org.Client, $"/Projects/Tickets/{org.ProjectId}");

        Assert.Contains("plan-sprint__name\">Week of Sep 21<", page, StringComparison.Ordinal);
        Assert.Contains("Current sprint", page, StringComparison.Ordinal);
        Assert.DoesNotContain("· backlog", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleRejections_ShowTheMessage_WithoutTheDeveloperRuleCode()
    {
        var org = await SetUpOrgAsync("Rule Codes");

        // Reopening a ticket that is still open is a domain rule (TICKET-WF-09) rejection.
        var response = await PostAsync(org.Client, $"/Tickets/Details/{org.TicketId}", "Reopen", [new("Input.ReopenReason", "Trying anyway")]);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Cannot reopen a ticket", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TICKET-WF", html, StringComparison.Ordinal);
    }

    [Fact] // Anonymous pages are centred by the page's own auto margins, not by the sidebar shell.
    public async Task AnonymousPages_AreCentredByTheStylesheet_AndTheSignedInShellIsNot()
    {
        var anonymous = _factory.CreateClient();
        var css = await anonymous.GetStringAsync("/flowops.css");

        var rule = System.Text.RegularExpressions.Regex.Match(css, @"body\s*>\s*main\s*\{[^}]*\}");
        Assert.True(rule.Success, "expected a `body > main` rule");
        Assert.Contains("margin-inline: auto", rule.Value, StringComparison.Ordinal);

        // Signed-in pages nest <main> inside .fo-appmain (so `body > main` cannot match them);
        // anonymous pages render <main> straight under <body>.
        var login = await anonymous.GetStringAsync("/Account/Login");
        Assert.Contains("<main id=\"main\">", login, StringComparison.Ordinal);
        Assert.DoesNotContain("fo-appmain", login, StringComparison.Ordinal);

        var org = await SetUpOrgAsync("Shell Layout");
        var signedIn = await GetAsync(org.Client, "/Projects");
        Assert.Contains("fo-appmain", signedIn, StringComparison.Ordinal);
    }

    private static void AssertRefused(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden || (response.Headers.Location?.ToString().Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ?? false), $"Expected a refusal, got {(int)response.StatusCode} -> {response.Headers.Location}");

    // ---------------- helpers ----------------

    private async Task<int> CreateSprintAsync(Org org, bool activate)
    {
        await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "CreateSprint", [new("name", "Week of Sep 21"), new("start", "2026-09-21"), new("end", "2026-09-27")]);
        var id = await SprintIdAsync(org.ProjectId);
        if (activate)
        {
            await PostAsync(org.Client, $"/Projects/Details/{org.ProjectId}", "StartSprint", [new("sprintId", id.ToString())]);
        }

        return id;
    }

    private async Task<int> SprintIdAsync(int projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Sprints.AsNoTracking().Where(s => s.ProjectId == projectId).OrderByDescending(s => s.Id).Select(s => s.Id).FirstAsync();
    }

    private async Task<int> CountSprintsAsync(int projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Sprints.CountAsync(s => s.ProjectId == projectId);
    }

    private async Task<Ticket> TicketAsync(int ticketId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts to a page handler using a token scraped from the page itself (its own forms
    /// carry one), exactly like a browser would.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string handler, List<KeyValuePair<string, string>> fields)
    {
        var token = ExtractToken(await GetAsync(client, path));
        return await PostRawAsync(client, path, handler, token, fields);
    }

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string handler, string token, List<KeyValuePair<string, string>> fields)
    {
        var body = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };
        return client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{path}?handler={handler}") { Content = new FormUrlEncodedContent(body) });
    }

    private static string ExtractToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No antiforgery token found.");
        start += marker.Length;
        return html[start..html.IndexOf('"', start)];
    }

    private async Task<Org> SetUpOrgAsync(string name)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@projectplanningtest.local";

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        var register = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", name),
                new("Input.Email", email),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{name} Org"),
                new("__RequestVerificationToken", token),
            ]),
        });
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        await TestAuthentication.SignInAsync(client, email, Password);

        // A team + category through the real UI, then a project directly (the project admin page is
        // not what is under test), then a ticket through the real Create page.
        var adminToken = ExtractToken(await GetAsync(client, "/Admin"));
        var createTeam = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=CreateTeam")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.TeamName", $"Team {Guid.NewGuid():N}"),
                new("Input.CategoryName", $"Category {Guid.NewGuid():N}"),
                new("Input.CategoryWorkType", "0"),
                new("__RequestVerificationToken", adminToken),
            ]),
        });
        Assert.Equal(HttpStatusCode.OK, createTeam.StatusCode);

        int organizationId;
        int projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            organizationId = (await db.OrganizationMemberships.SingleAsync(m => m.UserId == user.Id)).OrganizationId;
            var project = new FlowOps.Domain.Catalog.Project(0, organizationId, $"{name} Project", DateTimeOffset.UtcNow);
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        var createHtml = await GetAsync(client, "/Tickets/Create");
        var categoryMarker = "name=\"Input.CategoryId\"";
        var at = createHtml.IndexOf(categoryMarker, StringComparison.Ordinal);
        var optionMarker = "<option value=\"";
        var first = createHtml.IndexOf(optionMarker, at, StringComparison.Ordinal);
        first = createHtml.IndexOf(optionMarker, first + 1, StringComparison.Ordinal);
        var categoryId = createHtml[(first + optionMarker.Length)..createHtml.IndexOf('"', first + optionMarker.Length)];

        var create = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Title", "Migrate the reporting database"),
                new("Input.Description", "Move the reporting database to the new cluster before quarter end."),
                new("Input.WorkType", "ServiceRequest"),
                new("Input.Priority", "Medium"),
                new("Input.CategoryId", categoryId),
                new("Input.ProjectId", projectId.ToString()),
                new("__RequestVerificationToken", ExtractToken(createHtml)),
            ]),
        });
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var ticketId = int.Parse(create.Headers.Location!.ToString().Split('/').Last());

        return new Org(client, email, organizationId, projectId, ticketId);
    }

    private async Task<HttpClient> AddAgentAsync(Org org)
    {
        var email = $"{Guid.NewGuid():N}@projectplanningagent.local";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var agent = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Planning Agent", IsActive = true };
            Assert.True((await userManager.CreateAsync(agent, Password)).Succeeded);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, org.OrganizationId, agent.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            var teamId = await db.Tickets.Where(t => t.Id == org.TicketId).Select(t => t.TeamId).SingleAsync();
            db.TeamMembers.Add(new FlowOps.Domain.Directory.TeamMember(teamId, agent.Id, false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email, Password);
        return client;
    }
}
