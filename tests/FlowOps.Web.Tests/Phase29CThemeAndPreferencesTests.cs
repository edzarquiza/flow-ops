using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Application.Catalog;
using FlowOps.Application.Directory;
using FlowOps.Domain.Accounts;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 29C over real HTTP: the server-rendered theme attribute and the Appearance setting, the
/// stylesheet's theme contract, and "Show inactive" on the four Admin lists.
/// </summary>
public sealed class Phase29CThemeAndPreferencesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public Phase29CThemeAndPreferencesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- appearance

    [Fact]
    public async Task AnonymousPages_AreAlwaysDark()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Account/Login");

        Assert.Contains("<html lang=\"en\" data-theme=\"dark\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingUserWithNoChoice_IsDark_ThenLightSystemAndDarkAreSavedAndRendered()
    {
        var client = await SignInAsync(TestUsers.ViewerEmail);
        Assert.Equal("dark", await ThemeAsync(client, "/"));

        foreach (var (choice, expected) in new[] { ("Light", "light"), ("System", "system"), ("Dark", "dark") })
        {
            var response = await SaveAppearanceAsync(client, choice);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(expected, await ThemeAsync(client, "/"));
            Assert.Equal(expected, await ThemeAsync(client, "/Account/Settings"));
        }
    }

    [Fact] // Log out, log back in: the saved choice is what the server renders again.
    public async Task SavedLight_SurvivesSigningOutAndBackIn_AndOtherUsersAreUnaffected()
    {
        var agent = await SignInAsync(TestUsers.AgentEmail);
        await SaveAppearanceAsync(agent, "Light");
        Assert.Equal("light", await ThemeAsync(agent, "/"));

        var returning = await SignInAsync(TestUsers.AgentEmail); // a brand-new session
        Assert.Equal("light", await ThemeAsync(returning, "/"));

        var manager = await SignInAsync(TestUsers.ManagerEmail);
        Assert.Equal("dark", await ThemeAsync(manager, "/")); // another user's own default

        await SaveAppearanceAsync(agent, "Dark"); // leave the shared fixture as it was
    }

    [Theory]
    [InlineData("Purple")]
    [InlineData("light")]     // exact names only — not another casing
    [InlineData("1")]         // not a number
    [InlineData("")]
    public async Task InvalidAppearance_IsRejected_AndNothingChanges(string bad)
    {
        var client = await SignInAsync(TestUsers.ViewerEmail);
        await SaveAppearanceAsync(client, "Dark");

        var response = await SaveAppearanceAsync(client, bad);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-rendered with a message, not saved
        Assert.Contains("Choose System, Light or Dark.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("dark", await ThemeAsync(client, "/"));
    }

    [Fact]
    public async Task SettingsPage_OffersThreeRadios_WithTheSavedOneChecked_AndOnlyAnExternalScript()
    {
        var client = await SignInAsync(TestUsers.ViewerEmail);
        await SaveAppearanceAsync(client, "System");

        var html = await client.GetStringAsync("/Account/Settings");

        Assert.Matches(new Regex("<fieldset class=\"segmented\">"), html);
        Assert.Contains("value=\"System\" checked=\"checked\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"Light\" checked", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Dark\"", html, StringComparison.Ordinal);
        Assert.Contains("data-saved-theme=\"system\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/appearance-preview.js", html, StringComparison.Ordinal);

        // CSP compatibility: no inline script anywhere in the page (every <script> has a src).
        Assert.DoesNotMatch(new Regex("<script(?![^>]*\\bsrc=)"), html);

        await SaveAppearanceAsync(client, "Dark");
    }

    [Fact]
    public async Task AnonymousUser_CannotSaveAppearance()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Login");

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Settings?handler=SetAppearance")
        {
            Content = new FormUrlEncodedContent([new("Appearance", "Light"), new("__RequestVerificationToken", token)]),
        });

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- stylesheet contract

    [Fact]
    public async Task Stylesheet_LightAndSystemThemesRestateTheSameTokens_AndEveryOneExistsInDark()
    {
        var css = await _factory.CreateClient().GetStringAsync("/flowops.css");

        var dark = Tokens(Block(css, @":root\s*\{"));
        var light = Tokens(Block(css, @":root\[data-theme=""light""\]\s*\{"));
        var system = Tokens(Block(css, @"@media \(prefers-color-scheme: light\)\s*\{\s*:root\[data-theme=""system""\]\s*\{"));

        // System (when the OS prefers light) is exactly Light: same tokens, same values.
        Assert.Equal(light, system);

        // Light only ever overrides tokens Dark defines...
        Assert.All(light.Keys, k => Assert.True(dark.ContainsKey(k), $"{k} is overridden in Light but not defined in :root"));

        // ...and every token that carries a colour of the surface, text, or a semantic meaning is overridden.
        var mustVary = new[]
        {
            "--fo-bg", "--fo-surface", "--fo-surface-2", "--fo-line-row", "--fo-line", "--fo-line-hi",
            "--fo-text-hi", "--fo-text", "--fo-text-2", "--fo-text-3", "--fo-teal-text",
            "--fo-ok", "--fo-info", "--fo-warn", "--fo-danger", "--fo-violet",
            "--fo-ok-rgb", "--fo-info-rgb", "--fo-warn-rgb", "--fo-danger-rgb", "--fo-teal-rgb", "--fo-violet-rgb", "--fo-muted-rgb",
            "--fo-scrim-nav", "--fo-scrim-dialog", "--fo-shadow-pop", "--fo-track",
        };
        Assert.All(mustVary, k => Assert.True(light.ContainsKey(k), $"Light does not restate {k}"));

        // Themed colours are tokens: no hard-coded rgba() literal is left to stay the same in both themes.
        Assert.DoesNotContain("rgba(", css, StringComparison.Ordinal);

        // Native controls follow the theme.
        Assert.Contains("color-scheme: light", Block(css, @":root\[data-theme=""light""\]\s*\{"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- show inactive

    [Fact]
    public async Task ShowInactive_Teams_DefaultHidesInactive_ToggleShowsThem_AndTheyStayInactive()
    {
        var (teamId, name) = await CreateInactiveTeamAsync();
        var admin = await SignInAsync(TestUsers.AdminEmail);

        var hidden = await admin.GetStringAsync("/Admin");
        Assert.DoesNotContain(name, hidden, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\d+ inactive teams? hidden"), hidden);

        var shown = await admin.GetStringAsync("/Admin?showInactive=true");
        Assert.Contains(name, shown, StringComparison.Ordinal);
        Assert.Contains("Inactive", shown, StringComparison.Ordinal);

        await AssertTeamStillInactiveAsync(teamId);
    }

    [Fact]
    public async Task ShowInactive_Categories_OnTeamDetails()
    {
        var (teamId, _) = await CreateInactiveTeamAsync(); // a team page is reachable (inactive teams keep their page)
        var (activeTeamId, categoryName) = await CreateTeamWithInactiveCategoryAsync();
        var admin = await SignInAsync(TestUsers.AdminEmail);

        Assert.DoesNotContain(categoryName, await admin.GetStringAsync($"/Admin/Teams/Details/{activeTeamId}"), StringComparison.Ordinal);
        Assert.Contains(categoryName, await admin.GetStringAsync($"/Admin/Teams/Details/{activeTeamId}?showInactive=true"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Admin/Teams/Details/{teamId}")).StatusCode);
    }

    [Fact]
    public async Task ShowInactive_Projects()
    {
        var name = $"Retired project {Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var actor = await AdminActorAsync(scope);
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();
            var created = await catalog.CreateProjectAsync(actor, name);
            Assert.True((await catalog.DeactivateProjectAsync(actor, created.ProjectId!.Value)).Succeeded);
        }

        var admin = await SignInAsync(TestUsers.AdminEmail);

        Assert.DoesNotContain(name, await admin.GetStringAsync("/Admin/Projects"), StringComparison.Ordinal);
        Assert.Contains(name, await admin.GetStringAsync("/Admin/Projects?showInactive=true"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowInactive_Members_AndTheSearchTermSurvivesTheToggle()
    {
        var name = $"Former colleague {Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@inactive.local", Email = $"{Guid.NewGuid():N}@inactive.local", DisplayName = name, IsActive = false, RegistrationApprovedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, _factory.OrganizationId, user.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var admin = await SignInAsync(TestUsers.AdminEmail);

        Assert.DoesNotContain(name, await admin.GetStringAsync("/Organization/Members"), StringComparison.Ordinal);
        var shown = await admin.GetStringAsync("/Organization/Members?showInactive=true");
        Assert.Contains(name, shown, StringComparison.Ordinal);
        Assert.Contains("Deactivated", shown, StringComparison.Ordinal);

        // The switch's own form carries the search term along, so toggling does not lose it.
        var searched = await admin.GetStringAsync("/Organization/Members?search=Former");
        Assert.Contains("name=\"search\" value=\"Former\"", searched, StringComparison.Ordinal);
    }

    [Fact] // Another organization's inactive records are never exposed, with the switch on or off.
    public async Task ShowInactive_NeverExposesAnotherOrganizationsRecords()
    {
        var foreignTeam = $"Foreign inactive team {Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var org = new Organization(0, $"Other org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Add(org);
            await db.SaveChangesAsync();
            db.Add(new FlowOps.Domain.Directory.Team(0, org.Id, foreignTeam, DateTimeOffset.UtcNow, isActive: false));
            await db.SaveChangesAsync();
        }

        var admin = await SignInAsync(TestUsers.AdminEmail);

        Assert.DoesNotContain(foreignTeam, await admin.GetStringAsync("/Admin"), StringComparison.Ordinal);
        Assert.DoesNotContain(foreignTeam, await admin.GetStringAsync("/Admin?showInactive=true"), StringComparison.Ordinal);
    }

    [Fact] // Authorization is unchanged: a non-Admin cannot use the switch to reach the Admin lists.
    public async Task ShowInactive_DoesNotChangeAuthorization()
    {
        var agent = await SignInAsync(TestUsers.AgentEmail);

        var response = await agent.GetAsync("/Admin/Projects?showInactive=true");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, email);
        return client;
    }

    private static async Task<string> ThemeAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "<html lang=\"en\" data-theme=\"(\\w+)\">");
        Assert.True(match.Success, "expected <html data-theme=...>");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> SaveAppearanceAsync(HttpClient client, string choice)
    {
        var token = await TestAuthentication.AntiForgeryTokenAsync(client, "/Account/Settings");
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Account/Settings?handler=SetAppearance")
        {
            Content = new FormUrlEncodedContent([new("Appearance", choice), new("__RequestVerificationToken", token)]),
        });
    }

    private async Task<FlowOps.Domain.Tickets.CurrentUser> AdminActorAsync(IServiceScope scope)
    {
        var adminId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.AdminEmail);
        return new CurrentUser(adminId, _factory.OrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>());
    }

    private async Task<(int TeamId, string Name)> CreateInactiveTeamAsync()
    {
        var name = $"Retired team {Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var actor = await AdminActorAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        var created = await teams.CreateAsync(actor, name);
        Assert.True(created.Succeeded, created.Error);
        Assert.True((await teams.DeactivateAsync(actor, created.TeamId!.Value)).Succeeded);
        return (created.TeamId!.Value, name);
    }

    private async Task<(int TeamId, string CategoryName)> CreateTeamWithInactiveCategoryAsync()
    {
        var categoryName = $"Retired category {Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var actor = await AdminActorAsync(scope);
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();
        var made = await catalog.CreateCategoryAsync(actor, _factory.TeamId, categoryName, WorkType.Incident);
        Assert.True(made.Succeeded, made.Error);
        Assert.True((await catalog.DeactivateCategoryAsync(actor, made.CategoryId!.Value)).Succeeded);
        return (_factory.TeamId, categoryName);
    }

    private async Task AssertTeamStillInactiveAsync(int teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        Assert.False((await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId)).IsActive);
    }

    private static string Block(string css, string openPattern)
    {
        var start = Regex.Match(css, openPattern);
        Assert.True(start.Success, $"block not found: {openPattern}");
        var depth = 1;
        var i = start.Index + start.Length;
        var from = i;
        while (i < css.Length && depth > 0)
        {
            depth += css[i] == '{' ? 1 : css[i] == '}' ? -1 : 0;
            i++;
        }

        return css[from..(i - 1)];
    }

    private static Dictionary<string, string> Tokens(string block) =>
        Regex.Matches(block, @"(--fo-[a-z0-9-]+):\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " "));
}
