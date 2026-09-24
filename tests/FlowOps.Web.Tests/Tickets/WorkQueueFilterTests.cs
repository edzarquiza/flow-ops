using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 30B over real HTTP: the Work Queue's Status/Priority/"Assigned to me" filters — each one
/// narrows the visible list, they compose with each other, and the filter-bar round-trips its own
/// selections back into the rendered <c>&lt;select&gt;</c>/checkbox state.
/// </summary>
public sealed class WorkQueueFilterTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public WorkQueueFilterTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task StatusFilter_NarrowsToOnlyThatStatus()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var openTitle = $"Still open {suffix}";
        var assignedTitle = $"Now assigned {suffix}";
        await _factory.CreateTicketAsync(TestUsers.ManagerEmail, openTitle);
        var assignedId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, assignedTitle);

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);
        await Post(client, assignedId, "Assign");

        var html = await GetAsync(client, "/Tickets?status=Assigned");
        Assert.Contains(assignedTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain(openTitle, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignedToMe_NarrowsToTheCallersOwnAssignments()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var mineTitle = $"Mine {suffix}";
        var unassignedTitle = $"Unassigned {suffix}";
        var mineId = await _factory.CreateTicketAsync(TestUsers.ManagerEmail, mineTitle);
        await _factory.CreateTicketAsync(TestUsers.ManagerEmail, unassignedTitle);

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);
        await Post(client, mineId, "Assign");

        var html = await GetAsync(client, "/Tickets?assignedToMe=true");
        Assert.Contains(mineTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain(unassignedTitle, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterBar_RoundTripsTheSelectedStatusAndPriorityAndAssignedToMeState()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var html = await GetAsync(client, "/Tickets?status=Open&priority=High&assignedToMe=true");

        Assert.Contains("<option value=\"Open\" selected", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"High\" selected", html, StringComparison.Ordinal);
        Assert.Contains("id=\"assignedToMe\" type=\"checkbox\" name=\"assignedToMe\" value=\"true\" checked", html, StringComparison.Ordinal);
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
