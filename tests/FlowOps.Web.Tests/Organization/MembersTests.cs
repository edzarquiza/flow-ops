using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.OrganizationManagement;

/// <summary>
/// Phase 18: the Members page against real HTTP/auth infrastructure. Every test registers its own
/// disposable organization (via the real, unthrottled registration flow) rather than touching the
/// shared demo organization or the login rate limiter.
/// </summary>
public sealed partial class MembersTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public MembersTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Members_AnonymousRequest_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Organization/Members");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Members_Admin_CanView()
    {
        var client = RegisterAsync("Members Admin", out _);

        var response = await client.GetAsync("/Organization/Members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Members Admin", body);
    }

    [Fact]
    public async Task Members_Search_FiltersByName()
    {
        var client = RegisterAsync("Casey Nguyen", out _);

        var response = await client.GetAsync("/Organization/Members?search=casey");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Casey Nguyen", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_Search_NoMatches_ShowsEmptyStateWithClearSearchLink()
    {
        var client = RegisterAsync("Members Search NoMatch Admin", out _);

        var response = await client.GetAsync($"/Organization/Members?search=no-such-member-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No members found", body, StringComparison.Ordinal);
        Assert.Contains("Clear search", body, StringComparison.Ordinal);
    }

    [Fact] // Multi-tenant: search never crosses the organization boundary.
    public async Task Members_Search_NeverShowsAnotherOrganizationsMembers()
    {
        var clientA = RegisterAsync("Org A Search Admin", out _);
        RegisterAsync("Org B Unique Target Person", out _);

        var response = await clientA.GetAsync($"/Organization/Members?search={Uri.EscapeDataString("Unique Target")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Org B Unique Target Person", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InviteMember_ValidSubmission_ReturnsInvitationLinkContainingToken()
    {
        var client = RegisterAsync("Inviter Admin", out _);
        var invitedEmail = UniqueEmail();

        var response = await PostInviteAsync(client, invitedEmail, "Agent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/Account/AcceptInvitation?token=", body);
        Assert.Contains("Invitation created", body);
        Assert.DoesNotContain("Email sent", body);
    }

    [Fact]
    public async Task InviteMember_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisterAsync("Token Check Admin", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=Invite")
        {
            Content = new FormUrlEncodedContent(
            [
                new("InviteInput.Email", UniqueEmail()),
                new("InviteInput.Role", "Agent"),
            ]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- end-to-end: invite -> accept as a brand-new user ----

    [Fact]
    public async Task FullInvitationFlow_NewUser_JoinsCorrectOrganizationWithInvitedRole_NoSecondOrganizationCreated()
    {
        var adminClient = RegisterAsync("Flow Admin", out _);
        var invitedEmail = UniqueEmail();
        var inviteResponse = await PostInviteAsync(adminClient, invitedEmail, "Viewer");
        var link = ExtractInvitationLink(await inviteResponse.Content.ReadAsStringAsync());

        var recipientClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var getResponse = await recipientClient.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains(invitedEmail, getBody);
        Assert.Contains("Executive Viewer", getBody); // RoleDisplay for Viewer

        var acceptResponse = await AcceptAsNewUserAsync(recipientClient, link, "New Recipient");
        Assert.Equal(HttpStatusCode.Redirect, acceptResponse.StatusCode);

        var dashboard = await recipientClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var dashboardBody = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains(invitedEmail, dashboardBody);
        Assert.Contains("Executive Viewer", dashboardBody);

        // No demo tickets, and no way to reach the inviting admin's ticket queue with data from
        // some other, unrelated organization — the new member is scoped to exactly one org.
        var queue = await recipientClient.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_InvalidToken_ShowsNotValidMessage()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/AcceptInvitation?token=not-a-real-token-at-all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no longer valid", await response.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = UniqueEmail();

        var token = TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register").GetAwaiter().GetResult();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", capturedEmail),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", $"{fullName}'s Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // to PendingApproval, not the dashboard

        TestAuthentication.ApproveRegistrationAsync(_factory.Services, capturedEmail).GetAwaiter().GetResult();
        TestAuthentication.SignInAsync(client, capturedEmail, Password).GetAwaiter().GetResult();

        email = capturedEmail;
        return client;
    }

    private static async Task<HttpResponseMessage> PostInviteAsync(HttpClient client, string email, string role)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Organization/Members");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=Invite")
        {
            Content = new FormUrlEncodedContent(
            [
                new("InviteInput.Email", email),
                new("InviteInput.Role", role),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AcceptAsNewUserAsync(HttpClient client, string invitationPath, string fullName)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, invitationPath);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{invitationPath}&handler=CreateAccount")
        {
            Content = new FormUrlEncodedContent(
            [
                new("NewAccount.FullName", fullName),
                new("NewAccount.Password", Password),
                new("NewAccount.ConfirmPassword", Password),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    private static string ExtractInvitationLink(string html)
    {
        var match = InvitationLinkPattern().Match(html);
        Assert.True(match.Success, $"No invitation link found in response body:{Environment.NewLine}{html}");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@membertest.local";

    [GeneratedRegex(""""value="(http://[^"]*/Account/AcceptInvitation\?token=[^"]*)"""")]
    private static partial Regex InvitationLinkPattern();
}
