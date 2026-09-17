using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 25: Create Ticket's optional Planned Start / Due Date fields, end to end over real HTTP.
/// Uses the unthrottled registration flow (not <see cref="TestAuthentication.SignInAsync"/>) for
/// the same reason <see cref="CreateTicketEmptyStateTests"/> does — signing in repeatedly would
/// compete for the shared login rate-limit budget with <see cref="TicketPagesTests"/>.
/// </summary>
public sealed class CreateTicketPlanningDatesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public CreateTicketPlanningDatesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateTicket_WithBothPlanningDates_PersistsAndDisplaysBoth()
    {
        var client = await RegisterAsync("Planning Dates Both");
        await CreateTeamAsync(client);

        var location = await CreateTicketAsync(client, plannedStart: "2026-09-18T09:00", dueDate: "2026-09-20T17:00");
        Assert.NotNull(location);

        var detailResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var html = await detailResponse.Content.ReadAsStringAsync();

        Assert.Contains("2026-09-18 09:00", html, StringComparison.Ordinal);
        Assert.Contains("2026-09-20 17:00", html, StringComparison.Ordinal);
        Assert.Contains("Planned start", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateTicket_WithNoPlanningDates_ShowsNotSetForBoth()
    {
        var client = await RegisterAsync("Planning Dates None");
        await CreateTeamAsync(client);

        var location = await CreateTicketAsync(client, plannedStart: null, dueDate: null);
        Assert.NotNull(location);

        var detailResponse = await client.GetAsync(location);
        var html = await detailResponse.Content.ReadAsStringAsync();

        Assert.Contains("Not set", html, StringComparison.Ordinal);
    }

    [Fact] // TICKET-INV-11, surfaced as an inline validation error, not a 500 and not a silent
           // ticket creation with a nonsensical date pair.
    public async Task CreateTicket_PlannedStartAfterDueDate_IsRejectedWithValidationError()
    {
        var client = await RegisterAsync("Planning Dates Invalid");
        await CreateTeamAsync(client);

        var html = await CreateTicketAndGetPageAsync(client, plannedStart: "2026-09-25T09:00", dueDate: "2026-09-20T17:00");

        Assert.Contains("Planned start date must be on or before the due date", html, StringComparison.OrdinalIgnoreCase);

        // The ticket must not have been created — the work queue stays empty of it.
        var queueResponse = await client.GetAsync("/Tickets");
        var queueHtml = await queueResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("FO-", queueHtml, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static async Task<string?> CreateTicketAsync(HttpClient client, string? plannedStart, string? dueDate)
    {
        var response = await PostCreateAsync(client, plannedStart, dueDate);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location?.ToString();
    }

    private static async Task<string> CreateTicketAndGetPageAsync(HttpClient client, string? plannedStart, string? dueDate)
    {
        var response = await PostCreateAsync(client, plannedStart, dueDate);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered form, not a redirect
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string? plannedStart, string? dueDate)
    {
        var createHtml = await GetHtmlAsync(client, "/Tickets/Create");
        var token = ExtractAntiForgeryToken(createHtml);
        var categoryId = ExtractFirstCategoryId(createHtml);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Input.Title", "Migrate the reporting database"),
            new("Input.Description", "Move the reporting database to the new cluster before the quarter ends."),
            new("Input.WorkType", "ServiceRequest"),
            new("Input.Priority", "Medium"),
            new("Input.CategoryId", categoryId),
            new("__RequestVerificationToken", token),
        };
        if (plannedStart is not null)
        {
            fields.Add(new("Input.PlannedStartDate", plannedStart));
        }

        if (dueDate is not null)
        {
            fields.Add(new("Input.DueDate", dueDate));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        return await client.SendAsync(request);
    }

    private async Task CreateTeamAsync(HttpClient client)
    {
        var adminHtml = await GetHtmlAsync(client, "/Admin");
        var token = ExtractAntiForgeryToken(adminHtml);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=CreateTeam")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.TeamName", $"Team {Guid.NewGuid():N}"),
                new("Input.CategoryName", $"Category {Guid.NewGuid():N}"),
                new("Input.CategoryWorkType", "0"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No antiforgery token found in response.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private static string ExtractFirstCategoryId(string html)
    {
        var marker = "name=\"Input.CategoryId\"";
        var selectStart = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(selectStart >= 0, "Category select not found.");
        var optionMarker = "<option value=\"";
        var firstRealOption = html.IndexOf(optionMarker, selectStart + marker.Length, StringComparison.Ordinal);
        firstRealOption = html.IndexOf(optionMarker, firstRealOption + 1, StringComparison.Ordinal); // skip the blank "Select a category" option
        var start = firstRealOption + optionMarker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private async Task<HttpClient> RegisterAsync(string fullName)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@planningdatestest.local";

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", email),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName}'s Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        await TestAuthentication.SignInAsync(client, email, Password);

        return client;
    }
}
