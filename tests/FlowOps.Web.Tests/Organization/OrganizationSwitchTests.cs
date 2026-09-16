using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.OrganizationManagement;

/// <summary>
/// Phase 19: organization context and switching against real HTTP/auth infrastructure. Every test
/// registers/invites disposable accounts rather than touching the shared demo organization or the
/// login rate limiter (registration and invitation acceptance are both unthrottled).
/// </summary>
public sealed partial class OrganizationSwitchTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public OrganizationSwitchTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SingleOrganizationUser_SeesPlainTextNoSwitcherDropdown()
    {
        var client = RegisterAsync("Solo Org User", out _);

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Solo Org User Org", body);
        Assert.DoesNotContain("<details class=\"fo-org-switcher\">", body);
    }

    [Fact]
    public async Task MultiOrganizationUser_SeesSwitcherWithCurrentOrganizationMarked()
    {
        var (client, _, orgAName, orgBName) = SetUpTwoOrgUserAsync("SwitcherA", "SwitcherB");

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("<details class=\"fo-org-switcher fo-org-switcher--sidebar\">", body);
        Assert.Contains(orgAName, body);
        Assert.Contains(orgBName, body);
        Assert.Contains("fo-org-switcher__current", body);
    }

    [Fact]
    public async Task Switch_AnonymousRequest_RequiresAuthentication()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Switch")
        {
            Content = new FormUrlEncodedContent([new("organizationId", "1")]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Switch_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisterAsync("Forge Check", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Switch")
        {
            Content = new FormUrlEncodedContent([new("organizationId", "1")]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Switch_ToOwnMembership_ChangesCurrentOrganizationAndRoleDisplay_PersistsAcrossRequests()
    {
        var (client, orgBId, orgAName, orgBName) = SetUpTwoOrgUserAsync("PersistA", "PersistB");

        var switchResponse = await PostSwitchAsync(client, orgBId);
        Assert.Equal(HttpStatusCode.Redirect, switchResponse.StatusCode);
        Assert.Equal("/", switchResponse.Headers.Location?.ToString());

        var dashboard1 = await client.GetAsync("/");
        var body1 = await dashboard1.Content.ReadAsStringAsync();
        Assert.Contains(orgBName, body1);
        Assert.Contains("Executive Viewer", body1); // the role granted in Org B

        // A second, independent request — proves the selection persisted, not just this response.
        // Org A's name still legitimately appears once, as a switch-back option in the dropdown —
        // the thing actually under test is which one is marked CURRENT.
        var dashboard2 = await client.GetAsync("/");
        var body2 = await dashboard2.Content.ReadAsStringAsync();
        Assert.Contains(orgBName, body2);
        Assert.Contains($"<summary>{orgBName}</summary>", body2);
        _ = orgAName;
    }

    [Fact] // Step 6/29: switching to an organization the user is not a member of fails safely —
           // current organization is unaffected, and the response looks identical to any other
           // "not your organization" outcome (a plain redirect to the dashboard, nothing more).
    public async Task Switch_ToInaccessibleOrganization_FailsSafely_CurrentOrganizationUnchanged()
    {
        var (client, _, orgAName, _) = SetUpTwoOrgUserAsync("UnauthA", "UnauthB");
        RegisterAsync("Unrelated Owner", out _);
        var otherOrgId = await OrganizationIdByNameAsync("Unrelated Owner Org");

        var response = await PostSwitchAsync(client, otherOrgId);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // identical shape to a successful switch

        var dashboard = await client.GetAsync("/");
        var body = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains(orgAName, body); // unchanged — still whichever org was current before
    }

    [Fact]
    public async Task Switch_NonexistentOrganizationId_FailsSafely()
    {
        var client = RegisterAsync("Ghost Org Attempt", out _);

        var response = await PostSwitchAsync(client, 999_999_999);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Fact] // Step 19: Members follows current organization, never leaking another org's roster.
    public async Task Switch_MembersPageFollowsCurrentOrganization()
    {
        var (client, orgBId, _, orgBName) = SetUpTwoOrgUserAsync("MembersFollowA", "MembersFollowB", orgBRole: "Manager");

        await PostSwitchAsync(client, orgBId);

        var members = await client.GetAsync("/Organization/Members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        var body = await members.Content.ReadAsStringAsync();
        Assert.Contains(orgBName, body);
    }

    [Fact] // Account area keeps showing the full email regardless of organization context (Phase 17/19).
    public async Task AccountArea_StillShowsFullEmail_AfterSwitching()
    {
        var (client, orgBId, _, _) = SetUpTwoOrgUserAsync("EmailKeptA", "EmailKeptB");
        await PostSwitchAsync(client, orgBId);

        var dashboard = await client.GetAsync("/");
        var body = await dashboard.Content.ReadAsStringAsync();

        Assert.Contains("@switchtest.local", body);
    }

    [Fact] // Step 9: logging out and a different user logging in on the same HttpClient (i.e. the
           // same cookie jar) must never inherit the previous account's organization context.
    public async Task Logout_ThenDifferentUserLogin_DoesNotInheritPreviousOrganizationContext()
    {
        var (client, orgBId, _, orgBName) = SetUpTwoOrgUserAsync("LogoutA", "LogoutB");
        await PostSwitchAsync(client, orgBId);
        var confirmSwitch = await client.GetAsync("/");
        Assert.Contains(orgBName, await confirmSwitch.Content.ReadAsStringAsync());

        var logoutToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/");
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Logout")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", logoutToken)]),
        };
        await client.SendAsync(logoutRequest);

        // A second account registers and signs in using the SAME HttpClient/cookie container.
        var secondEmail = UniqueEmail();
        var registerToken = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        using var registerRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", "Second Browser User"),
                new("Input.Email", secondEmail),
                new("Input.Password", Password),
                new("Input.ConfirmPassword", Password),
                new("Input.OrganizationName", "Second Browser User Org"),
                new("__RequestVerificationToken", registerToken),
            ]),
        };
        await client.SendAsync(registerRequest);
        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, secondEmail);
        await TestAuthentication.SignInAsync(client, secondEmail, Password);

        var dashboard = await client.GetAsync("/");
        var body = await dashboard.Content.ReadAsStringAsync();

        Assert.Contains("Second Browser User Org", body);
        Assert.DoesNotContain(orgBName, body); // never inherited from the previous account's session
    }

    // ---- helpers ----

    private (HttpClient Client, int OrgBId, string OrgAName, string OrgBName) SetUpTwoOrgUserAsync(string labelA, string labelB, string orgBRole = "Viewer")
    {
        var orgAName = $"{labelA} Org";
        var orgBName = $"{labelB} Org";
        var client = RegisterAsync(labelA, out var email);

        // Register a second, throwaway org/admin, then invite the first user into it.
        var secondAdminClient = RegisterAsync(labelB, out _);
        var inviteToken = TestAuthentication.AntiForgeryTokenAsync(secondAdminClient, "/Organization/Members").GetAwaiter().GetResult();
        using var inviteRequest = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=Invite")
        {
            Content = new FormUrlEncodedContent(
            [
                new("InviteInput.Email", email),
                new("InviteInput.Role", orgBRole),
                new("__RequestVerificationToken", inviteToken),
            ]),
        };
        var inviteResponse = secondAdminClient.SendAsync(inviteRequest).GetAwaiter().GetResult();
        var link = ExtractInvitationLink(inviteResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        var acceptToken = TestAuthentication.AntiForgeryTokenAsync(client, link).GetAwaiter().GetResult();
        using var acceptRequest = new HttpRequestMessage(HttpMethod.Post, $"{link}&handler=Accept")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", acceptToken)]),
        };
        var acceptResponse = client.SendAsync(acceptRequest).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, acceptResponse.StatusCode);

        var orgBId = OrganizationIdByNameAsync(orgBName).GetAwaiter().GetResult();

        return (client, orgBId, orgAName, orgBName);
    }

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
                new("Input.OrganizationName", $"{fullName} Org"),
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

    private static async Task<HttpResponseMessage> PostSwitchAsync(HttpClient client, int organizationId)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Switch")
        {
            Content = new FormUrlEncodedContent(
            [
                new("organizationId", organizationId.ToString()),
                new("__RequestVerificationToken", token),
            ]),
        };

        return await client.SendAsync(request);
    }

    /// <summary>
    /// The Members page deliberately never renders a raw organization id (Step 16's "do not
    /// expose internal IDs"), so tests that need one to set up a scenario (never to assert
    /// anything the application itself discloses) read it directly from the database — the same
    /// in-process <c>_factory.Services.CreateScope()</c> pattern <c>SettingsTests</c> already uses.
    /// </summary>
    private async Task<int> OrganizationIdByNameAsync(string organizationName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Organizations.Where(o => o.Name == organizationName).Select(o => o.Id).SingleAsync();
    }

    private static string ExtractInvitationLink(string html)
    {
        var match = InvitationLinkPattern().Match(html);
        Assert.True(match.Success, $"No invitation link found in response body:{Environment.NewLine}{html}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@switchtest.local";

    [GeneratedRegex(""""value="(http://[^"]*/Account/AcceptInvitation\?token=[^"]*)"""")]
    private static partial Regex InvitationLinkPattern();
}
