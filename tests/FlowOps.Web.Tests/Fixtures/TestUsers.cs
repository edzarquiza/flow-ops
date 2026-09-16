using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// Deterministic, test-only seeded identities — one per role (CLAUDE.md §15: "log in as seeded
/// test users of each role"). Not the demo persona system (CLAUDE.md §14, a later phase); these
/// exist only to exercise the authentication/authorization boundary in Web.Tests, and live only
/// inside an ephemeral Testcontainers database that is destroyed when the test run ends.
/// </summary>
public static class TestUsers
{
    // Test-only, non-production password. Never used against a real deployment; the database
    // holding it exists only for the lifetime of one test run.
    public const string Password = "Test-Only-Passw0rd!1";

    public const string AdminEmail = "admin@test.flowops.local";
    public const string ManagerEmail = "manager@test.flowops.local";
    public const string AgentEmail = "agent@test.flowops.local";
    public const string ViewerEmail = "viewer@test.flowops.local";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        await CreateAsync(userManager, AdminEmail, WellKnownRoles.Admin);
        await CreateAsync(userManager, ManagerEmail, WellKnownRoles.Manager);
        await CreateAsync(userManager, AgentEmail, WellKnownRoles.Agent);
        await CreateAsync(userManager, ViewerEmail, WellKnownRoles.Viewer);
    }

    private static async Task CreateAsync(UserManager<ApplicationUser> userManager, string email, string role)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            IsActive = true,
            // Phase 24A (ADR-0024): these fixtures exist to sign in immediately, not to exercise
            // the approval gate — pre-approved exactly like DemoDataSeeder's personas.
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };

        var createResult = await userManager.CreateAsync(user, Password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to seed test user {email}: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        }

        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to assign role {role} to {email}: {string.Join("; ", roleResult.Errors.Select(e => e.Description))}");
        }
    }
}
