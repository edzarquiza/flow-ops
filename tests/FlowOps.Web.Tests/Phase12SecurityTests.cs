using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 12: the security response headers, the global exception handler, and the antiforgery
/// guarantee — all verified against a real HTTP response, not asserted from configuration alone.
/// Uses the plain <see cref="WebApplicationFactory{TEntryPoint}"/> (no Testcontainers) throughout:
/// none of these three concerns touch ticket data.
/// </summary>
public sealed class Phase12SecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public Phase12SecurityTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(_ => { });

    [Fact]
    public async Task Response_CarriesTheRequiredSecurityHeaders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", GetHeader(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", GetHeader(response, "Referrer-Policy"));
        Assert.Equal("DENY", GetHeader(response, "X-Frame-Options"));
        Assert.True(response.Headers.Contains("Permissions-Policy") || response.Content.Headers.Contains("Permissions-Policy"));
        var csp = GetHeader(response, "Content-Security-Policy");
        Assert.NotNull(csp);
        // Written for the app's actual surface (no JS, no CDN, local CSS only) — not merely present.
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact] // The same middleware runs for a 429; the headers are not something only "happy path" responses get.
    public async Task SecurityHeaders_AreAlsoPresentOnAHealthCheckResponse()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", GetHeader(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", GetHeader(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task UnhandledException_ProducesFriendlyPageWithCorrelationId_AndNoStackTrace()
    {
        var throwingFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Testing:EnableDiagnosticThrowEndpoint", "true"));
        var client = throwingFactory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Testing/Throw");

        // ExceptionHandlerMiddleware sets the response status to 500 before re-executing the
        // pipeline against "/Error"; the friendly page itself renders normally underneath that.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", body, StringComparison.Ordinal);
        Assert.Contains("Reference:", body, StringComparison.Ordinal);

        // No exception detail of any kind reaches the client.
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Deliberate exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at FlowOps.", body, StringComparison.Ordinal); // no stack frame lines
        Assert.DoesNotContain(":\\", body, StringComparison.Ordinal); // no filesystem path
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
    }

    [Fact] // The diagnostic endpoint itself must not exist unless a test explicitly opts in.
    public async Task DiagnosticThrowEndpoint_DoesNotExist_WhenNotExplicitlyEnabled()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Testing/Throw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // Distinct from the existing forged-authorization tests: this proves the antiforgery
           // mechanism itself rejects a state-changing POST that carries no token at all, before
           // any authorization or business logic runs — not that an authorized-but-wrong-role
           // caller is refused despite holding a valid one.
    public async Task StateChangingPost_WithNoAntiforgeryToken_IsRejected()
    {
        using var webApplicationFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(_ => { });
        var client = webApplicationFactory.CreateClient(new() { AllowAutoRedirect = false });

        // Establish the antiforgery cookie (issued on any GET) without ever reading the matching
        // request-verification token out of the form — the whole point of this test.
        await client.GetAsync("/Account/Login");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", TestUsers.AgentEmail),
                new("Input.Password", TestUsers.Password),
                // Deliberately no "__RequestVerificationToken" field at all.
            ]),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
