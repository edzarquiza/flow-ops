using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Platform;

/// <summary>
/// Phase 30D (ADR-0034): <c>/Platform/Sla</c> over real HTTP — a Platform Admin can add, edit, and
/// remove SLA configuration rows; an ordinary tenant Admin (not a Platform Admin) is refused even
/// though they hold the highest tenant-scoped role there is; the four priority-default rows can
/// never be removed, even via a forged POST.
/// </summary>
public sealed class PlatformSlaConfigurationTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public PlatformSlaConfigurationTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PlatformAdmin_CanAddEditAndRemoveAnOverride()
    {
        var client = RegisterAsync("Sla Admin1", out var email);
        await GrantPlatformAdminAsync(email);

        var listHtml = await GetHtmlAsync(client, "/Platform/Sla/Index");
        Assert.Contains("SLA Configuration", listHtml, StringComparison.Ordinal);
        Assert.Contains("Default for priority", listHtml, StringComparison.Ordinal);

        var createToken = ExtractAntiForgeryToken(listHtml);
        var createResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Sla/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateOverrideInput.WorkType", "Incident"),
                new("CreateOverrideInput.Priority", "High"),
                new("CreateOverrideInput.TargetMinutes", "300"),
                new("CreateOverrideInput.RiskThresholdPercent", "65"),
                new("__RequestVerificationToken", createToken),
            ]),
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var afterCreateHtml = await createResponse.Content.ReadAsStringAsync();
        Assert.Contains("SLA override added.", afterCreateHtml, StringComparison.Ordinal);
        Assert.Contains("Incident", afterCreateHtml, StringComparison.Ordinal);

        int overrideId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            overrideId = await db.SlaConfigurations
                .Where(c => c.WorkType == FlowOps.Domain.Tickets.WorkType.Incident && c.Priority == FlowOps.Domain.Tickets.Priority.High)
                .Select(c => c.Id)
                .SingleAsync();
        }

        try
        {
            var updateToken = ExtractAntiForgeryToken(afterCreateHtml);
            var updateResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Sla/Index?handler=Update&id={overrideId}")
            {
                Content = new FormUrlEncodedContent(
                [
                    new("targetMinutes", "240"),
                    new("riskThresholdPercent", "50"),
                    new("__RequestVerificationToken", updateToken),
                ]),
            });
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            var afterUpdateHtml = await updateResponse.Content.ReadAsStringAsync();
            Assert.Contains("SLA configuration updated.", afterUpdateHtml, StringComparison.Ordinal);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
                var row = await db.SlaConfigurations.AsNoTracking().SingleAsync(c => c.Id == overrideId);
                Assert.Equal(240, row.TargetMinutes);
                Assert.Equal(50, row.RiskThresholdPercent);
            }

            var deleteToken = ExtractAntiForgeryToken(afterUpdateHtml);
            var deleteResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Sla/Index?handler=Delete&id={overrideId}")
            {
                Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deleteToken)]),
            });
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
            var afterDeleteHtml = await deleteResponse.Content.ReadAsStringAsync();
            Assert.Contains("SLA override removed.", afterDeleteHtml, StringComparison.Ordinal);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
                Assert.False(await db.SlaConfigurations.AsNoTracking().AnyAsync(c => c.Id == overrideId));
            }
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var stray = await db.SlaConfigurations.SingleOrDefaultAsync(c => c.Id == overrideId);
            if (stray is not null)
            {
                db.SlaConfigurations.Remove(stray);
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task CreateOverride_DuplicateWorkTypeAndPriority_ShowsAFriendlyErrorAndPersistsOnlyOne()
    {
        var client = RegisterAsync("Sla Admin2", out var email);
        await GrantPlatformAdminAsync(email);

        var firstToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Platform/Sla/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Sla/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateOverrideInput.WorkType", "ServiceRequest"),
                new("CreateOverrideInput.Priority", "Low"),
                new("CreateOverrideInput.TargetMinutes", "1000"),
                new("CreateOverrideInput.RiskThresholdPercent", "80"),
                new("__RequestVerificationToken", firstToken),
            ]),
        });

        try
        {
            var secondToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Platform/Sla/Index"));
            var secondResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Platform/Sla/Index?handler=Create")
            {
                Content = new FormUrlEncodedContent(
                [
                    new("CreateOverrideInput.WorkType", "ServiceRequest"),
                    new("CreateOverrideInput.Priority", "Low"),
                    new("CreateOverrideInput.TargetMinutes", "2000"),
                    new("CreateOverrideInput.RiskThresholdPercent", "90"),
                    new("__RequestVerificationToken", secondToken),
                ]),
            });

            var secondHtml = await secondResponse.Content.ReadAsStringAsync();
            Assert.Contains("already exists", secondHtml, StringComparison.OrdinalIgnoreCase);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var count = await db.SlaConfigurations.CountAsync(c => c.WorkType == FlowOps.Domain.Tickets.WorkType.ServiceRequest && c.Priority == FlowOps.Domain.Tickets.Priority.Low);
            Assert.Equal(1, count);
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var row = await db.SlaConfigurations.SingleOrDefaultAsync(c => c.WorkType == FlowOps.Domain.Tickets.WorkType.ServiceRequest && c.Priority == FlowOps.Domain.Tickets.Priority.Low);
            if (row is not null)
            {
                db.SlaConfigurations.Remove(row);
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact] // Hidden from the UI entirely (no Delete button renders for a default row), but a
           // forged POST must still be refused server-side (CLAUDE.md §2 rule 8).
    public async Task DeleteADefaultRow_ViaForgedPost_IsRefused()
    {
        var client = RegisterAsync("Sla Admin3", out var email);
        await GrantPlatformAdminAsync(email);
        var html = await GetHtmlAsync(client, "/Platform/Sla/Index");
        Assert.DoesNotContain("Remove</label>", html, StringComparison.Ordinal); // no Delete offered anywhere yet without an override present

        int defaultRowId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            defaultRowId = await db.SlaConfigurations
                .Where(c => c.WorkType == null && c.Priority == FlowOps.Domain.Tickets.Priority.Critical)
                .Select(c => c.Id)
                .SingleAsync();
        }

        var token = ExtractAntiForgeryToken(html);
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Platform/Sla/Index?handler=Delete&id={defaultRowId}")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        });

        var responseHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot be removed", responseHtml, StringComparison.OrdinalIgnoreCase);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.True(await db2.SlaConfigurations.AsNoTracking().AnyAsync(c => c.Id == defaultRowId));
    }

    [Fact] // A tenant's own Admin role grants nothing at the platform level (ADR-0023).
    public async Task OrdinaryTenantAdmin_CannotReachTheSlaPage()
    {
        var client = RegisterAsync("Sla NonPlatformAdmin", out _);

        var response = await client.GetAsync("/Platform/Sla/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Platform/Sla/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private async Task GrantPlatformAdminAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.IsPlatformAdmin = true;
        await db.SaveChangesAsync();
    }

    private async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No antiforgery token found in response.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private HttpClient RegisterAsync(string fullName, out string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var capturedEmail = $"{Guid.NewGuid():N}@platformslatest.local";

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
}
