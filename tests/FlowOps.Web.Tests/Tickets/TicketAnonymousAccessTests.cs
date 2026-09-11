using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Every ticket page is behind the authentication gate. Uses the plain
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with no Testcontainers database for the same
/// reason <c>AnonymousAccessTests</c> does: the cookie challenge happens in middleware, before any
/// PageModel or query runs, so these assertions never touch PostgreSQL.
/// </summary>
public sealed class TicketAnonymousAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TicketAnonymousAccessTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(_ => { });

    [Theory]
    [InlineData("/Tickets")]
    [InlineData("/Tickets/Create")]
    [InlineData("/Tickets/Details/1")]
    public async Task TicketPages_Anonymous_RedirectToLogin(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // Workflow POSTs are behind the same gate — the challenge precedes any handler.
    public async Task WorkflowPost_Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Details/1?handler=StartWork")
        {
            Content = new FormUrlEncodedContent([]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }
}
