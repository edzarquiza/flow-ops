using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// AUTH-RULE-04: proves <see cref="CurrentUserAccessor"/> resolves role and team membership from
/// the real database rather than trusting any client-supplied claim — the whole reason it needs
/// a real Postgres instance rather than a Domain.Tests-style in-memory check.
/// </summary>
[Collection("Postgres")]
public sealed class CurrentUserAccessorTests
{
    private readonly PostgresFixture _fixture;

    public CurrentUserAccessorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetCurrentUser_ActiveUserWithRoleAndTeam_ReturnsPopulatedCurrentUser()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var organization = new Organization(0, $"Org-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(organization);
        await context.SaveChangesAsync();

        var team = new Team(0, organization.Id, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(team);
        await context.SaveChangesAsync();

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "Test Agent", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");
        await userManager.AddToRoleAsync(user, WellKnownRoles.Agent);

        // Phase 16: role/organization now come from OrganizationMembership, not Identity's own
        // per-user role assignment (kept above only as the coarse-gate mirror).
        context.Add(new OrganizationMembership(0, organization.Id, user.Id, UserRole.Agent, DateTimeOffset.UtcNow));
        context.Add(new TeamMember(team.Id, user.Id, isTeamManager: true, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.NotNull(currentUser);
        Assert.Equal(organization.Id, currentUser.OrganizationId);
        Assert.Equal(UserRole.Agent, currentUser.Role);
        Assert.Contains(team.Id, currentUser.MemberTeamIds);
        Assert.Contains(team.Id, currentUser.ManagedTeamIds); // IsTeamManager: true
    }

    [Fact] // "Active/inactive users" — an inactive user is not a valid CurrentUser
    public async Task GetCurrentUser_InactiveUser_ReturnsNull()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "Inactive User", IsActive = false };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");
        await userManager.AddToRoleAsync(user, WellKnownRoles.Viewer);

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.Null(currentUser);
    }

    [Fact] // Phase 16: role is authoritatively sourced from OrganizationMembership, so a user with
           // no membership at all returns null regardless of any Identity role assignment.
    public async Task GetCurrentUser_UserWithNoOrganizationMembership_ReturnsNull()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "No Role", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.Null(currentUser);
    }

    [Fact] // Phase 24 (ADR-0023): a membership in a deactivated organization is never resolvable —
           // this is what makes organization deactivation actually stop ordinary tenant operation.
    public async Task GetCurrentUser_OnlyMembershipInDeactivatedOrganization_ReturnsNull()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var organization = new Organization(0, $"Org-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(organization);
        await context.SaveChangesAsync();

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "Test Agent", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");
        context.Add(new OrganizationMembership(0, organization.Id, user.Id, UserRole.Admin, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        organization.Deactivate();
        await context.SaveChangesAsync();

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.Null(currentUser);
    }

    [Fact] // A user with memberships in both an active and a deactivated organization is still
           // resolved — into the active one only; the deactivated membership is simply invisible.
    public async Task GetCurrentUser_OneActiveOneDeactivatedOrganization_ResolvesOnlyTheActiveOne()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var activeOrg = new Organization(0, $"Active-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        var inactiveOrg = new Organization(0, $"Inactive-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(activeOrg);
        context.Add(inactiveOrg);
        await context.SaveChangesAsync();

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "Multi Org", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");
        context.Add(new OrganizationMembership(0, activeOrg.Id, user.Id, UserRole.Admin, DateTimeOffset.UtcNow));
        context.Add(new OrganizationMembership(0, inactiveOrg.Id, user.Id, UserRole.Admin, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        inactiveOrg.Deactivate();
        await context.SaveChangesAsync();

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);
        var available = await accessor.GetAvailableOrganizationsAsync(user.Id);
        var switchToInactive = await accessor.TrySwitchOrganizationAsync(user.Id, inactiveOrg.Id);

        Assert.NotNull(currentUser);
        Assert.Equal(activeOrg.Id, currentUser.OrganizationId);
        Assert.Single(available);
        Assert.Equal(activeOrg.Id, available[0].Id);
        Assert.False(switchToInactive);
    }

    [Fact]
    public async Task GetCurrentUser_UnknownUserId_ReturnsNull()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(Guid.NewGuid());

        Assert.Null(currentUser);
    }

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<FlowOpsDbContext>();

        var provider = services.BuildServiceProvider();

        // Ensure the four roles this test run needs actually exist (the migration seeds them,
        // but each Testcontainers database is fresh — the seed runs via MigrateAsync already).
        return provider.GetRequiredService<UserManager<ApplicationUser>>();
    }
}
