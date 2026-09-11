using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// Signs a test client in through the real login form — a genuine POST with a genuine antiforgery
/// token, never a stubbed authentication handler (CLAUDE.md §15).
/// </summary>
/// <remarks>
/// Login POSTs are rate limited to 5 per minute per IP (CLAUDE.md §12), and every request from a
/// <c>WebApplicationFactory</c> client shares one partition. Test classes using this helper must
/// therefore stay at or under five sign-ins per factory instance.
/// </remarks>
internal static partial class TestAuthentication
{
    public static async Task<HttpClient> SignInAsync(HttpClient client, string email)
    {
        var token = await AntiForgeryTokenAsync(client, "/Account/Login");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", TestUsers.Password),
                new("__RequestVerificationToken", token),
            ]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
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
