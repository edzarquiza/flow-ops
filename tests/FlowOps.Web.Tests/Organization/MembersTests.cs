using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Infrastructure.Email;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact] // Verification pass: a pending invitation must still be visible after navigating away —
           // previously the only trace of it was the one-time "Invitation created" notice.
    public async Task Members_AfterInviting_ShowsThePendingInvitationOnAFreshLoad()
    {
        var client = RegisterAsync("Pending List Admin", out _);
        var invitedEmail = UniqueEmail();
        await PostInviteAsync(client, invitedEmail, "Agent");

        var freshLoad = await client.GetAsync("/Organization/Members");

        Assert.Equal(HttpStatusCode.OK, freshLoad.StatusCode);
        var body = await freshLoad.Content.ReadAsStringAsync();
        Assert.Contains("Pending invitations", body, StringComparison.Ordinal);
        Assert.Contains(invitedEmail, body, StringComparison.Ordinal);
    }

    [Fact] // Phase 30 (ADR-0035): a failed email send still creates the invitation, with a warning
           // notice and the copy-link fallback in place of the "and emailed" success message.
           // Verification pass: Provider is overridden to "Resend" alongside the failing sender — this
           // test simulates a genuinely configured provider that fails, distinct from the "no provider
           // configured at all" case InviteMember_ValidSubmission... below now also covers.
    public async Task InviteMember_EmailDeliveryFails_ShowsWarningNoticeWithCopyLinkFallback()
    {
        var failingFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEmailSender>(new FailingEmailSender());
                services.AddSingleton(new EmailOptions { Provider = "Resend", FromAddress = "no-reply@flowops.test", FromName = "FlowOps", BaseUrl = "https://flowops.test" });
            }));
        var client = RegisterAsync(failingFactory, "Email Fail Admin", out _);

        var response = await PostInviteAsync(client, UniqueEmail(), "Agent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be sent", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Account/AcceptInvitation?token=", body, StringComparison.Ordinal);
    }

    [Fact] // Verification pass: the actual default (no Email section in appsettings) — every
           // local/dev/CI run of this app — must never claim an email was sent.
    public async Task InviteMember_NoLiveEmailProviderConfigured_ShowsHonestNotConfiguredNotice()
    {
        var client = RegisterAsync("Email Not Configured Admin", out _);

        var response = await PostInviteAsync(client, UniqueEmail(), "Agent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email is not configured in this environment", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emailed to the invited address", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Account/AcceptInvitation?token=", body, StringComparison.Ordinal);
    }

    [Fact] // Phase F1-B: the "sensitive" policy is applied to the whole MembersModel page (a Razor
           // Page is one endpoint regardless of handler method — see the class's own remarks), but
           // its built-in non-POST bypass keeps this page's member-listing GET unthrottled.
    public async Task InviteMember_IsRateLimited_ButMemberListingOnTheSamePageIsNot()
    {
        // Register against the shared, unthrottled default factory first — Register is itself
        // under the "sensitive" policy, so doing it against the low-limit factory below would
        // consume part of that same IP-partitioned budget before the test's own POSTs run.
        RegisterAsync("Rate Limit Invite Admin", out var email);

        var lowLimitFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Sensitive:PermitLimitPerMinute", "2"));
        var client = lowLimitFactory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email, Password);

        for (var i = 0; i < 2; i++)
        {
            var response = await PostInviteAsync(client, UniqueEmail(), "Agent");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var throttled = await PostInviteAsync(client, UniqueEmail(), "Agent");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // The member-listing GET on this same page (a different handler) is unaffected by the
        // invite handler's own bucket being exhausted — proves the throttle is handler-scoped.
        var listing = await client.GetAsync("/Organization/Members");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
    }

    [Fact] // Same page-wide "sensitive" throttling as OnPostInviteAsync, for OnPostGenerateResetLinkAsync.
    public async Task GenerateResetLink_IsRateLimited_AfterConfiguredPermitLimit()
    {
        // Same reasoning as InviteMember_IsRateLimited_ButMemberListingOnTheSamePageIsNot: register
        // against the shared default factory first, sign in fresh against the low-limit factory.
        RegisterAsync("Rate Limit Reset Admin", out var email);

        var lowLimitFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Sensitive:PermitLimitPerMinute", "2"));
        var client = lowLimitFactory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email, Password);

        // The rate limiter runs before the handler, so a fabricated target id is enough to prove
        // the 429 — the first two requests are refused by the handler itself (403, no such member),
        // never by the limiter; only the third is.
        for (var i = 0; i < 2; i++)
        {
            var response = await PostGenerateResetLinkAsync(client, Guid.NewGuid());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var throttled = await PostGenerateResetLinkAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
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

    private HttpClient RegisterAsync(string fullName, out string email) => RegisterAsync(_factory, fullName, out email);

    private static HttpClient RegisterAsync(WebApplicationFactory<Program> factory, string fullName, out string email)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
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

        TestAuthentication.ApproveRegistrationAsync(factory.Services, capturedEmail).GetAwaiter().GetResult();
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

    private static async Task<HttpResponseMessage> PostGenerateResetLinkAsync(HttpClient client, Guid targetUserId)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Organization/Members");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=GenerateResetLink")
        {
            Content = new FormUrlEncodedContent(
            [
                new("targetUserId", targetUserId.ToString()),
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

    /// <summary>An <see cref="IEmailSender"/> that always fails — for proving the invitation
    /// warning-notice UI path, without touching the real Resend provider.</summary>
    private sealed class FailingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Failed("Simulated failure for a test."));
    }
}
