using FlowOps.Application.Accounts;
using FlowOps.Application.Catalog;
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
/// Project management: the smallest legitimate way for an organization to populate the Create
/// Ticket "Project (optional)" dropdown, which previously had no data source beyond
/// <c>DemoDataSeeder</c>. Extends the same <see cref="CatalogService"/> boundary category management
/// already uses — no new abstraction.
/// </summary>
[Collection("Postgres")]
public sealed class ProjectServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public ProjectServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task GetProjectsAsync_Admin_ListsOnlyOwnOrganizationsProjects()
    {
        var worldA = await NewOrganizationAsync("Proj1A");
        var worldB = await NewOrganizationAsync("Proj1B");
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        await serviceA.CreateProjectAsync(AsCurrentUser(worldA), "Alpha Rollout");
        await serviceB.CreateProjectAsync(AsCurrentUser(worldB), "Beta Rollout");

        var projects = await serviceA.GetProjectsAsync(AsCurrentUser(worldA));

        var project = Assert.Single(projects);
        Assert.Equal("Alpha Rollout", project.Name);
    }

    [Fact]
    public async Task CreateProjectAsync_Admin_CreatesActiveProject()
    {
        var world = await NewOrganizationAsync("Proj2");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.CreateProjectAsync(AsCurrentUser(world), "  Alpha Rollout  ");

        Assert.True(result.Succeeded);
        var project = await world.Context.Projects.AsNoTracking().SingleAsync(p => p.Id == result.ProjectId);
        Assert.Equal("Alpha Rollout", project.Name); // trimmed
        Assert.True(project.IsActive);
    }

    [Fact]
    public async Task CreateProjectAsync_DuplicateActiveName_Fails()
    {
        var world = await NewOrganizationAsync("Proj3");
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateProjectAsync(AsCurrentUser(world), "Alpha Rollout");

        var result = await service.CreateProjectAsync(AsCurrentUser(world), "Alpha Rollout");

        Assert.False(result.Succeeded);
    }

    [Fact] // Deactivating frees the name for reuse — the unique index is filtered to active rows.
    public async Task CreateProjectAsync_NameOfADeactivatedProject_Succeeds()
    {
        var world = await NewOrganizationAsync("Proj4");
        var admin = AsCurrentUser(world);
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var first = await service.CreateProjectAsync(admin, "Alpha Rollout");
        await service.DeactivateProjectAsync(admin, first.ProjectId!.Value);

        var result = await service.CreateProjectAsync(admin, "Alpha Rollout");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RenameProjectAsync_Admin_RenamesOwnProject()
    {
        var world = await NewOrganizationAsync("Proj5");
        var admin = AsCurrentUser(world);
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateProjectAsync(admin, "Alpha Rollout");

        var result = await service.RenameProjectAsync(admin, created.ProjectId!.Value, "Alpha Rollout v2");

        Assert.True(result.Succeeded);
        var project = await world.Context.Projects.AsNoTracking().SingleAsync(p => p.Id == created.ProjectId);
        Assert.Equal("Alpha Rollout v2", project.Name);
    }

    [Fact]
    public async Task RenameProjectAsync_ToAnActiveDuplicateName_Fails()
    {
        var world = await NewOrganizationAsync("Proj6");
        var admin = AsCurrentUser(world);
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.CreateProjectAsync(admin, "Alpha Rollout");
        var second = await service.CreateProjectAsync(admin, "Beta Rollout");

        var result = await service.RenameProjectAsync(admin, second.ProjectId!.Value, "Alpha Rollout");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RenameProjectAsync_AnotherOrganizationsProject_Throws()
    {
        var worldA = await NewOrganizationAsync("Proj7A");
        var worldB = await NewOrganizationAsync("Proj7B");
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var projectInB = await serviceB.CreateProjectAsync(AsCurrentUser(worldB), "Beta Rollout");
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ProjectAccessDeniedException>(
            () => serviceA.RenameProjectAsync(AsCurrentUser(worldA), projectInB.ProjectId!.Value, "Hijacked"));
    }

    [Fact]
    public async Task DeactivateProjectAsync_Admin_DeactivatesOwnProject()
    {
        var world = await NewOrganizationAsync("Proj8");
        var admin = AsCurrentUser(world);
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateProjectAsync(admin, "Alpha Rollout");

        var result = await service.DeactivateProjectAsync(admin, created.ProjectId!.Value);

        Assert.True(result.Succeeded);
        var project = await world.Context.Projects.AsNoTracking().SingleAsync(p => p.Id == created.ProjectId);
        Assert.False(project.IsActive);
    }

    [Fact]
    public async Task DeactivateProjectAsync_AnotherOrganizationsProject_ThrowsAndDoesNotMutate()
    {
        var worldA = await NewOrganizationAsync("Proj9A");
        var worldB = await NewOrganizationAsync("Proj9B");
        var serviceB = new CatalogService(worldB.Context, new TicketTestData.FixedTimeProvider(Now));
        var projectInB = await serviceB.CreateProjectAsync(AsCurrentUser(worldB), "Beta Rollout");
        var serviceA = new CatalogService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ProjectAccessDeniedException>(
            () => serviceA.DeactivateProjectAsync(AsCurrentUser(worldA), projectInB.ProjectId!.Value));

        var project = await worldB.Context.Projects.AsNoTracking().SingleAsync(p => p.Id == projectInB.ProjectId);
        Assert.True(project.IsActive);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task CreateProjectAsync_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Proj10" + role);
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ProjectAccessDeniedException>(
            () => service.CreateProjectAsync(nonAdmin, "Alpha Rollout"));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task RenameAndDeactivate_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("Proj11" + role);
        var admin = AsCurrentUser(world);
        var service = new CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var created = await service.CreateProjectAsync(admin, "Alpha Rollout");
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<ProjectAccessDeniedException>(
            () => service.RenameProjectAsync(nonAdmin, created.ProjectId!.Value, "Hijacked"));
        await Assert.ThrowsAsync<ProjectAccessDeniedException>(
            () => service.DeactivateProjectAsync(nonAdmin, created.ProjectId!.Value));
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
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@projectservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, registration.UserId!.Value, registration.OrganizationId!.Value);
    }
}
