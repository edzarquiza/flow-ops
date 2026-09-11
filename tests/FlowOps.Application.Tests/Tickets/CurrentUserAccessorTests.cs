using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Directory;
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

        var team = new Team(0, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(team);
        await context.SaveChangesAsync();

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "Test Agent", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");
        await userManager.AddToRoleAsync(user, WellKnownRoles.Agent);

        context.Add(new TeamMember(team.Id, user.Id, isTeamManager: true, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.NotNull(currentUser);
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

    [Fact]
    public async Task GetCurrentUser_UserWithNoRole_ReturnsNull()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);

        var user = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@test.local", Email = $"{Guid.NewGuid():N}@test.local", DisplayName = "No Role", IsActive = true };
        await userManager.CreateAsync(user, "Test-Only-Passw0rd!1");

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(user.Id);

        Assert.Null(currentUser);
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
