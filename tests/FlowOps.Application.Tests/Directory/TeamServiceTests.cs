using FlowOps.Application.Accounts;
using FlowOps.Application.Catalog;
using FlowOps.Application.Directory;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Directory;

/// <summary>
/// CLAUDE.md §3.3's `Directory` module, first real content: <see cref="TeamService"/>. Every test
/// registers its own disposable organization via the real registration flow (mirroring
/// <c>Organizations.MembershipServiceTests</c>) so its Admin genuinely has no Identity role claim —
/// the same real-world shape ADR-0021 fixed <c>/Admin</c>'s authorization for.
/// </summary>
[Collection("Postgres")]
public sealed class TeamServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TeamServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task CreateAsync_Admin_CreatesTeamInOwnOrganization()
    {
        var world = await NewOrganizationAsync("Team1");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.CreateAsync(admin, "Service Desk");

        Assert.True(result.Succeeded);
        var team = await world.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == result.TeamId);
        Assert.Equal("Service Desk", team.Name);
        Assert.Equal(world.OrganizationId, team.OrganizationId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameInSameOrganization_Fails()
    {
        var world = await NewOrganizationAsync("Team2");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateAsync(admin, "Service Desk");

        var result = await service.CreateAsync(admin, "Service Desk");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact] // The same name is fine in a different organization — uniqueness is per-org (Phase 16).
    public async Task CreateAsync_SameNameInDifferentOrganization_Succeeds()
    {
        var worldA = await NewOrganizationAsync("Team3A");
        var worldB = await NewOrganizationAsync("Team3B");
        var service = new TeamService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateAsync(AsCurrentUser(worldA), "Service Desk");

        var serviceB = new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var result = await serviceB.CreateAsync(AsCurrentUser(worldB), "Service Desk");

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task CreateAsync_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Team4" + role);
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => service.CreateAsync(nonAdmin, "Service Desk"));
    }

    [Fact]
    public async Task CreateAsync_NameTooLong_Fails()
    {
        var world = await NewOrganizationAsync("Team5");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.CreateAsync(admin, new string('a', TeamService.MaxNameLength + 1));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task GetTeamsAsync_NeverIncludesAnotherOrganizationsTeams()
    {
        var worldA = await NewOrganizationAsync("Team6A");
        var worldB = await NewOrganizationAsync("Team6B");
        await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");

        var teamsInA = await new TeamService(worldA.Context, new TicketTestData.FixedTimeProvider(Now)).GetTeamsAsync(AsCurrentUser(worldA));

        Assert.DoesNotContain(teamsInA, t => t.TeamName == "Org B Team");
    }

    [Fact]
    public async Task RenameAsync_Admin_RenamesOwnTeam()
    {
        var world = await NewOrganizationAsync("Team7");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateAsync(admin, "Service Desk");

        var result = await service.RenameAsync(admin, created.TeamId!.Value, "Support Desk");

        Assert.True(result.Succeeded);
        var team = await world.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == created.TeamId);
        Assert.Equal("Support Desk", team.Name);
    }

    [Fact]
    public async Task RenameAsync_ToAnActiveDuplicateName_Fails()
    {
        var world = await NewOrganizationAsync("Team8");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateAsync(admin, "Service Desk");
        var second = await service.CreateAsync(admin, "Support Desk");

        var result = await service.RenameAsync(admin, second.TeamId!.Value, "Service Desk");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RenameAsync_AnotherOrganizationsTeam_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Team9A");
        var worldB = await NewOrganizationAsync("Team9B");
        var serviceB = new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var teamInB = await serviceB.CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceA = new TeamService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<TeamAccessDeniedException>(
            () => serviceA.RenameAsync(AsCurrentUser(worldA), teamInB.TeamId!.Value, "Hijacked"));

        var team = await worldB.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamInB.TeamId);
        Assert.Equal("Org B Team", team.Name);
    }

    [Fact]
    public async Task DeactivateAsync_Admin_DeactivatesOwnTeam()
    {
        var world = await NewOrganizationAsync("Team10");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateAsync(admin, "Service Desk");

        var result = await service.DeactivateAsync(admin, created.TeamId!.Value);

        Assert.True(result.Succeeded);
        var team = await world.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == created.TeamId);
        Assert.False(team.IsActive);
    }

    [Fact] // Deactivating frees the name for reuse — the unique index is filtered to active rows.
    public async Task CreateAsync_NameOfADeactivatedTeam_Succeeds()
    {
        var world = await NewOrganizationAsync("Team11");
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var first = await service.CreateAsync(admin, "Service Desk");
        await service.DeactivateAsync(admin, first.TeamId!.Value);

        var result = await service.CreateAsync(admin, "Service Desk");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DeactivateAsync_AnotherOrganizationsTeam_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Team12A");
        var worldB = await NewOrganizationAsync("Team12B");
        var serviceB = new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var teamInB = await serviceB.CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceA = new TeamService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<TeamAccessDeniedException>(
            () => serviceA.DeactivateAsync(AsCurrentUser(worldA), teamInB.TeamId!.Value));

        var team = await worldB.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamInB.TeamId);
        Assert.True(team.IsActive);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task RenameAndDeactivate_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Team13" + role);
        var admin = AsCurrentUser(world);
        var service = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateAsync(admin, "Service Desk");
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => service.RenameAsync(nonAdmin, created.TeamId!.Value, "Hijacked"));
        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => service.DeactivateAsync(nonAdmin, created.TeamId!.Value));
    }

    [Fact] // Deactivating a team must never affect its members, categories, or historical tickets.
    public async Task DeactivateAsync_DoesNotAffectMembersCategoriesOrTickets()
    {
        var world = await NewOrganizationAsync("Team14");
        var admin = AsCurrentUser(world);
        var teamService = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var catalogService = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await teamService.CreateAsync(admin, "Service Desk");
        var categoryResult = await catalogService.CreateCategoryAsync(admin, created.TeamId!.Value, "Incidents", WorkType.Incident);
        var memberId = await AddOrganizationMemberAsync(world, "Agent One", UserRole.Agent);
        await teamService.AddMemberAsync(admin, created.TeamId!.Value, memberId);
        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var agent = new CurrentUser(memberId, world.OrganizationId, UserRole.Agent, new HashSet<int> { created.TeamId!.Value }, new HashSet<int>());
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("Printer jam", "The printer on the third floor is jammed.", WorkType.Incident, Priority.Medium, created.TeamId.Value, categoryResult.CategoryId!.Value, null),
            agent);

        var result = await teamService.DeactivateAsync(admin, created.TeamId.Value);

        Assert.True(result.Succeeded);
        var membership = await world.Context.TeamMembers.AsNoTracking().SingleOrDefaultAsync(m => m.TeamId == created.TeamId && m.UserId == memberId);
        Assert.NotNull(membership);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryResult.CategoryId);
        Assert.True(category.IsActive);
        var ticket = await world.Context.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(created.TeamId, ticket.TeamId);
    }

    private async Task<Guid> AddOrganizationMemberAsync(World world, string displayName, UserRole role)
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@teamservice.test.local",
            Email = $"{Guid.NewGuid():N}@teamservice.test.local",
            EmailConfirmed = true,
            DisplayName = displayName,
            IsActive = true,
        };
        world.Context.Users.Add(user);
        await world.Context.SaveChangesAsync();
        world.Context.OrganizationMemberships.Add(new FlowOps.Domain.Organizations.OrganizationMembership(0, world.OrganizationId, user.Id, role, Now));
        await world.Context.SaveChangesAsync();
        return user.Id;
    }

    private static CurrentUser AsCurrentUser(World world) =>
        new(world.AdminId, world.OrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>());

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@teamservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, registration.UserId!.Value, registration.OrganizationId!.Value);
    }
}
