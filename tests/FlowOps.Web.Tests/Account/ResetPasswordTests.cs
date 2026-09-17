using System.Net;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Account;

/// <summary>
/// ADR-0027: Admin-generated password-reset links, over real HTTP. FlowOps sends no email at
/// all — the Admin generates a one-time link from Organization/Members and sends it out-of-band,
/// the exact same "generate, someone else copies and sends it, this page is the anonymous
/// landing spot" shape invitations already use (<see cref="Organization.MembersTests"/>).
/// </summary>
public sealed class ResetPasswordTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private const string NewPassword = "A-Different-Str0ng-Pw!2";

    private readonly FlowOpsWebApplicationFactory _factory;

    public ResetPasswordTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_GeneratesLink_MemberResetsAndCanLogInWithNewPassword()
    {
        var adminClient = RegisterAsync("Reset Flow Admin", out _);
        var memberEmail = UniqueEmail();
        await InviteAndAcceptAsync(adminClient, memberEmail, "Agent", "Reset Flow Member");
        var memberUserId = await UserIdAsync(memberEmail);

        var resetLink = await GenerateResetLinkAsync(adminClient, memberUserId);
        Assert.Contains("/Account/ResetPassword", resetLink, StringComparison.Ordinal);

        var anonymousClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var relativeResetPath = new Uri(resetLink).PathAndQuery;
        var token = await TestAuthentication.AntiForgeryTokenAsync(anonymousClient, relativeResetPath);

        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, relativeResetPath)
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.NewPassword", NewPassword),
                new("Input.ConfirmPassword", NewPassword),
                new("__RequestVerificationToken", token),
            ]),
        };
        var resetResponse = await anonymousClient.SendAsync(resetRequest);

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var resetBody = await resetResponse.Content.ReadAsStringAsync();
        Assert.Contains("Your password has been reset", resetBody, StringComparison.Ordinal);

        // The new password now works.
        var freshLoginClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(freshLoginClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", memberEmail),
                new("Input.Password", NewPassword),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await freshLoginClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.DoesNotContain("/Account/Login", loginResponse.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedToken_IsRejectedWithGenericMessage_AndOldPasswordStillWorks()
    {
        var adminClient = RegisterAsync("Reset Tamper Admin", out _);
        var memberEmail = UniqueEmail();
        await InviteAndAcceptAsync(adminClient, memberEmail, "Agent", "Reset Tamper Member");
        var memberUserId = await UserIdAsync(memberEmail);

        var resetLink = await GenerateResetLinkAsync(adminClient, memberUserId);
        var tamperedLink = resetLink.Replace("token=", "token=tampered-", StringComparison.Ordinal);

        var anonymousClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var relativeResetPath = new Uri(tamperedLink).PathAndQuery;
        var token = await TestAuthentication.AntiForgeryTokenAsync(anonymousClient, relativeResetPath);

        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, relativeResetPath)
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.NewPassword", NewPassword),
                new("Input.ConfirmPassword", NewPassword),
                new("__RequestVerificationToken", token),
            ]),
        };
        var resetResponse = await anonymousClient.SendAsync(resetRequest);

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var body = await resetResponse.Content.ReadAsStringAsync();
        Assert.Contains("This reset link is no longer valid", body, StringComparison.Ordinal);

        // The original password is untouched by the failed attempt.
        var stillWorksClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(stillWorksClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", memberEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await stillWorksClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.DoesNotContain("/Account/Login", loginResponse.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact] // Deliberately more conservative than ordinary member management — a Manager may invite
           // and manage Agents/Viewers, but must not be able to generate a reset link for anyone.
    public async Task NonAdmin_CannotGenerateAResetLink()
    {
        var adminClient = RegisterAsync("Reset AuthZ Admin", out var adminEmail);
        var managerEmail = UniqueEmail();
        var managerClient = await InviteAndAcceptAsync(adminClient, managerEmail, "Manager", "Reset AuthZ Manager");
        var adminUserId = await UserIdAsync(adminEmail);

        var membersToken = await TestAuthentication.AntiForgeryTokenAsync(managerClient, "/Organization/Members");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=GenerateResetLink")
        {
            Content = new FormUrlEncodedContent(
            [
                new("targetUserId", adminUserId.ToString()),
                new("__RequestVerificationToken", membersToken),
            ]),
        };
        var response = await managerClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    private async Task<string> GenerateResetLinkAsync(HttpClient adminClient, Guid targetUserId)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(adminClient, "/Organization/Members");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=GenerateResetLink")
        {
            Content = new FormUrlEncodedContent(
            [
                new("targetUserId", targetUserId.ToString()),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await adminClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var marker = "value=\"";
        var linkStart = body.IndexOf("id=\"reset-link\"", StringComparison.Ordinal);
        Assert.True(linkStart >= 0, $"No reset-link field found in response body:{Environment.NewLine}{body}");
        var valueStart = body.IndexOf(marker, linkStart, StringComparison.Ordinal) + marker.Length;
        var valueEnd = body.IndexOf('"', valueStart);
        return System.Net.WebUtility.HtmlDecode(body[valueStart..valueEnd]);
    }

    /// <summary>Invites <paramref name="email"/> with <paramref name="role"/>, accepts as a brand
    /// new account, and returns a signed-in client for it.</summary>
    private async Task<HttpClient> InviteAndAcceptAsync(HttpClient adminClient, string email, string role, string fullName)
    {
        var inviteToken = await TestAuthentication.AntiForgeryTokenAsync(adminClient, "/Organization/Members");
        using var inviteRequest = new HttpRequestMessage(HttpMethod.Post, "/Organization/Members?handler=Invite")
        {
            Content = new FormUrlEncodedContent(
            [
                new("InviteInput.Email", email),
                new("InviteInput.Role", role),
                new("__RequestVerificationToken", inviteToken),
            ]),
        };
        var inviteResponse = await adminClient.SendAsync(inviteRequest);
        Assert.Equal(HttpStatusCode.OK, inviteResponse.StatusCode);
        var inviteBody = await inviteResponse.Content.ReadAsStringAsync();

        var marker = "/Account/AcceptInvitation?token=";
        var start = inviteBody.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No invitation link found in response body:{Environment.NewLine}{inviteBody}");
        var valueStart = inviteBody.LastIndexOf("value=\"", start, StringComparison.Ordinal) + "value=\"".Length;
        var valueEnd = inviteBody.IndexOf('"', start);
        var invitationPath = System.Net.WebUtility.HtmlDecode(new Uri(inviteBody[valueStart..valueEnd]).PathAndQuery);

        var memberClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var acceptToken = await TestAuthentication.AntiForgeryTokenAsync(memberClient, invitationPath);
        using var acceptRequest = new HttpRequestMessage(HttpMethod.Post, $"{invitationPath}&handler=CreateAccount")
        {
            Content = new FormUrlEncodedContent(
            [
                new("NewAccount.FullName", fullName),
                new("NewAccount.Password", Password),
                new("NewAccount.ConfirmPassword", Password),
                new("__RequestVerificationToken", acceptToken),
            ]),
        };
        var acceptResponse = await memberClient.SendAsync(acceptRequest);
        Assert.Equal(HttpStatusCode.Redirect, acceptResponse.StatusCode);

        return memberClient;
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
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
                new("Input.OrganizationName", $"{fullName}'s Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        TestAuthentication.ApproveRegistrationAsync(_factory.Services, capturedEmail).GetAwaiter().GetResult();
        TestAuthentication.SignInAsync(client, capturedEmail, Password).GetAwaiter().GetResult();

        email = capturedEmail;
        return client;
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@resetpwtest.local";
}
