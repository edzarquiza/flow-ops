using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// Signs a test client in through the real login form — a genuine POST with a genuine antiforgery
/// token, never a stubbed authentication handler (CLAUDE.md §15).
/// </summary>
/// <remarks>
/// Login POSTs are rate limited (5/min/IP in production, CLAUDE.md §12) but
/// <see cref="FlowOpsWebApplicationFactory"/> raises that limit for its own in-process test host
/// (see that class's own comment) precisely because Phase 24A's approval gate means a Web.Tests
/// fixture now needs a genuine login POST per registered account, not just per test.
/// </remarks>
internal static partial class TestAuthentication
{
    public static Task<HttpClient> SignInAsync(HttpClient client, string email) =>
        SignInAsync(client, email, TestUsers.Password);

    public static async Task<HttpClient> SignInAsync(HttpClient client, string email, string password)
    {
        var token = await AntiForgeryTokenAsync(client, "/Account/Login");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", password),
                new("__RequestVerificationToken", token),
            ]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>
    /// Phase 24A (ADR-0024): registration alone no longer grants a session — a self-registered
    /// account starts Pending. This is the standard Web.Tests shortcut for "approve it as a
    /// Platform Admin would, without exercising /Platform/Users itself" (the same direct-DB-scope
    /// pattern Platform test classes already use for GrantPlatformAdminAsync-style setup that is
    /// not the thing under test). Tests of the approval workflow itself go through the real
    /// /Platform/Users/Details POST instead.
    /// </summary>
    public static async Task ApproveRegistrationAsync(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.RegistrationApprovedAt = DateTimeOffset.UtcNow;
        user.LockoutEnabled = false;
        user.LockoutEnd = null;
        await db.SaveChangesAsync();
    }

    public static async Task<string> AntiForgeryTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        var tag = AntiForgeryInputTagPattern().Match(html);
        Assert.True(tag.Success, $"No antiforgery input tag found on {path} (status {response.StatusCode}).");

        var value = ValueAttributePattern().Match(tag.Value);
        Assert.True(value.Success, $"Antiforgery input tag on {path} had no value attribute.");

        return value.Groups[1].Value;
    }

    [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*>""")]
    private static partial Regex AntiForgeryInputTagPattern();

    [GeneratedRegex(""""value="([^"]*)"""")]
    private static partial Regex ValueAttributePattern();
}
