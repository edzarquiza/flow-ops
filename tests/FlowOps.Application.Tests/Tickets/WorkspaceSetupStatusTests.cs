using FlowOps.Application.Accounts;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
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
/// ADR-0020: the first-run workspace checklist is derived entirely from live data, scoped to the
/// caller's own organization — no persisted onboarding state to test for staleness, only whether
/// the three underlying facts (team, active membership, ticket) are read correctly and stay
/// organization-isolated.
/// </summary>
[Collection("Postgres")]
public sealed class WorkspaceSetupStatusTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public WorkspaceSetupStatusTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task NewOrganization_AllApplicableItemsAreIncomplete()
    {
        var world = await NewOrganizationAsync("Setup1");
        var status = await GetStatusAsync(world);

        Assert.False(status.HasTeam);
        Assert.False(status.HasMultipleActiveMembers);
        Assert.False(status.HasTicket);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task OrganizationWithTeam_TeamItemIsComplete()
    {
        var world = await NewOrganizationAsync("Setup2");
        world.Context.Teams.Add(new Team(0, world.OrganizationId, "A Team", Now));
        await world.Context.SaveChangesAsync();

        var status = await GetStatusAsync(world);

        Assert.True(status.HasTeam);
        Assert.False(status.HasMultipleActiveMembers);
        Assert.False(status.HasTicket);
    }

    [Fact]
    public async Task OrganizationWithSecondActiveMember_InviteItemIsComplete()
    {
        var world = await NewOrganizationAsync("Setup3");
        var secondUserId = await TicketTestData.AddUserAsync(world.Context);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, secondUserId, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();

        var status = await GetStatusAsync(world);

        Assert.True(status.HasMultipleActiveMembers);
    }

    [Fact] // The open question from the spec: sending an invite alone must not complete this item.
    public async Task PendingInvitationAlone_DoesNotCompleteInviteItem()
    {
        var world = await NewOrganizationAsync("Setup3b");
        var invitations = new InvitationService(world.Context, world.UserManager, world.UserManager.KeyNormalizer, new TicketTestData.FixedTimeProvider(Now));
        var admin = AsCurrentUser(world);

        var result = await invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        Assert.True(result.Succeeded);
        var status = await GetStatusAsync(world);
        Assert.False(status.HasMultipleActiveMembers);
    }

    [Fact] // A deactivated second member does not count as "active."
    public async Task DeactivatedSecondMember_DoesNotCompleteInviteItem()
    {
        var world = await NewOrganizationAsync("Setup3c");
        var secondUserId = await TicketTestData.AddUserAsync(world.Context);
        var secondUser = await world.Context.Users.SingleAsync(u => u.Id == secondUserId);
        secondUser.IsActive = false;
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, secondUserId, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();

        var status = await GetStatusAsync(world);

        Assert.False(status.HasMultipleActiveMembers);
    }

    [Fact]
    public async Task OrganizationWithTicket_TicketItemIsComplete()
    {
        var world = await NewOrganizationAsync("Setup4");
        var team = new Team(0, world.OrganizationId, "A Team", Now);
        world.Context.Teams.Add(team);
        await world.Context.SaveChangesAsync();
        var category = new Category(0, team.Id, "A Category", WorkType.Incident, Now);
        world.Context.Categories.Add(category);
        await world.Context.SaveChangesAsync();

        var admin = AsCurrentUser(world);
        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await ticketService.CreateAsync(
            new CreateTicketRequest("A ticket for setup status", "A routine description.", WorkType.Incident, Priority.Medium, team.Id, category.Id, null),
            admin);

        var status = await GetStatusAsync(world);

        Assert.True(status.HasTeam);
        Assert.True(status.HasTicket);
    }

    [Fact]
    public async Task FullyConfiguredOrganization_EveryItemComplete()
    {
        var world = await NewOrganizationAsync("Setup5");
        var team = new Team(0, world.OrganizationId, "A Team", Now);
        world.Context.Teams.Add(team);
        await world.Context.SaveChangesAsync();
        var category = new Category(0, team.Id, "A Category", WorkType.Incident, Now);
        world.Context.Categories.Add(category);
        await world.Context.SaveChangesAsync();

        var secondUserId = await TicketTestData.AddUserAsync(world.Context);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, secondUserId, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();

        var admin = AsCurrentUser(world);
        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        await ticketService.CreateAsync(
            new CreateTicketRequest("A ticket for setup status", "A routine description.", WorkType.Incident, Priority.Medium, team.Id, category.Id, null),
            admin);

        var status = await GetStatusAsync(world);

        Assert.True(status.IsComplete);
    }

    [Fact] // Multi-tenant: Org A's setup state never reflects Org B's data.
    public async Task CrossOrganizationIsolation_EachOrganizationSeesOnlyItsOwnState()
    {
        var orgA = await NewOrganizationAsync("SetupCrossA");
        var orgB = await NewOrganizationAsync("SetupCrossB");

        var teamB = new Team(0, orgB.OrganizationId, "Org B Team", Now);
        orgB.Context.Teams.Add(teamB);
        await orgB.Context.SaveChangesAsync();
        var secondUserB = await TicketTestData.AddUserAsync(orgB.Context);
        orgB.Context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.OrganizationId, secondUserB, UserRole.Agent, Now));
        await orgB.Context.SaveChangesAsync();

        var statusA = await GetStatusAsync(orgA);
        var statusB = await GetStatusAsync(orgB);

        Assert.False(statusA.HasTeam);
        Assert.False(statusA.HasMultipleActiveMembers);
        Assert.True(statusB.HasTeam);
        Assert.True(statusB.HasMultipleActiveMembers);
    }

    private static async Task<WorkspaceSetupStatus> GetStatusAsync(World world)
    {
        var admin = AsCurrentUser(world);
        var service = new AnalyticsQueryService(world.Context, TimeProvider.System);
        return await service.GetWorkspaceSetupStatusAsync(admin);
    }

    private static CurrentUser AsCurrentUser(World world) =>
        new(world.AdminId, world.OrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>());

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", UniqueEmail(), Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, userManager, registration.UserId!.Value, registration.OrganizationId!.Value);
    }

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@setup.test.local";
}
