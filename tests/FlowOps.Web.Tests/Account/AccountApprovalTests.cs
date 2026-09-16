using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Account;

/// <summary>
/// Phase 24A (ADR-0024): the account-approval gate itself, over real HTTP — a Pending account must
/// have genuinely zero normal FlowOps access, enforced by the authentication pipeline (a real
/// Identity lockout), never by hiding a link or redirecting after the fact.
/// </summary>
public sealed class AccountApprovalTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public AccountApprovalTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Theory] // spec §21: a Pending caller (who was never signed in at all, since login itself is
             // refused) cannot reach any protected route — same outcome as any other anonymous
             // visitor, via the ordinary AuthorizeFolder("/") gate.
    [InlineData("/")]
    [InlineData("/Admin")]
    [InlineData("/Platform")]
    [InlineData("/Tickets")]
    public async Task PendingAccount_NeverAuthenticated_ProtectedRoutesRedirectToLogin(string path)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await RegisterAsync(client, "Pending Route Check");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact] // The critical guarantee: correct credentials for a Pending account are genuinely
           // refused by SignInManager (a real Identity lockout), not merely hidden by the UI.
    public async Task PendingAccount_CorrectCredentials_LoginStillFails()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = await RegisterAsync(client, "Pending Login Refusal");

        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Login");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, never redirected — no session established
        // Phase 24A-Extension (ADR-0025): a distinct message, safe here because the password was
        // genuinely correct (see LoginModel's own remarks on why this doesn't weaken enumeration
        // protection for a caller who does NOT already know the password).
        Assert.Contains("awaiting approval", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovedAccount_CanThenLoginAndUseFlowOps()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = await RegisterAsync(client, "Approved Then Login");

        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        await TestAuthentication.SignInAsync(client, email, Password);

        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains(email, await dashboard.Content.ReadAsStringAsync());
    }

    private static async Task<string> RegisterAsync(HttpClient client, string fullName)
    {
        var email = $"{Guid.NewGuid():N}@accountapprovaltest.local";
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", email),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName} Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/PendingApproval", response.Headers.Location?.ToString());
        return email;
    }
}
