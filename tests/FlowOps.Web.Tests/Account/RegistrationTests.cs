using System.Net;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Account;

/// <summary>
/// Phase 17: public registration against real HTTP/auth infrastructure (CLAUDE.md §15). Every
/// registration here is a genuine, disposable account/organization created through the real form
/// POST — nothing here touches the seeded demo organization/personas.
/// </summary>
public sealed class RegistrationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public RegistrationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisterPage_IsAccessibleAnonymously()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/Register");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // Phase 24A (ADR-0024): registration no longer signs the caller in — the account starts
           // Pending, and only lands on /Account/PendingApproval, never the dashboard.
    public async Task Register_ValidSubmission_RedirectsToPendingApproval_NeverSignsIn()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = UniqueEmail();

        var response = await RegisterAsync(client, "New Admin", email, "A-Genuinely-Str0ng-Pw!", "New Admin's Org");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/PendingApproval", response.Headers.Location?.ToString());

        var pendingPage = await client.GetAsync("/Account/PendingApproval");
        Assert.Equal(HttpStatusCode.OK, pendingPage.StatusCode);
        Assert.Contains("awaiting approval", await pendingPage.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Never signed in: the dashboard redirects to Login, not to itself.
        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/Account/Login", dashboard.Headers.Location?.ToString());
    }

    [Fact] // The critical authentication-boundary guarantee (spec §5): a Pending account's
           // credentials are genuinely refused by the real sign-in pipeline, not just hidden by UI.
    public async Task Register_ThenLoginBeforeApproval_IsRejected()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = UniqueEmail();
        const string password = "A-Genuinely-Str0ng-Pw!";
        await RegisterAsync(client, "Pending Login Attempt", email, password, "Pending Login Attempt Org");

        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await client.SendAsync(loginRequest);

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode); // re-rendered with an error, not redirected
        var body = await loginResponse.Content.ReadAsStringAsync();
        // Phase 24A-Extension (ADR-0025, spec §23): a distinct "awaiting approval" message — safe
        // here specifically because the submitted password was genuinely correct (LoginModel only
        // reveals this once CheckPasswordAsync confirms it), so this caller already proved they
        // hold this account's real credentials rather than merely guessing an email address.
        Assert.Contains("awaiting approval", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invalid login attempt", body, StringComparison.Ordinal);

        // Never signed in even though the message is specific: the session boundary is unchanged.

        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/Account/Login", dashboard.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Register_NewOrganization_DashboardShowsNoDemoTickets_OnceApprovedAndSignedIn()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = UniqueEmail();
        const string password = "A-Genuinely-Str0ng-Pw!";
        await RegisterAsync(client, "Fresh Org Admin", email, password, "Fresh Organization");
        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, email);
        await TestAuthentication.SignInAsync(client, email, password);

        var queue = await client.GetAsync("/Tickets");
        var body = await queue.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        // The seeded demo ticket's own subject line (TestReferenceData) must never appear for a
        // brand-new, unrelated organization.
        Assert.DoesNotContain("VPN client will not connect", body);
    }

    [Fact]
    public async Task Register_MissingAntiforgeryToken_IsRejected()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", "No Token"),
                new("Input.Email", UniqueEmail()),
                new("Input.Password", "A-Genuinely-Str0ng-Pw!"),
                new("Input.ConfirmPassword", "A-Genuinely-Str0ng-Pw!"),
                new("Input.OrganizationName", "No Token Org"),
            ]),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReRendersFormWithError_AndDoesNotSignIn()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = UniqueEmail();
        await RegisterAsync(client, "First", email, "A-Genuinely-Str0ng-Pw!", "First Org");

        var secondClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await RegisterAsync(secondClient, "Second", email, "A-Genuinely-Str0ng-Pw!", "Second Org");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, not redirected
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("already", body, StringComparison.OrdinalIgnoreCase);

        var dashboard = await secondClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode); // never signed in
        Assert.Contains("/Account/Login", dashboard.Headers.Location?.ToString());
    }

    [Fact] // Phase 22 (SEC-1): the explicit password policy (min length 12 + complexity) is
           // enforced on registration, not left to Identity's weaker 6-character default.
    public async Task Register_PasswordShorterThanPolicyMinimum_ReRendersFormWithError_AndDoesNotSignIn()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = UniqueEmail();

        var response = await RegisterAsync(client, "Weak Password", email, "Sh0rt!", "Weak Password Org");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, not redirected
        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode); // never signed in
    }

    private static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string fullName, string email, string password, string organizationName)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", email),
                new("Input.Password", password),
                new("Input.ConfirmPassword", password),
                new("Input.OrganizationName", organizationName),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@register.test.local";
}
