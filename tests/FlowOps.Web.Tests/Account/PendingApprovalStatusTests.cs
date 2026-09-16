using System.Net;
using System.Text.Json;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Account;

/// <summary>
/// Phase 24A-Extension (ADR-0025): the <c>/Account/PendingApproval</c> page and its
/// <c>?handler=Status</c> polling endpoint — the endpoint is reachable anonymously and public-facing,
/// so it is treated as a security boundary in its own right (spec §39): it must identify the caller
/// solely through <c>PendingRegistrationCookie</c>, never through any client-supplied id, and must
/// never let one browser observe another account's status.
/// </summary>
public sealed class PendingApprovalStatusTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PendingApprovalStatusTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AfterRegistration_PendingPage_ShowsWaitingState()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await RegisterAsync(client, "PendingApproval Waiting");

        var page = await client.GetAsync("/Account/PendingApproval");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Waiting for approval", html, StringComparison.Ordinal);
        // All three state blocks are always present in the markup (the polling script toggles
        // visibility via the `hidden` attribute rather than replacing DOM content) — so the
        // meaningful assertion is which one is NOT hidden, not plain substring presence.
        AssertVisibleState(html, "pending");
    }

    [Fact]
    public async Task StatusEndpoint_ReturnsPending_ImmediatelyAfterRegistration()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await RegisterAsync(client, "PendingApproval StatusPending");

        var status = await GetStatusAsync(client);

        Assert.Equal("Pending", status);
    }

    [Fact]
    public async Task StatusEndpoint_ReturnsActive_AfterApproval_NoBrowserRefreshNeeded()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = await RegisterAsync(client, "PendingApproval StatusActive");
        Assert.Equal("Pending", await GetStatusAsync(client));

        // The Platform Admin approves out-of-band — the same client/cookie from registration is
        // reused for the next poll, exactly like the page's own script re-polling on a timer
        // without the user doing anything.
        await ApproveAsync(email);

        Assert.Equal("Active", await GetStatusAsync(client));
    }

    [Fact]
    public async Task StatusEndpoint_ReturnsRejected_AfterRejection()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = await RegisterAsync(client, "PendingApproval StatusRejected");
        Assert.Equal("Pending", await GetStatusAsync(client));

        await RejectAsync(email);

        Assert.Equal("Rejected", await GetStatusAsync(client));
    }

    [Fact]
    public async Task PendingPage_ServerRendersApprovedState_WhenAlreadyApprovedBeforeFirstVisit()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = await RegisterAsync(client, "PendingApproval PreApproved");
        await ApproveAsync(email);

        var page = await client.GetAsync("/Account/PendingApproval");
        var html = await page.Content.ReadAsStringAsync();

        AssertVisibleState(html, "approved");
    }

    [Fact] // spec §11/§12/§37: no cookie at all — a direct, unauthenticated visit with no prior
           // registration — must never resolve to any real account's status.
    public async Task StatusEndpoint_NoCorrelationCookie_ReturnsUnknown_NeverAnotherAccount()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var status = await GetStatusAsync(client);

        Assert.Equal("Unknown", status);
    }

    [Fact] // spec §12/§37: a client-supplied id must never substitute for the cookie — the endpoint
           // takes no id parameter at all, so even attempting to smuggle one has no effect.
    public async Task StatusEndpoint_IgnoresClientSuppliedUserId_CannotBeUsedToProbeAnotherAccount()
    {
        var victimClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var victimEmail = await RegisterAsync(victimClient, "PendingApproval Victim");
        var victimId = await GetUserIdAsync(victimEmail);
        await ApproveAsync(victimEmail); // the victim's real status is Active

        // An unrelated caller, with no correlation cookie of their own, tries to pass the victim's
        // real user id directly.
        var attackerClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await attackerClient.GetAsync($"/Account/PendingApproval?handler=Status&userId={victimId}");
        var status = await ReadStatusAsync(response);

        Assert.Equal("Unknown", status); // never "Active" — the query parameter is never read at all.
    }

    [Fact] // spec §37: multi-tenant/cross-account isolation — two different browsers each polling
           // their own registration must never see each other's status.
    public async Task StatusEndpoint_TwoDifferentPendingAccounts_EachSeesOnlyItsOwnStatus()
    {
        var clientA = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var emailA = await RegisterAsync(clientA, "PendingApproval TenantA");

        var clientB = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var emailB = await RegisterAsync(clientB, "PendingApproval TenantB");

        await ApproveAsync(emailA); // only A is approved

        Assert.Equal("Active", await GetStatusAsync(clientA));
        Assert.Equal("Pending", await GetStatusAsync(clientB)); // B's own poll is unaffected by A's approval
    }

    // ---- helpers ----

    /// <summary>All three <c>data-approval-state</c> blocks are always present in the server-rendered
    /// markup; only the expected one should lack the <c>hidden</c> attribute.</summary>
    private static void AssertVisibleState(string html, string expectedVisibleState)
    {
        foreach (var state in new[] { "pending", "approved", "rejected" })
        {
            var marker = $"data-approval-state=\"{state}\"";
            var index = html.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"No block found for state '{state}'.");

            // The `hidden` attribute (when present) immediately follows the state marker in the
            // same tag, before the tag's closing '>'.
            var tagEnd = html.IndexOf('>', index);
            var tagContent = html[index..tagEnd];
            var isHidden = tagContent.Contains("hidden", StringComparison.Ordinal);

            if (state == expectedVisibleState)
            {
                Assert.False(isHidden, $"Expected the '{state}' block to be visible, but it was hidden.");
            }
            else
            {
                Assert.True(isHidden, $"Expected the '{state}' block to be hidden, but it was visible.");
            }
        }
    }

    private async Task<string?> GetStatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Account/PendingApproval?handler=Status");
        return await ReadStatusAsync(response);
    }

    private static async Task<string?> ReadStatusAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var status = document.RootElement.GetProperty("status").GetString();

        // spec §10: the endpoint returns only the status — nothing else.
        var propertyNames = new List<string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            propertyNames.Add(property.Name);
        }

        Assert.Equal(["status"], propertyNames);
        return status;
    }

    private async Task<Guid> GetUserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private async Task ApproveAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.RegistrationApprovedAt = DateTimeOffset.UtcNow;
        user.LockoutEnabled = false;
        user.LockoutEnd = null;
        await db.SaveChangesAsync();
    }

    private async Task RejectAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.RegistrationRejectedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<string> RegisterAsync(HttpClient client, string fullName)
    {
        var email = $"{Guid.NewGuid():N}@pendingapprovaltest.local";
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
