using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 15: forwarded-header/cookie-security behavior (ADR-0013) and HSTS, verified against real
/// HTTP responses — not asserted from the presence of <c>UseForwardedHeaders()</c> in
/// <c>Program.cs</c> alone. Uses <see cref="FlowOpsWebApplicationFactory"/> because rendering the
/// Login page issues a real antiforgery cookie through the real Identity/Data Protection stack.
/// </summary>
public sealed class Phase15DeploymentTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public Phase15DeploymentTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact] // The actual goal ADR-0013 exists for: a forwarded HTTPS request gets a Secure cookie.
    public async Task ForwardedHttpsRequest_ReceivesSecureAntiforgeryCookie()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? string.Join(";", cookies) : string.Empty;
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Without the forwarded header, Kestrel genuinely sees plain HTTP — SameAsRequest correctly omits Secure.
    public async Task PlainHttpRequest_DoesNotReceiveSecureAntiforgeryCookie()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? string.Join(";", cookies) : string.Empty;
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_EmitsHstsHeader_OnAForwardedHttpsRequest()
    {
        await using var productionFactory = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var client = productionFactory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");
        // ASP.NET Core's HstsMiddleware deliberately never adds the header for a "localhost"
        // Host (a built-in safety exclusion so HSTS can never lock a developer out of local
        // testing) — WebApplicationFactory's default Host is literally "localhost", so a
        // realistic non-loopback Host is required to actually exercise the middleware.
        request.Headers.Host = "flowops.example.com";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Development_DoesNotEmitHstsHeader()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact] // CLAUDE.md §14: a misconfigured demo deployment must fail fast at startup, not seed
           // accounts nobody can log into.
    public void DemoEnabled_WithoutPersonaPassword_FailsFastAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:FlowOps", "Host=localhost;Port=5433;Database=flowops_dev;Username=flowops;Password=flowops_dev_password");
            builder.UseSetting("FlowOps:Demo:Enabled", "true");
            // FlowOps:Demo:PersonaPassword deliberately left unset.
        });

        // Accessing Services triggers host construction, including options validation.
        var exception = Record.Exception(() => factory.Services.GetService(typeof(object)));

        Assert.NotNull(exception);
        Assert.Contains("Demo:PersonaPassword", exception.ToString(), StringComparison.Ordinal);
    }
}
