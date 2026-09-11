using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowOps.Web.Tests.Authentication;

/// <summary>
/// Uses the plain <see cref="WebApplicationFactory{TEntryPoint}"/> with no Testcontainers-backed
/// database — these assertions never need PostgreSQL (an unauthenticated cookie-auth challenge,
/// and the liveness check, both resolve with zero database access), so they execute regardless
/// of Docker availability.
/// </summary>
public sealed class AnonymousAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AnonymousAccessTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact] // "authenticated request handling" boundary — anonymous access is redirected
    public async Task GetIndex_Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // AUTH-RULE-01 coarse gate applies to the Admin-only area too
    public async Task GetAdmin_Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Admin");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // Login page itself must remain reachable anonymously
    public async Task GetLogin_Anonymous_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // liveness never touches the database
    public async Task GetHealth_ReturnsHealthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
