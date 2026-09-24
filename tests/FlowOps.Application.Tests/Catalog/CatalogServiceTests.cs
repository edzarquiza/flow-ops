using FlowOps.Application.Accounts;
using FlowOps.Application.Catalog;
using FlowOps.Application.Directory;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Catalog;

/// <summary>
/// CLAUDE.md §3.3's `Catalog` module, first real content: <see cref="CatalogService"/>.
/// </summary>
[Collection("Postgres")]
public sealed class CatalogServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public CatalogServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task CreateCategoryAsync_Admin_CreatesCategoryOnOwnTeam()
    {
        var world = await NewOrganizationAsync("Cat1");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        Assert.True(result.Succeeded);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == result.CategoryId);
        Assert.Equal("Incidents", category.Name);
        Assert.Equal(teamResult.TeamId, category.TeamId);
        Assert.Equal(WorkType.Incident, category.DefaultWorkType);
    }

    [Fact]
    public async Task CreateCategoryAsync_DuplicateNameOnSameTeam_Fails()
    {
        var world = await NewOrganizationAsync("Cat2");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        var result = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        Assert.False(result.Succeeded);
    }

    [Fact] // Phase 16 organization boundary: a team from a different organization is refused
           // exactly like a nonexistent one — never disclosed by a different error shape.
    public async Task CreateCategoryAsync_TeamFromAnotherOrganization_Throws()
    {
        var worldA = await NewOrganizationAsync("Cat3A");
        var worldB = await NewOrganizationAsync("Cat3B");
        var teamInB = await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");

        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => serviceA.CreateCategoryAsync(AsCurrentUser(worldA), teamInB.TeamId!.Value, "Incidents", WorkType.Incident));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task CreateCategoryAsync_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Cat4" + role);
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => service.CreateCategoryAsync(nonAdmin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident));
    }

    [Fact] // Deactivating frees the name for reuse — the unique index is filtered to active rows.
    public async Task CreateCategoryAsync_NameOfADeactivatedCategory_Succeeds()
    {
        var world = await NewOrganizationAsync("Cat5");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var first = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, first.CategoryId!.Value);

        var result = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task GetCategoriesForTeamAsync_ListsActiveAndInactiveCategories()
    {
        var world = await NewOrganizationAsync("Cat6");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, created.CategoryId!.Value);
        await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Requests", WorkType.ServiceRequest);

        var categories = await service.GetCategoriesForTeamAsync(admin, teamResult.TeamId!.Value);

        Assert.Equal(2, categories.Count);
        Assert.Contains(categories, c => c.Name == "Incidents" && !c.IsActive);
        Assert.Contains(categories, c => c.Name == "Requests" && c.IsActive);
    }

    [Fact]
    public async Task RenameCategoryAsync_Admin_RenamesOwnCategory()
    {
        var world = await NewOrganizationAsync("Cat7");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        var result = await service.RenameCategoryAsync(admin, created.CategoryId!.Value, "Priority Incidents");

        Assert.True(result.Succeeded);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == created.CategoryId);
        Assert.Equal("Priority Incidents", category.Name);
    }

    [Fact]
    public async Task RenameCategoryAsync_ToAnActiveDuplicateName_Fails()
    {
        var world = await NewOrganizationAsync("Cat8");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        var second = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Requests", WorkType.ServiceRequest);

        var result = await service.RenameCategoryAsync(admin, second.CategoryId!.Value, "Incidents");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RenameCategoryAsync_AnotherOrganizationsCategory_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Cat9A");
        var worldB = await NewOrganizationAsync("Cat9B");
        var teamInB = await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var categoryInB = await serviceB.CreateCategoryAsync(AsCurrentUser(worldB), teamInB.TeamId!.Value, "Incidents", WorkType.Incident);
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => serviceA.RenameCategoryAsync(AsCurrentUser(worldA), categoryInB.CategoryId!.Value, "Hijacked"));

        var category = await worldB.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryInB.CategoryId);
        Assert.Equal("Incidents", category.Name);
    }

    [Fact]
    public async Task DeactivateCategoryAsync_Admin_DeactivatesOwnCategory()
    {
        var world = await NewOrganizationAsync("Cat10");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        var result = await service.DeactivateCategoryAsync(admin, created.CategoryId!.Value);

        Assert.True(result.Succeeded);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == created.CategoryId);
        Assert.False(category.IsActive);
    }

    [Fact]
    public async Task DeactivateCategoryAsync_AnotherOrganizationsCategory_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Cat11A");
        var worldB = await NewOrganizationAsync("Cat11B");
        var teamInB = await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var categoryInB = await serviceB.CreateCategoryAsync(AsCurrentUser(worldB), teamInB.TeamId!.Value, "Incidents", WorkType.Incident);
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => serviceA.DeactivateCategoryAsync(AsCurrentUser(worldA), categoryInB.CategoryId!.Value));

        var category = await worldB.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryInB.CategoryId);
        Assert.True(category.IsActive);
    }

    [Fact] // A category from another organization's team must never be creatable through this
           // team's own category-management surface, even if the team id is tampered.
    public async Task CreateCategoryAsync_CannotCreateUnderAnotherOrganizationsTeam()
    {
        var worldA = await NewOrganizationAsync("Cat12A");
        var worldB = await NewOrganizationAsync("Cat12B");
        var teamInB = await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => serviceA.CreateCategoryAsync(AsCurrentUser(worldA), teamInB.TeamId!.Value, "Sneaky", WorkType.Incident));

        var count = await worldB.Context.Categories.CountAsync(c => c.TeamId == teamInB.TeamId);
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task RenameAndDeactivateCategory_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Cat13" + role);
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(() => service.RenameCategoryAsync(nonAdmin, created.CategoryId!.Value, "Hijacked"));
        await Assert.ThrowsAsync<CategoryAccessDeniedException>(() => service.DeactivateCategoryAsync(nonAdmin, created.CategoryId!.Value));
    }

    [Fact] // Phase 30B (ADR-0032): deactivation is no longer terminal.
    public async Task ReactivateCategoryAsync_Admin_ReactivatesOwnCategory()
    {
        var world = await NewOrganizationAsync("Cat14");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, created.CategoryId!.Value);

        var result = await service.ReactivateCategoryAsync(admin, created.CategoryId!.Value);

        Assert.True(result.Succeeded);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == created.CategoryId);
        Assert.True(category.IsActive);
    }

    [Fact]
    public async Task ReactivateCategoryAsync_AlreadyActive_Fails()
    {
        var world = await NewOrganizationAsync("Cat15");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        var result = await service.ReactivateCategoryAsync(admin, created.CategoryId!.Value);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReactivateCategoryAsync_NameNowTakenByAnActiveCategory_FailsWithFriendlyMessage()
    {
        var world = await NewOrganizationAsync("Cat16");
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var first = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, first.CategoryId!.Value);
        await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);

        var result = await service.ReactivateCategoryAsync(admin, first.CategoryId!.Value);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact] // ADR-0022's independence rule, unchanged by ADR-0032: a category may be reactivated
           // while its own team is still inactive.
    public async Task ReactivateCategoryAsync_SucceedsEvenWhileParentTeamIsInactive()
    {
        var world = await NewOrganizationAsync("Cat17");
        var admin = AsCurrentUser(world);
        var teamService = new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var teamResult = await teamService.CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, created.CategoryId!.Value);
        await teamService.DeactivateAsync(admin, teamResult.TeamId!.Value);

        var result = await service.ReactivateCategoryAsync(admin, created.CategoryId!.Value);

        Assert.True(result.Succeeded);
        var category = await world.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == created.CategoryId);
        Assert.True(category.IsActive);
        var team = await world.Context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamResult.TeamId);
        Assert.False(team.IsActive);
    }

    [Fact]
    public async Task ReactivateCategoryAsync_AnotherOrganizationsCategory_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Cat18A");
        var worldB = await NewOrganizationAsync("Cat18B");
        var teamInB = await new TeamService(worldB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(AsCurrentUser(worldB), "Org B Team");
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var categoryInB = await serviceB.CreateCategoryAsync(AsCurrentUser(worldB), teamInB.TeamId!.Value, "Incidents", WorkType.Incident);
        await serviceB.DeactivateCategoryAsync(AsCurrentUser(worldB), categoryInB.CategoryId!.Value);
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(
            () => serviceA.ReactivateCategoryAsync(AsCurrentUser(worldA), categoryInB.CategoryId!.Value));

        var category = await worldB.Context.Categories.AsNoTracking().SingleAsync(c => c.Id == categoryInB.CategoryId);
        Assert.False(category.IsActive);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task ReactivateCategoryAsync_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Cat19" + role);
        var admin = AsCurrentUser(world);
        var teamResult = await new TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(admin, "Service Desk");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        await service.DeactivateCategoryAsync(admin, created.CategoryId!.Value);
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<CategoryAccessDeniedException>(() => service.ReactivateCategoryAsync(nonAdmin, created.CategoryId!.Value));
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
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@catalogservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, registration.UserId!.Value, registration.OrganizationId!.Value);
    }
}
