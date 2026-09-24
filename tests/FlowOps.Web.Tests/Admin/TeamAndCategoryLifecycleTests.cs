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

namespace FlowOps.Web.Tests.Admin;

/// <summary>
/// Phase 22 (ADR-0022): Team rename/deactivate and Category add/rename/deactivate over real HTTP,
/// from the team-detail page that already hosts membership management. Every test registers its
/// own disposable organization via the real, unthrottled registration flow, the same pattern
/// <see cref="ProjectManagementTests"/> already uses.
/// </summary>
public sealed class TeamAndCategoryLifecycleTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public TeamAndCategoryLifecycleTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_CanRenameAndDeactivateATeam()
    {
        var client = RegisterAsync("TeamLifecycle Admin1", out _);
        var teamId = await CreateTeamAsync(client);
        var renamedName = $"Renamed Team {Guid.NewGuid():N}";

        var renameToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        using var renameRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=RenameTeam")
        {
            Content = new FormUrlEncodedContent([new("name", renamedName), new("__RequestVerificationToken", renameToken)]),
        };
        var renameResponse = await client.SendAsync(renameRequest);
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var afterRenameHtml = await renameResponse.Content.ReadAsStringAsync();
        Assert.Contains(renamedName, afterRenameHtml, StringComparison.Ordinal);
        Assert.Contains("renamed", afterRenameHtml, StringComparison.OrdinalIgnoreCase);

        var deactivateToken = ExtractAntiForgeryToken(afterRenameHtml);
        using var deactivateRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=DeactivateTeam")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deactivateToken)]),
        };
        var deactivateResponse = await client.SendAsync(deactivateRequest);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var afterDeactivateHtml = await deactivateResponse.Content.ReadAsStringAsync();
        Assert.Contains("Inactive", afterDeactivateHtml, StringComparison.Ordinal);
        Assert.Contains("deactivated", afterDeactivateHtml, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
        Assert.False(team.IsActive);
        Assert.Equal(renamedName, team.Name);
    }

    [Fact] // Phase 30B (ADR-0032): reactivation now exists for teams/categories/projects, so neither
           // page may still claim deactivation is one-way — and a Reactivate button must actually work.
    public async Task DeactivateConfirmationCopy_DoesNotClaimOneWay_AndReactivateActuallyWorks_ForTeamsCategoriesOrProjects()
    {
        var client = RegisterAsync("TeamLifecycle CopyCheck", out _);
        var teamId = await CreateTeamAsync(client);

        // The team confirm-panel (rendered unconditionally on its own page) and the category
        // confirm-panel (rendered for the one category CreateTeamAsync's own handler adds) must not
        // claim deactivation is permanent.
        var teamHtml = await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}");
        Assert.DoesNotContain("one-way", teamHtml, StringComparison.OrdinalIgnoreCase);

        var deactivateTeamToken = ExtractAntiForgeryToken(teamHtml);
        var deactivateResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=DeactivateTeam")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deactivateTeamToken)]),
        });
        var afterDeactivateHtml = await deactivateResponse.Content.ReadAsStringAsync();
        Assert.Contains("Reactivate team", afterDeactivateHtml, StringComparison.Ordinal);

        var reactivateTeamToken = ExtractAntiForgeryToken(afterDeactivateHtml);
        var reactivateResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=ReactivateTeam")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", reactivateTeamToken)]),
        });
        var afterReactivateHtml = await reactivateResponse.Content.ReadAsStringAsync();
        Assert.Contains("Team reactivated.", afterReactivateHtml, StringComparison.Ordinal);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var team = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
            Assert.True(team.IsActive);
        }

        var projectName = $"Copy Check Project {Guid.NewGuid():N}";
        var createToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", projectName), new("__RequestVerificationToken", createToken)]),
        });

        var projectsHtml = await GetHtmlAsync(client, "/Admin/Projects/Index");
        Assert.Contains(projectName, projectsHtml, StringComparison.Ordinal); // the project's own confirm-panel is actually present
        Assert.DoesNotContain("one-way", projectsHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_CanAddRenameAndDeactivateACategoryOnAnExistingTeam()
    {
        var client = RegisterAsync("TeamLifecycle Admin2", out _);
        var teamId = await CreateTeamAsync(client);
        var categoryName = $"Requests {Guid.NewGuid():N}";
        var renamedName = $"{categoryName} v2";

        var addToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        using var addRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=AddCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateCategoryInput.Name", categoryName),
                new("CreateCategoryInput.DefaultWorkType", "ServiceRequest"),
                new("__RequestVerificationToken", addToken),
            ]),
        };
        var addResponse = await client.SendAsync(addRequest);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var afterAddHtml = await addResponse.Content.ReadAsStringAsync();
        Assert.Contains(categoryName, afterAddHtml, StringComparison.Ordinal);
        Assert.Contains("added", afterAddHtml, StringComparison.OrdinalIgnoreCase);

        int categoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            categoryId = await db.Categories.Where(c => c.Name == categoryName).Select(c => c.Id).SingleAsync();
        }

        var renameToken = ExtractAntiForgeryToken(afterAddHtml);
        using var renameRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=RenameCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("categoryId", categoryId.ToString()),
                new("name", renamedName),
                new("__RequestVerificationToken", renameToken),
            ]),
        };
        var renameResponse = await client.SendAsync(renameRequest);
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var afterRenameHtml = await renameResponse.Content.ReadAsStringAsync();
        Assert.Contains(renamedName, afterRenameHtml, StringComparison.Ordinal);

        var deactivateToken = ExtractAntiForgeryToken(afterRenameHtml);
        using var deactivateRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=DeactivateCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("categoryId", categoryId.ToString()),
                new("__RequestVerificationToken", deactivateToken),
            ]),
        };
        var deactivateResponse = await client.SendAsync(deactivateRequest);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var afterDeactivateHtml = await deactivateResponse.Content.ReadAsStringAsync();
        Assert.Contains("deactivated", afterDeactivateHtml, StringComparison.OrdinalIgnoreCase);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var category = await db2.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryId);
        Assert.False(category.IsActive);
        Assert.Equal(renamedName, category.Name);
    }

    [Fact] // Phase 30B (ADR-0032): ADR-0022's independence rule is unchanged — a category can be
           // reactivated while its own team is still inactive, and the UI says so.
    public async Task Admin_CanReactivateACategory_EvenWhileItsTeamIsStillInactive()
    {
        var client = RegisterAsync("TeamLifecycle Admin3", out _);
        var teamId = await CreateTeamAsync(client);
        var categoryName = $"Requests {Guid.NewGuid():N}";

        var addToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=AddCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateCategoryInput.Name", categoryName),
                new("CreateCategoryInput.DefaultWorkType", "ServiceRequest"),
                new("__RequestVerificationToken", addToken),
            ]),
        });

        int categoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            categoryId = await db.Categories.Where(c => c.Name == categoryName).Select(c => c.Id).SingleAsync();
        }

        var deactivateCategoryToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=DeactivateCategory")
        {
            Content = new FormUrlEncodedContent([new("categoryId", categoryId.ToString()), new("__RequestVerificationToken", deactivateCategoryToken)]),
        });

        var deactivateTeamToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=DeactivateTeam")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", deactivateTeamToken)]),
        });

        // The deactivated category row is hidden by default (Show inactive is off) — fetch it with
        // the toggle on, the same way an Admin would need to in order to find it and reactivate it.
        var afterDeactivateTeamHtml = await GetHtmlAsync(client, $"/Admin/Teams/Details/{teamId}?showInactive=true");
        Assert.Contains("Won't appear in Create Ticket until", afterDeactivateTeamHtml, StringComparison.Ordinal);

        var reactivateCategoryToken = ExtractAntiForgeryToken(afterDeactivateTeamHtml);
        var afterReactivateCategoryHtml = await (await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamId}?handler=ReactivateCategory")
        {
            Content = new FormUrlEncodedContent([new("categoryId", categoryId.ToString()), new("__RequestVerificationToken", reactivateCategoryToken)]),
        })).Content.ReadAsStringAsync();
        Assert.Contains("Category reactivated.", afterReactivateCategoryHtml, StringComparison.Ordinal);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var category = await db2.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryId);
        var team = await db2.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
        Assert.True(category.IsActive);
        Assert.False(team.IsActive); // reactivating the category never cascades to its team
    }

    [Fact] // Phase 16 organization boundary: Org A's Admin must not manage Org B's team/category.
    public async Task Admin_CannotManageAnotherOrganizationsTeamOrCategory()
    {
        var clientA = RegisterAsync("TeamLifecycle CrossOrgA", out _);
        var clientB = RegisterAsync("TeamLifecycle CrossOrgB", out _);
        var teamBId = await CreateTeamAsync(clientB);
        int categoryInB;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            categoryInB = await db.Categories.Where(c => c.TeamId == teamBId).Select(c => c.Id).SingleAsync();
        }

        var responseA = await clientA.GetAsync($"/Admin/Teams/Details/{teamBId}");
        Assert.Equal(HttpStatusCode.NotFound, responseA.StatusCode);

        var tokenForOwnAdminPage = ExtractAntiForgeryToken(await GetHtmlAsync(clientA, "/Admin"));
        using var renameTeamRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamBId}?handler=RenameTeam")
        {
            Content = new FormUrlEncodedContent([new("name", "Hijacked"), new("__RequestVerificationToken", tokenForOwnAdminPage)]),
        };
        var renameTeamResponse = await clientA.SendAsync(renameTeamRequest);
        Assert.Equal(HttpStatusCode.Redirect, renameTeamResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", renameTeamResponse.Headers.Location?.ToString());

        using var deactivateTeamRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamBId}?handler=DeactivateTeam")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", tokenForOwnAdminPage)]),
        };
        var deactivateTeamResponse = await clientA.SendAsync(deactivateTeamRequest);
        Assert.Equal(HttpStatusCode.Redirect, deactivateTeamResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", deactivateTeamResponse.Headers.Location?.ToString());

        using var renameCategoryRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamBId}?handler=RenameCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("categoryId", categoryInB.ToString()),
                new("name", "Hijacked"),
                new("__RequestVerificationToken", tokenForOwnAdminPage),
            ]),
        };
        var renameCategoryResponse = await clientA.SendAsync(renameCategoryRequest);
        Assert.Equal(HttpStatusCode.Redirect, renameCategoryResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", renameCategoryResponse.Headers.Location?.ToString());

        using var addCategoryRequest = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Teams/Details/{teamBId}?handler=AddCategory")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateCategoryInput.Name", "Sneaky"),
                new("CreateCategoryInput.DefaultWorkType", "Incident"),
                new("__RequestVerificationToken", tokenForOwnAdminPage),
            ]),
        };
        var addCategoryResponse = await clientA.SendAsync(addCategoryRequest);
        Assert.Equal(HttpStatusCode.Redirect, addCategoryResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", addCategoryResponse.Headers.Location?.ToString());

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var team = await db2.Teams.AsNoTracking().SingleAsync(t => t.Id == teamBId);
        Assert.True(team.IsActive);
        Assert.NotEqual("Hijacked", team.Name);
        var category = await db2.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryInB);
        Assert.NotEqual("Hijacked", category.Name);
        Assert.DoesNotContain(await db2.Categories.AsNoTracking().Where(c => c.TeamId == teamBId).Select(c => c.Name).ToListAsync(), n => n == "Sneaky");
    }

    [Fact]
    public async Task NonAdmin_CannotRenameOrDeactivateTeam()
    {
        var ownerClient = RegisterAsync("TeamLifecycle NonAdminOwner", out var ownerEmail);
        var teamId = await CreateTeamAsync(ownerClient);
        var agentEmail = $"{Guid.NewGuid():N}@teamlifecycletest.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await db.Users.SingleAsync(u => u.Email == ownerEmail);
            var ownerMembership = await db.OrganizationMemberships.SingleAsync(m => m.UserId == owner.Id);

            var agent = new ApplicationUser
            {
                UserName = agentEmail,
                Email = agentEmail,
                EmailConfirmed = true,
                DisplayName = "Non-Admin Agent",
                IsActive = true,
            };
            var createResult = await userManager.CreateAsync(agent, Password);
            Assert.True(createResult.Succeeded);
            db.OrganizationMemberships.Add(new OrganizationMembership(0, ownerMembership.OrganizationId, agent.Id, UserRole.Agent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var agentClient = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var loginToken = await TestAuthentication.AntiForgeryTokenAsync(agentClient, "/Account/Login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Email", agentEmail),
                new("Input.Password", Password),
                new("__RequestVerificationToken", loginToken),
            ]),
        };
        var loginResponse = await agentClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var response = await agentClient.GetAsync($"/Admin/Teams/Details/{teamId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    // ---- helpers ----

    private async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<int> CreateTeamAsync(HttpClient client)
    {
        var adminHtml = await GetHtmlAsync(client, "/Admin");
        var token = ExtractAntiForgeryToken(adminHtml);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=CreateTeam")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.TeamName", $"Team {Guid.NewGuid():N}"),
                new("Input.CategoryName", $"Category {Guid.NewGuid():N}"),
                new("Input.CategoryWorkType", "0"),
                new("__RequestVerificationToken", token),
            ]),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Teams.OrderByDescending(t => t.Id).Select(t => t.Id).FirstAsync();
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
        var capturedEmail = $"{Guid.NewGuid():N}@teamlifecycletest.local";

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
