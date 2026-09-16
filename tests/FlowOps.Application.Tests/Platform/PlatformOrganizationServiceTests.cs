using FlowOps.Application.Accounts;
using FlowOps.Application.Organizations;
using FlowOps.Application.Platform;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Domain.Platform;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Platform;

/// <summary>Phase 24 (ADR-0023): <see cref="PlatformOrganizationService"/> — deliberately not
/// scoped to any one caller's organization, since a Platform Admin operates above the tenant
/// boundary by definition.</summary>
[Collection("Postgres")]
public sealed class PlatformOrganizationServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public PlatformOrganizationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task ListOrganizationsAsync_IncludesActiveAndInactiveOrganizations()
    {
        // Scoped by a search term unique to this test — other tests in this shared Postgres
        // database may have already created 25+ organizations, so an unfiltered page 1 (ordered by
        // name) is not guaranteed to contain either of these two.
        var uniqueLabel = $"PlatformOrgListing-{Guid.NewGuid():N}";
        var worldA = await NewOrganizationAsync(uniqueLabel + "A");
        var worldB = await NewOrganizationAsync(uniqueLabel + "B");
        var service = new PlatformOrganizationService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateOrganizationAsync(PlatformAdmin(worldA), worldB.OrganizationId);

        var result = await service.ListOrganizationsAsync(pageNumber: 1, search: uniqueLabel);

        Assert.Contains(result.Items, o => o.OrganizationId == worldA.OrganizationId && o.IsActive);
        Assert.Contains(result.Items, o => o.OrganizationId == worldB.OrganizationId && !o.IsActive);
    }

    [Fact]
    public async Task ListOrganizationsAsync_SearchFiltersAndCountsAreCorrect()
    {
        var world = await NewOrganizationAsync("PlatformSearchUnique");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.ListOrganizationsAsync(pageNumber: 1, search: "PlatformSearchUnique");

        var item = Assert.Single(result.Items);
        Assert.Equal(world.OrganizationId, item.OrganizationId);
        Assert.Equal(1, item.MemberCount); // the Admin created at registration
    }

    [Fact]
    public async Task GetOrganizationDetailAsync_UnknownId_ReturnsNull()
    {
        var world = await NewOrganizationAsync("PO3");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetOrganizationDetailAsync(-1);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeactivateAsync_Admin_DeactivatesOrganization()
    {
        var world = await NewOrganizationAsync("PO4");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.DeactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        Assert.True(result.Succeeded);
        var detail = await service.GetOrganizationDetailAsync(world.OrganizationId);
        Assert.False(detail!.IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_AlreadyInactive_FailsWithoutMutation()
    {
        var world = await NewOrganizationAsync("PO5");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        var result = await service.DeactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReactivateAsync_RestoresActiveState_WithoutDuplicatingAnyRecord()
    {
        var world = await NewOrganizationAsync("PO6");
        var teamId = await TicketTestData.AddTeamAsync(world.Context);
        var categoryId = await TicketTestData.AddCategoryAsync(world.Context, teamId);
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        var result = await service.ReactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        Assert.True(result.Succeeded);
        var detail = await service.GetOrganizationDetailAsync(world.OrganizationId);
        Assert.True(detail!.IsActive);
        var teamCount = await world.Context.Teams.CountAsync(t => t.Id == teamId);
        var categoryCount = await world.Context.Categories.CountAsync(c => c.Id == categoryId);
        Assert.Equal(1, teamCount);
        Assert.Equal(1, categoryCount);
    }

    [Fact]
    public async Task DeactivateAndReactivate_RecordAuditEvents()
    {
        var world = await NewOrganizationAsync("PO7");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var actor = PlatformAdmin(world);
        await service.DeactivateOrganizationAsync(actor, world.OrganizationId);
        await service.ReactivateOrganizationAsync(actor, world.OrganizationId);

        var events = await world.Context.PlatformAuditEvents
            .Where(e => e.TargetOrganizationId == world.OrganizationId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(PlatformEventType.OrganizationDeactivated, events[0].EventType);
        Assert.Equal(PlatformEventType.OrganizationReactivated, events[1].EventType);
        Assert.All(events, e => Assert.Equal(actor.UserId, e.ActorUserId));
    }

    [Fact] // Deactivation must never delete or alter any membership/team/category/project/ticket.
    public async Task DeactivateAsync_PreservesAllHistoricalData()
    {
        var world = await NewOrganizationAsync("PO8");
        var admin = AsCurrentUser(world);
        var teamService = new FlowOps.Application.Directory.TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var catalogService = new FlowOps.Application.Catalog.CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var teamResult = await teamService.CreateAsync(admin, "Support");
        var categoryResult = await catalogService.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", FlowOps.Domain.Tickets.WorkType.Incident);
        var ticketService = new FlowOps.Application.Tickets.TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var (ticketId, _) = await ticketService.CreateAsync(
            new FlowOps.Application.Tickets.CreateTicketRequest("Server down", "The primary server is unresponsive.", FlowOps.Domain.Tickets.WorkType.Incident, FlowOps.Domain.Tickets.Priority.Critical, teamResult.TeamId.Value, categoryResult.CategoryId!.Value, null),
            new FlowOps.Domain.Tickets.CurrentUser(world.AdminId, world.OrganizationId, FlowOps.Domain.Tickets.UserRole.Admin, new HashSet<int>(), new HashSet<int>()));

        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateOrganizationAsync(PlatformAdmin(world), world.OrganizationId);

        var membershipCount = await world.Context.OrganizationMemberships.CountAsync(m => m.OrganizationId == world.OrganizationId);
        var team = await world.Context.Teams.SingleAsync(t => t.Id == teamResult.TeamId);
        var category = await world.Context.Categories.SingleAsync(c => c.Id == categoryResult.CategoryId);
        var ticket = await world.Context.Tickets.SingleAsync(t => t.Id == ticketId);

        Assert.Equal(1, membershipCount);
        Assert.Equal("Support", team.Name);
        Assert.Equal("Incidents", category.Name);
        Assert.Equal(teamResult.TeamId, ticket.TeamId);
    }

    [Fact]
    public async Task RenameOrganizationAsync_Admin_UpdatesName()
    {
        var world = await NewOrganizationAsync("PO9");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RenameOrganizationAsync(PlatformAdmin(world), world.OrganizationId, "  Renamed Org  ");

        Assert.True(result.Succeeded);
        var detail = await service.GetOrganizationDetailAsync(world.OrganizationId);
        Assert.Equal("Renamed Org", detail!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameOrganizationAsync_BlankName_Fails(string name)
    {
        var world = await NewOrganizationAsync("PO10");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RenameOrganizationAsync(PlatformAdmin(world), world.OrganizationId, name);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RenameOrganizationAsync_UnknownId_Throws()
    {
        var world = await NewOrganizationAsync("PO11");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<PlatformAccessDeniedException>(() =>
            service.RenameOrganizationAsync(PlatformAdmin(world), -1, "New Name"));
    }

    [Fact]
    public async Task GetPendingInvitationsAsync_ReturnsOnlyUnacceptedUnexpiredInvitations()
    {
        var world = await NewOrganizationAsync("PO12");
        var services = new ServiceCollection();
        services.AddSingleton(world.Context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var provider = services.BuildServiceProvider();
        var invitations = new InvitationService(world.Context, provider.GetRequiredService<UserManager<ApplicationUser>>(), provider.GetRequiredService<ILookupNormalizer>(), new TicketTestData.FixedTimeProvider(Now));
        var admin = AsCurrentUser(world);
        var pendingEmail = $"{Guid.NewGuid():N}@platformorgservice.test.local";
        await invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(pendingEmail, UserRole.Agent));

        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var result = await service.GetPendingInvitationsAsync(world.OrganizationId);

        var item = Assert.Single(result);
        Assert.Equal(pendingEmail, item.Email);
        Assert.Equal(UserRole.Agent.ToString(), item.Role);
    }

    [Fact]
    public async Task GetRecentAuditEventsAsync_ReturnsMostRecentFirst()
    {
        var world = await NewOrganizationAsync("PO13");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var actor = PlatformAdmin(world);
        await service.DeactivateOrganizationAsync(actor, world.OrganizationId);
        await service.ReactivateOrganizationAsync(actor, world.OrganizationId);

        var events = await service.GetRecentAuditEventsAsync(world.OrganizationId);

        Assert.Equal(2, events.Count);
        Assert.Equal(PlatformEventType.OrganizationReactivated, events[0].EventType);
        Assert.Equal(PlatformEventType.OrganizationDeactivated, events[1].EventType);
    }

    // ---------------------------------------------------------------------------------------
    // Phase 24B: the Platform homepage's own queries
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetOrganizationStatusSummaryAsync_CountsActiveAndInactiveSeparately()
    {
        var worldA = await NewOrganizationAsync("HomeSummaryA");
        var worldB = await NewOrganizationAsync("HomeSummaryB");
        var service = new PlatformOrganizationService(worldA.Context, new TicketTestData.FixedTimeProvider(Now));
        var before = await service.GetOrganizationStatusSummaryAsync();

        await service.DeactivateOrganizationAsync(PlatformAdmin(worldB), worldB.OrganizationId);
        var after = await service.GetOrganizationStatusSummaryAsync();

        Assert.Equal(before.Total, after.Total); // moved between buckets, nothing created or destroyed
        Assert.Equal(before.Active - 1, after.Active);
        Assert.Equal(before.Inactive + 1, after.Inactive);
    }

    [Fact]
    public async Task GetTicketSummaryAsync_CountsTotalAndRecentTickets()
    {
        var world = await NewOrganizationAsync("HomeTicketSummary");
        var admin = AsCurrentUser(world);
        var teamService = new FlowOps.Application.Directory.TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var catalogService = new FlowOps.Application.Catalog.CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var teamResult = await teamService.CreateAsync(admin, "Support");
        var categoryResult = await catalogService.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", FlowOps.Domain.Tickets.WorkType.Incident);
        var ticketService = new FlowOps.Application.Tickets.TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var before = await service.GetTicketSummaryAsync();

        await ticketService.CreateAsync(
            new FlowOps.Application.Tickets.CreateTicketRequest("Homepage KPI ticket", "Counted by the platform ticket summary.", FlowOps.Domain.Tickets.WorkType.Incident, FlowOps.Domain.Tickets.Priority.Low, teamResult.TeamId.Value, categoryResult.CategoryId!.Value, null),
            admin);

        var after = await service.GetTicketSummaryAsync();

        Assert.Equal(before.Total + 1, after.Total);
        Assert.Equal(before.CreatedInLast30Days + 1, after.CreatedInLast30Days); // created "now" — always within the trailing-30-day window
    }

    [Fact]
    public async Task GetPriorityOrganizationsAsync_OrganizationsWithAPendingAccountRankFirst()
    {
        var pendingWorld = await NewPendingOrganizationAsync("HomePriorityPending");
        var activeWorld = await NewOrganizationAsync("HomePriorityActive"); // registered after — "newer" by CreatedAt, but must still rank below the pending one
        var service = new PlatformOrganizationService(pendingWorld.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetPriorityOrganizationsAsync(take: 1000);

        var items = result.ToList();
        var pendingIndex = items.FindIndex(o => o.OrganizationId == pendingWorld.OrganizationId);
        var activeIndex = items.FindIndex(o => o.OrganizationId == activeWorld.OrganizationId);
        Assert.True(pendingIndex >= 0, "The organization with a Pending account was not returned.");
        Assert.True(activeIndex >= 0, "The plain active organization was not returned.");
        Assert.True(pendingIndex < activeIndex, "An organization with a Pending account must rank ahead of an ordinary active organization.");
    }

    [Fact]
    public async Task GetPriorityOrganizationsAsync_NeverExceedsTake()
    {
        var world = await NewOrganizationAsync("HomePriorityBound");
        var service = new PlatformOrganizationService(world.Context, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetPriorityOrganizationsAsync(take: 3);

        Assert.True(result.Count <= 3);
    }

    private static PlatformAdminIdentity PlatformAdmin(World world) => new(world.AdminId, "Platform Operator");

    private static FlowOps.Domain.Tickets.CurrentUser AsCurrentUser(World world) =>
        new(world.AdminId, world.OrganizationId, FlowOps.Domain.Tickets.UserRole.Admin, new HashSet<int>(), new HashSet<int>());

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@platformorgservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        // Phase 24A (ADR-0024): self-registration alone leaves the founder Pending. This fixture
        // exists to exercise organization lifecycle, not the registration-approval gate — approved
        // immediately, the same shortcut TicketTestData.AddUserAsync already takes.
        var founder = await context.Users.SingleAsync(u => u.Id == registration.UserId!.Value);
        founder.RegistrationApprovedAt = Now;
        await context.SaveChangesAsync();

        return new World(context, registration.UserId!.Value, registration.OrganizationId!.Value);
    }

    /// <summary>The genuine, unmodified result of self-registration (ADR-0024): the founder is
    /// Pending — for tests of the homepage's pending-account-aware organization ordering.</summary>
    private async Task<World> NewPendingOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@platformorgservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, registration.UserId!.Value, registration.OrganizationId!.Value);
    }
}
