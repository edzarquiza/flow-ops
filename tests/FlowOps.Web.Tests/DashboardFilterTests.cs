using System.Net;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 20 (ADR-0019): the dashboard's filter bar over real HTTP — query-string binding,
/// malformed input, cross-organization manipulation via a forged team id, and role differences in
/// the team dropdown. <see cref="DashboardTests"/> already covers the unfiltered page and the KPI
/// strip; this file covers the filter surface added on top of it.
/// </summary>
/// <remarks>
/// Login POSTs are rate limited to 5/minute per <see cref="TestAuthentication"/>'s own doc comment,
/// shared across every client this factory instance creates — so, deliberately, every scenario that
/// needs the SAME signed-in role is grouped into ONE test method behind ONE sign-in (several GETs,
/// no further logins), rather than one sign-in per assertion.
/// </remarks>
public sealed class DashboardFilterTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public DashboardFilterTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact] // One Manager sign-in, exercising every range/team/work-type filter scenario as
           // successive GETs on the same authenticated client.
    public async Task ManagerFilterScenarios()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        // Default: 90-day range, every new section present.
        var defaultHtml = await GetHtmlAsync(client, "/");
        Assert.Contains("last 90 days", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("Ticket volume over time", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("Tickets by status", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("Workload by team", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("Deadline status", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("Average resolution time", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("<option value=\"90\" selected=\"selected\">", defaultHtml, StringComparison.Ordinal);

        // A valid, non-default range is honored and reflected.
        var thirtyDayHtml = await GetHtmlAsync(client, "/?range=30");
        Assert.Contains("last 30 days", thirtyDayHtml, StringComparison.Ordinal);
        Assert.Contains("<option value=\"30\" selected=\"selected\">", thirtyDayHtml, StringComparison.Ordinal);

        // An out-of-set value is coerced to the default, never trusted or reflected as-is.
        var malformedRangeHtml = await GetHtmlAsync(client, "/?range=999999");
        Assert.Contains("last 90 days", malformedRangeHtml, StringComparison.Ordinal);

        // A non-numeric value must not crash the page — model binding just leaves it null.
        var nonNumericRangeResponse = await client.GetAsync("/?range=not-a-number");
        Assert.Equal(HttpStatusCode.OK, nonNumericRangeResponse.StatusCode);
        Assert.Contains("last 90 days", await nonNumericRangeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The caller's own team, honored.
        var ownTeamHtml = await GetHtmlAsync(client, $"/?team={_factory.TeamId}");
        Assert.Contains($"<option value=\"{_factory.TeamId}\" selected=\"selected\">", ownTeamHtml, StringComparison.Ordinal);

        // A syntactically valid but nonexistent team id falls back silently — no distinguishing
        // error that would let a caller probe for which ids are real.
        var nonexistentTeamHtml = await GetHtmlAsync(client, "/?team=999999999");
        Assert.Contains("<option value=\"\" selected=\"selected\">All teams</option>", nonexistentTeamHtml, StringComparison.Ordinal);

        // Work type: honored when valid...
        var workTypeHtml = await GetHtmlAsync(client, "/?type=Problem");
        Assert.Contains("<option value=\"Problem\" selected=\"selected\">", workTypeHtml, StringComparison.Ordinal);

        // ...and does not crash when invalid.
        var invalidWorkTypeResponse = await client.GetAsync("/?type=NotARealWorkType");
        Assert.Equal(HttpStatusCode.OK, invalidWorkTypeResponse.StatusCode);

        // All three filters apply simultaneously.
        var combinedHtml = await GetHtmlAsync(client, $"/?range=30&team={_factory.TeamId}&type=Incident");
        Assert.Contains("<option value=\"30\" selected=\"selected\">", combinedHtml, StringComparison.Ordinal);
        Assert.Contains($"<option value=\"{_factory.TeamId}\" selected=\"selected\">", combinedHtml, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Incident\" selected=\"selected\">", combinedHtml, StringComparison.Ordinal);
    }

    [Fact] // The central anti-leak requirement: a forged/real-but-foreign team id must never be
           // reflected as selected, and the request behaves exactly like "no team filter" — never
           // a distinguishable response that would confirm the foreign team exists.
    public async Task Manager_ForeignOrganizationTeamId_IsIgnored_NeverAppliedOrDisclosed()
    {
        int foreignTeamId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var foreignOrg = new FlowOps.Domain.Organizations.Organization(0, $"Foreign Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(foreignOrg);
            await db.SaveChangesAsync();
            var foreignTeam = new FlowOps.Domain.Directory.Team(0, foreignOrg.Id, $"Foreign Team {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(foreignTeam);
            await db.SaveChangesAsync();
            foreignTeamId = foreignTeam.Id;
        }

        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ManagerEmail);

        var html = await GetHtmlAsync(client, $"/?team={foreignTeamId}");

        Assert.DoesNotContain($"value=\"{foreignTeamId}\" selected", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"\" selected=\"selected\">All teams</option>", html, StringComparison.Ordinal);
    }

    [Fact] // Agent's analytics scope is "own assigned tickets only" — a team-wide filter control
           // would never narrow anything further, so the dropdown itself is withheld.
    public async Task Agent_HasNoTeamFilterDropdown()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        var html = await GetHtmlAsync(client, "/");

        Assert.DoesNotContain("id=\"team\"", html, StringComparison.Ordinal);
    }

    [Fact] // Viewer is a team-scoped role and does get the dropdown — but never populated with a
           // team the viewer is not a member of (Viewer is deliberately not a member of _factory.TeamId).
    public async Task Viewer_HasTeamFilterDropdown_WithNoForeignTeams()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var html = await GetHtmlAsync(client, "/");

        Assert.DoesNotContain($"value=\"{_factory.TeamId}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_Anonymous_QueryStringFilters_StillRedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/?range=30&team=1&type=Incident");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string requestUri)
    {
        var response = await client.GetAsync(requestUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
