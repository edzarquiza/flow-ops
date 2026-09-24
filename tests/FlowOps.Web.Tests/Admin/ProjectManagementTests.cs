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
/// Project management over real HTTP — the smallest legitimate way for an organization to populate
/// the Create Ticket "Project (optional)" dropdown, which previously had no data source beyond
/// <c>DemoDataSeeder</c>. Every test registers its own disposable organization via the real,
/// unthrottled registration flow, the same pattern <see cref="TeamMembershipTests"/> already uses.
/// </summary>
/// <remarks>One login POST in this class (the non-admin test) — well inside the five-per-minute
/// budget (CLAUDE.md §12).</remarks>
public sealed class ProjectManagementTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";

    private readonly FlowOpsWebApplicationFactory _factory;

    public ProjectManagementTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_CanAccessProjectManagement()
    {
        var client = RegisterAsync("ProjectMgmt Admin1", out _);

        var response = await client.GetAsync("/Admin/Projects/Index");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No projects yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_CanCreateRenameAndDeactivateAProject()
    {
        var client = RegisterAsync("ProjectMgmt Admin2", out _);
        var projectName = $"Alpha Rollout {Guid.NewGuid():N}";
        var renamedName = $"{projectName} v2";

        var createToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("CreateInput.Name", projectName),
                new("__RequestVerificationToken", createToken),
            ]),
        };
        var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var afterCreateHtml = await createResponse.Content.ReadAsStringAsync();
        Assert.Contains(projectName, afterCreateHtml, StringComparison.Ordinal);
        Assert.Contains("project", afterCreateHtml, StringComparison.OrdinalIgnoreCase);

        int projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            projectId = await db.Projects.Where(p => p.Name == projectName).Select(p => p.Id).SingleAsync();
        }

        var renameToken = ExtractAntiForgeryToken(afterCreateHtml);
        using var renameRequest = new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Rename")
        {
            Content = new FormUrlEncodedContent(
            [
                new("projectId", projectId.ToString()),
                new("name", renamedName),
                new("__RequestVerificationToken", renameToken),
            ]),
        };
        var renameResponse = await client.SendAsync(renameRequest);
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var afterRenameHtml = await renameResponse.Content.ReadAsStringAsync();
        Assert.Contains(renamedName, afterRenameHtml, StringComparison.Ordinal);
        Assert.Contains("renamed", afterRenameHtml, StringComparison.OrdinalIgnoreCase);

        var deactivateToken = ExtractAntiForgeryToken(afterRenameHtml);
        using var deactivateRequest = new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent(
            [
                new("projectId", projectId.ToString()),
                new("__RequestVerificationToken", deactivateToken),
            ]),
        };
        var deactivateResponse = await client.SendAsync(deactivateRequest);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var afterDeactivateHtml = await deactivateResponse.Content.ReadAsStringAsync();
        Assert.Contains("Inactive", afterDeactivateHtml, StringComparison.Ordinal);
        Assert.Contains("deactivated", afterDeactivateHtml, StringComparison.OrdinalIgnoreCase);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            Assert.False(project.IsActive);
            Assert.Equal(renamedName, project.Name);
        }
    }

    [Fact] // Phase 30B (ADR-0032): deactivation is no longer terminal.
    public async Task Admin_CanReactivateADeactivatedProject_AndItReappearsOnCreateTicket()
    {
        var client = RegisterAsync("ProjectMgmt Admin4", out _);
        await CreateTeamAsync(client); // Create Ticket offers nothing at all without an active team.
        var projectName = $"Alpha Rollout {Guid.NewGuid():N}";
        var createToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", projectName), new("__RequestVerificationToken", createToken)]),
        });
        int projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            projectId = await db.Projects.Where(p => p.Name == projectName).Select(p => p.Id).SingleAsync();
        }

        var deactivateToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("projectId", projectId.ToString()), new("__RequestVerificationToken", deactivateToken)]),
        });

        // The deactivated row is hidden by default (Show inactive is off) — fetch it with the
        // toggle on, the same way an Admin would need to in order to find it and reactivate it.
        var afterDeactivateHtml = await GetHtmlAsync(client, "/Admin/Projects/Index?showInactive=true");
        Assert.Contains("Reactivate", afterDeactivateHtml, StringComparison.Ordinal);

        var reactivateToken = ExtractAntiForgeryToken(afterDeactivateHtml);
        var afterReactivate = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Reactivate")
        {
            Content = new FormUrlEncodedContent([new("projectId", projectId.ToString()), new("__RequestVerificationToken", reactivateToken)]),
        });
        var afterReactivateHtml = await afterReactivate.Content.ReadAsStringAsync();
        Assert.Contains("Project reactivated.", afterReactivateHtml, StringComparison.Ordinal);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            Assert.True(project.IsActive);
        }

        var createOptionsHtml = await GetHtmlAsync(client, "/Tickets/Create");
        Assert.Contains($"value=\"{projectId}\"", createOptionsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeactivatedProject_NoLongerOfferedOnCreateTicket_ButExistingTicketKeepsDisplayingIt()
    {
        var client = RegisterAsync("ProjectMgmt Admin3", out _);
        var teamId = await CreateTeamAsync(client);
        int categoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            categoryId = await db.Categories.Where(c => c.TeamId == teamId).Select(c => c.Id).SingleAsync();
        }

        var projectName = $"Alpha Rollout {Guid.NewGuid():N}";
        var createToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", projectName), new("__RequestVerificationToken", createToken)]),
        });
        int projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            projectId = await db.Projects.Where(p => p.Name == projectName).Select(p => p.Id).SingleAsync();
        }

        var createTicketToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Tickets/Create"));
        using var createTicketRequest = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Title", "Laptop will not power on"),
                new("Input.Description", "The laptop shows no lights when the power button is pressed."),
                new("Input.WorkType", "Incident"),
                new("Input.Priority", "High"),
                new("Input.CategoryId", categoryId.ToString()),
                new("Input.ProjectId", projectId.ToString()),
                new("__RequestVerificationToken", createTicketToken),
            ]),
        };
        var createTicketResponse = await client.SendAsync(createTicketRequest);
        Assert.Equal(HttpStatusCode.Redirect, createTicketResponse.StatusCode);
        var ticketLocation = createTicketResponse.Headers.Location!.ToString();

        var deactivateToken = ExtractAntiForgeryToken(await GetHtmlAsync(client, "/Admin/Projects/Index"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent([new("projectId", projectId.ToString()), new("__RequestVerificationToken", deactivateToken)]),
        });

        var createOptionsHtml = await GetHtmlAsync(client, "/Tickets/Create");
        Assert.DoesNotContain($"value=\"{projectId}\"", createOptionsHtml, StringComparison.Ordinal);

        var ticketDetailHtml = await GetHtmlAsync(client, ticketLocation);
        Assert.Contains(projectName, ticketDetailHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdmin_CannotAccessProjectManagementPage()
    {
        var ownerClient = RegisterAsync("ProjectMgmt NonAdminOwner", out var ownerEmail);
        var agentEmail = $"{Guid.NewGuid():N}@projectmgmttest.local";

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

        var response = await agentClient.GetAsync("/Admin/Projects/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact] // Phase 16 organization boundary: Org A's Admin must not manage Org B's project.
    public async Task Admin_CannotManageAnotherOrganizationsProject()
    {
        var clientA = RegisterAsync("ProjectMgmt CrossOrgA", out _);
        var clientB = RegisterAsync("ProjectMgmt CrossOrgB", out _);

        var projectNameB = $"Org B Project {Guid.NewGuid():N}";
        var createTokenB = ExtractAntiForgeryToken(await GetHtmlAsync(clientB, "/Admin/Projects/Index"));
        await clientB.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", projectNameB), new("__RequestVerificationToken", createTokenB)]),
        });
        int projectInB;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            projectInB = await db.Projects.Where(p => p.Name == projectNameB).Select(p => p.Id).SingleAsync();
        }

        var tokenForOwnPage = ExtractAntiForgeryToken(await GetHtmlAsync(clientA, "/Admin/Projects/Index"));
        using var tamperedRequest = new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Deactivate")
        {
            Content = new FormUrlEncodedContent(
            [
                new("projectId", projectInB.ToString()),
                new("__RequestVerificationToken", tokenForOwnPage),
            ]),
        };
        var tamperedResponse = await clientA.SendAsync(tamperedRequest);

        Assert.Equal(HttpStatusCode.Redirect, tamperedResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", tamperedResponse.Headers.Location?.ToString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectInB);
            Assert.True(project.IsActive);
        }
    }

    [Fact] // A client cannot submit another organization's ProjectId when creating a ticket.
    public async Task TicketCreation_CannotSelectAnotherOrganizationsProject()
    {
        var clientA = RegisterAsync("ProjectMgmt TicketCrossOrgA", out _);
        var teamId = await CreateTeamAsync(clientA);
        int categoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            categoryId = await db.Categories.Where(c => c.TeamId == teamId).Select(c => c.Id).SingleAsync();
        }

        var clientB = RegisterAsync("ProjectMgmt TicketCrossOrgB", out _);
        var projectNameB = $"Org B Project {Guid.NewGuid():N}";
        var createTokenB = ExtractAntiForgeryToken(await GetHtmlAsync(clientB, "/Admin/Projects/Index"));
        await clientB.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", projectNameB), new("__RequestVerificationToken", createTokenB)]),
        });
        int projectInB;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            projectInB = await db.Projects.Where(p => p.Name == projectNameB).Select(p => p.Id).SingleAsync();
        }

        var createTicketToken = ExtractAntiForgeryToken(await GetHtmlAsync(clientA, "/Tickets/Create"));
        using var createTicketRequest = new HttpRequestMessage(HttpMethod.Post, "/Tickets/Create")
        {
            Content = new FormUrlEncodedContent(
            [
                new("Input.Title", "Cross-org project substitution attempt"),
                new("Input.Description", "Attempting to attach another organization's project."),
                new("Input.WorkType", "Incident"),
                new("Input.Priority", "High"),
                new("Input.CategoryId", categoryId.ToString()),
                new("Input.ProjectId", projectInB.ToString()),
                new("__RequestVerificationToken", createTicketToken),
            ]),
        };
        var response = await clientA.SendAsync(createTicketRequest);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CreateProject_MissingAntiforgeryToken_IsRejected()
    {
        var client = RegisterAsync("ProjectMgmt AntiForgery", out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Projects/Index?handler=Create")
        {
            Content = new FormUrlEncodedContent([new("CreateInput.Name", "Alpha Rollout")]),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        var capturedEmail = $"{Guid.NewGuid():N}@projectmgmttest.local";

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
