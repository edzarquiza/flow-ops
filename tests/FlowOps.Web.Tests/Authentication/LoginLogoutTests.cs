using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Authentication;

/// <summary>
/// Real authentication against a real, Testcontainers-provisioned PostgreSQL database
/// (CLAUDE.md §15: "Do not stub the auth handler"). Requires Docker — see
/// <see cref="FlowOpsWebApplicationFactory"/>'s doc comment for what happens without it.
/// </summary>
public sealed partial class LoginLogoutTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public LoginLogoutTests(FlowOpsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_ValidCredentials_EstablishesAuthenticatedSession()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var (token, antiforgeryCookie) = await GetAntiForgeryTokenAsync(client);

        var response = await PostLoginAsync(client, TestUsers.AdminEmail, TestUsers.Password, token, antiforgeryCookie);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains(TestUsers.AdminEmail, await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_InvalidPassword_IsRejectedWithGenericMessage()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var (token, antiforgeryCookie) = await GetAntiForgeryTokenAsync(client);

        var response = await PostLoginAsync(client, TestUsers.AgentEmail, "wrong-password", token, antiforgeryCookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // page re-rendered, not redirected
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid login attempt", body);

        // Confirm no session was actually established.
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Account/Login", home.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Logout_RemovesAuthenticatedState()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginAsAsync(client, TestUsers.ManagerEmail);

        var (logoutToken, _) = await GetAntiForgeryTokenAsync(client, "/");
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Logout")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", logoutToken)]),
        };
        var logoutResponse = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.Redirect, logoutResponse.StatusCode);

        var afterLogout = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Contains("/Account/Login", afterLogout.Headers.Location?.ToString());
    }

    [Fact] // AUTH-RULE-01 coarse gate: Admin-only area rejects an authenticated non-Admin
    public async Task Admin_AsNonAdminRole_IsForbidden()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginAsAsync(client, TestUsers.AgentEmail);

        var response = await client.GetAsync("/Admin");

        // Identity's default AccessDeniedPath redirect for an authenticated-but-forbidden user.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // AUTH-RULE-01 coarse gate: Admin-only area accepts an authenticated Admin
    public async Task Admin_AsAdmin_ReturnsOk()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginAsAsync(client, TestUsers.AdminEmail);

        var response = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task LoginAsAsync(HttpClient client, string email)
    {
        var (token, antiforgeryCookie) = await GetAntiForgeryTokenAsync(client);
        var response = await PostLoginAsync(client, email, TestUsers.Password, token, antiforgeryCookie);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password, string token, string? antiforgeryCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", password),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    private static async Task<(string Token, string? Cookie)> GetAntiForgeryTokenAsync(HttpClient client, string path = "/Account/Login")
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        // Attribute order within the rendered <input> tag is an ASP.NET Core implementation
        // detail, so the tag is matched first, then the value attribute is pulled out of it
        // independently of where "name" and "value" fall relative to each other.
        var tagMatch = AntiForgeryInputTagPattern().Match(html);
        if (!tagMatch.Success)
        {
            throw new InvalidOperationException($"No antiforgery input tag found on {path}.");
        }

        var valueMatch = ValueAttributePattern().Match(tagMatch.Value);
        if (!valueMatch.Success)
        {
            throw new InvalidOperationException($"Antiforgery input tag on {path} had no value attribute.");
        }

        return (valueMatch.Groups[1].Value, null);
    }

    [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*>""")]
    private static partial Regex AntiForgeryInputTagPattern();

    [GeneratedRegex(""""value="([^"]*)"""")]
    private static partial Regex ValueAttributePattern();
}
