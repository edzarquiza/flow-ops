using System.Net;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Account;

/// <summary>
/// Phase 17: profile/settings and account deletion against real HTTP/auth infrastructure. Every
/// test here registers its own disposable account (never a shared demo persona, and never the
/// login rate limiter — registration is unthrottled) so account-mutating and account-deleting
/// tests can never pollute or lock out anything another test depends on.
/// </summary>
public sealed class SettingsTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public SettingsTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Settings_AnonymousRequest_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/Settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Settings_Authenticated_ShowsFullEmailAndSettingsLinkInAccountArea()
    {
        var client = RegisteredClientAsync("Nav Check", out var email);

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(email, body);
        Assert.Contains("Profile &amp; Settings", body);
        Assert.Contains("Sign out", body);
    }

    [Fact]
    public async Task UpdateName_ValidSubmission_PersistsNewName()
    {
        var client = RegisteredClientAsync("Old Name", out _);

        var response = await PostFormAsync(client, "UpdateName", [new("NameInput.FullName", "Updated Name")]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var settings = await client.GetAsync("/Account/Settings");
        var body = await settings.Content.ReadAsStringAsync();
        Assert.Contains("Updated Name", body);
    }

    [Fact]
    public async Task UpdateName_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisteredClientAsync("Token Check", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Settings?handler=UpdateName")
        {
            Content = new FormUrlEncodedContent([new("NameInput.FullName", "No Token")]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangeEmail_ValidNewEmail_UpdatesAccountArea()
    {
        var client = RegisteredClientAsync("Email Changer", out _);
        var newEmail = UniqueEmail();

        var response = await PostFormAsync(client, "ChangeEmail", [new("EmailInput.NewEmail", newEmail)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode); // session still valid after the change
        Assert.Contains(newEmail, await dashboard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangeEmail_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisteredClientAsync("Email Token Check", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Settings?handler=ChangeEmail")
        {
            Content = new FormUrlEncodedContent([new("EmailInput.NewEmail", UniqueEmail())]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_KeepsSessionValid()
    {
        var client = RegisteredClientAsync("Password Changer", out _, out var originalPassword);

        var response = await PostFormAsync(client, "ChangePassword",
        [
            new("PasswordInput.CurrentPassword", originalPassword),
            new("PasswordInput.NewPassword", "A-Brand-New-Passw0rd!9"),
            new("PasswordInput.ConfirmNewPassword", "A-Brand-New-Passw0rd!9"),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_IncorrectCurrentPassword_ReRendersWithError()
    {
        var client = RegisteredClientAsync("Password Rejector", out _);

        var response = await PostFormAsync(client, "ChangePassword",
        [
            new("PasswordInput.CurrentPassword", "definitely-wrong"),
            new("PasswordInput.NewPassword", "A-Brand-New-Passw0rd!9"),
            new("PasswordInput.ConfirmNewPassword", "A-Brand-New-Passw0rd!9"),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Incorrect password", body);
    }

    [Fact] // Phase 22 (SEC-1): the explicit password policy applies to a password *change* too,
           // not only to registration.
    public async Task ChangePassword_NewPasswordShorterThanPolicyMinimum_ReRendersWithError()
    {
        var client = RegisteredClientAsync("Weak New Password", out _, out var originalPassword);

        var response = await PostFormAsync(client, "ChangePassword",
        [
            new("PasswordInput.CurrentPassword", originalPassword),
            new("PasswordInput.NewPassword", "Sh0rt!"),
            new("PasswordInput.ConfirmNewPassword", "Sh0rt!"),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, not redirected
    }

    [Fact]
    public async Task DeleteAccount_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisteredClientAsync("Delete Token Check", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Settings?handler=DeleteAccount")
        {
            Content = new FormUrlEncodedContent([new("ConfirmDelete", "true")]),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_SoleAdmin_IsBlocked()
    {
        // Registration makes the new user the sole Admin of a brand-new organization — deletion
        // must be refused outright, with no session change.
        var client = RegisteredClientAsync("Sole Admin", out _);

        var response = await PostFormAsync(client, "DeleteAccount", [new("ConfirmDelete", "true")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered, not redirected
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("only Admin", body);

        var dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode); // still signed in
    }

    [Fact]
    public async Task DeleteAccount_WithoutConfirmationCheckbox_IsRejected()
    {
        var client = RegisteredClientAsync("Unconfirmed Deleter", out _);

        var response = await PostFormAsync(client, "DeleteAccount", []); // ConfirmDelete omitted

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot be undone", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAccount_Successful_InvalidatesSessionAndPreventsFurtherAccess()
    {
        var client = RegisteredClientAsync("Deletable Owner", out var email);

        // A second Admin in the same organization, added directly to the test database, so the
        // sole-admin safety rule does not block this specific deletion — the point here is testing
        // the deletion/session-invalidation path itself, not the safety rule again.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.Users.SingleAsync(u => u.Email == email);
            var membership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

            var backupAdmin = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@backup.test.local",
                Email = $"{Guid.NewGuid():N}@backup.test.local",
                EmailConfirmed = true,
                DisplayName = "Backup Admin",
                IsActive = true,
                RegistrationApprovedAt = DateTimeOffset.UtcNow,
            };
            await userManager.CreateAsync(backupAdmin, "A-Backup-Passw0rd!1");
            db.OrganizationMemberships.Add(new OrganizationMembership(
                0, membership.OrganizationId, backupAdmin.Id, UserRole.Admin, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var response = await PostFormAsync(client, "DeleteAccount", [new("ConfirmDelete", "true")]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());

        // The cookie the client still holds must no longer grant access to anything.
        var afterDeletion = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, afterDeletion.StatusCode);
        Assert.Contains("/Account/Login", afterDeletion.Headers.Location?.ToString());

        // And re-authenticating as the deleted account must fail outright.
        var freshClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await TestAuthentication.AntiForgeryTokenAsync(freshClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", email),
                new("Input.Password", "A-Genuinely-Str0ng-Pw!"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var loginResponse = await freshClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode); // re-rendered with an error, never a redirect
        Assert.Contains("Invalid login attempt", await loginResponse.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private const string RegisteredPassword = "A-Genuinely-Str0ng-Pw!";

    private HttpClient RegisteredClientAsync(string fullName, out string email)
    {
        var (client, capturedEmail) = RegisterCoreAsync(fullName).GetAwaiter().GetResult();
        email = capturedEmail;
        return client;
    }

    private HttpClient RegisteredClientAsync(string fullName, out string email, out string password)
    {
        var (client, capturedEmail) = RegisterCoreAsync(fullName).GetAwaiter().GetResult();
        email = capturedEmail;
        password = RegisteredPassword;
        return client;
    }

    private async Task<(HttpClient Client, string Email)> RegisterCoreAsync(string fullName)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = UniqueEmail();

        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Register");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Register")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.FullName", fullName),
                new("Input.Email", capturedEmail),
                new("Input.Password", RegisteredPassword),
                new("Input.ConfirmPassword", RegisteredPassword),
                new("Input.OrganizationName", $"{fullName}'s Org"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // to PendingApproval, not the dashboard

        await TestAuthentication.ApproveRegistrationAsync(_factory.Services, capturedEmail);
        await TestAuthentication.SignInAsync(client, capturedEmail, RegisteredPassword);

        return (client, capturedEmail);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");

        var formFields = fields.ToList();
        formFields.Add(new("__RequestVerificationToken", token));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Account/Settings?handler={handler}")
        {
            Content = new FormUrlEncodedContent(formFields),
        };

        return await client.SendAsync(request);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@settings.test.local";
}
