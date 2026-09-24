using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase F1-B (hardening): CLAUDE.md §12 says "login and password endpoints (5/min/IP)," but only
/// the login page actually carried <c>[EnableRateLimiting("login")]</c> before this phase —
/// registration and password reset were unthrottled. These tests prove the new "sensitive" policy
/// genuinely rejects the caller with 429 once its configured limit is exceeded, not merely that the
/// attribute is present in source. Each test lowers the shared factory's raised test-only override
/// (<see cref="FlowOpsWebApplicationFactory"/> sets it to 1000 by default, the same reasoning as its
/// existing login override) back down to a small, deterministic number via its own
/// <c>WithWebHostBuilder</c> call, mirroring <c>Phase12SecurityTests</c>' own one-off override
/// pattern — a dedicated low-limit factory per test, not a shared, order-sensitive global budget.
/// </summary>
public sealed class RateLimitingTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public RateLimitingTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_IsRateLimited_AfterConfiguredPermitLimit()
    {
        var lowLimitFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Sensitive:PermitLimitPerMinute", "2"));
        var client = lowLimitFactory.CreateClient(new() { AllowAutoRedirect = false });

        // Two requests within the configured limit — neither is rejected by the rate limiter
        // itself (a validation failure from a deliberately-invalid body is fine; 429 is not).
        for (var i = 0; i < 2; i++)
        {
            var response = await PostRegisterAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        // The third request in the same window is rejected by the rate limiter before it ever
        // reaches the registration handler.
        var throttled = await PostRegisterAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_IsRateLimited_AfterConfiguredPermitLimit()
    {
        var lowLimitFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Sensitive:PermitLimitPerMinute", "2"));
        var client = lowLimitFactory.CreateClient(new() { AllowAutoRedirect = false });

        for (var i = 0; i < 2; i++)
        {
            var response = await PostResetPasswordAsync(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var throttled = await PostResetPasswordAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact] // The login and sensitive policies are independent partitions/limits, not one shared bucket.
    public async Task Login_RateLimit_IsUnaffectedByTheSensitivePolicysOwnLimit()
    {
        var lowLimitFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Sensitive:PermitLimitPerMinute", "1"));
        var client = lowLimitFactory.CreateClient(new() { AllowAutoRedirect = false });

        // Exhaust the (now very low) sensitive limit via Register.
        await PostRegisterAsync(client);
        var throttledRegister = await PostRegisterAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledRegister.StatusCode);

        // Login (its own "login" policy, still at the factory's default raised test limit) is
        // untouched by the sensitive policy's bucket being exhausted.
        var loginResponse = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRegisterAsync(HttpClient client)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", "Rate Limit Test"),
                new("Input.Email", $"{Guid.NewGuid():N}@ratelimittest.local"),
                new("Input.Password", "A-Genuinely-Str0ng-Pw!"),
                new("Input.ConfirmPassword", "A-Genuinely-Str0ng-Pw!"),
                new("Input.OrganizationName", "Rate Limit Test Org"),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostResetPasswordAsync(HttpClient client)
    {
        // The page's own generic "no longer valid" handling means a fabricated userId/token pair
        // is enough to reach and exercise the rate-limited handler — the limiter runs before the
        // handler needs a real token, and this test cares about the 429 only, not the reset outcome.
        var path = $"/Account/ResetPassword?userId={Guid.NewGuid()}&token=not-a-real-token";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.NewPassword", "A-Genuinely-Str0ng-Pw!"),
                new("Input.ConfirmPassword", "A-Genuinely-Str0ng-Pw!"),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }
}
