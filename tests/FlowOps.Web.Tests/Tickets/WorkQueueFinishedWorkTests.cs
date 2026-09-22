using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 29B over real HTTP: the Work Queue opens on unfinished work, finished tickets stay one
/// click away (the "Show finished" switch, a search, or the resolved KPI filter), and a caller still
/// only ever sees the tickets their team membership allows.
/// </summary>
public sealed class WorkQueueFinishedWorkTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public WorkQueueFinishedWorkTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DefaultQueueIsActiveWork_AndFinishedTicketsRemainDiscoverable()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var openTitle = $"Keep me visible {suffix}";
        var finishedTitle = $"Already resolved {suffix}";
        await _factory.CreateTicketAsync(TestUsers.ManagerEmail, openTitle);
        var finishedId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, finishedTitle);

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);
        await Post(client, finishedId, "Assign");
        await Post(client, finishedId, "StartWork");
        await Post(client, finishedId, "Resolve", new("Input.ResolutionCode", "Fixed"), new("Input.ResolutionNotes", "Reseated the cable and confirmed with the user."));

        // Default: unfinished work only, and the page says so.
        var active = await GetAsync(client, "/Tickets");
        Assert.Contains(openTitle, active, StringComparison.Ordinal);
        Assert.DoesNotContain(finishedTitle, active, StringComparison.Ordinal);
        Assert.Contains("Show finished", active, StringComparison.Ordinal);
        Assert.Contains("Active work", active, StringComparison.Ordinal);

        // The switch brings finished tickets back, alongside the active ones.
        var withFinished = await GetAsync(client, "/Tickets?showFinished=true");
        Assert.Contains(openTitle, withFinished, StringComparison.Ordinal);
        Assert.Contains(finishedTitle, withFinished, StringComparison.Ordinal);

        // A search and the "resolved" KPI filter define their own population: finished tickets are included.
        Assert.Contains(finishedTitle, await GetAsync(client, $"/Tickets?search={finishedTitle.Replace(" ", "+")}"), StringComparison.Ordinal);
        Assert.Contains(finishedTitle, await GetAsync(client, "/Tickets?filter=ResolvedRecently"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewerOutsideTheTeam_StillSeesNothing_WithOrWithoutTheSwitch()
    {
        var title = $"Not for outsiders {Guid.NewGuid():N}";
        await _factory.CreateTicketAsync(TestUsers.ManagerEmail, title);

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        Assert.DoesNotContain(title, await GetAsync(client, "/Tickets"), StringComparison.Ordinal);
        Assert.DoesNotContain(title, await GetAsync(client, "/Tickets?showFinished=true"), StringComparison.Ordinal);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
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
