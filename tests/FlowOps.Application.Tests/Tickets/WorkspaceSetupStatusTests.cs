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
/// ADR-0020/ADR-0026: the first-run workspace checklist is derived from live data (team, invite,
/// project, ticket) plus two persisted skip flags on <see cref="Organization"/> — no persisted
/// state for anything else, only whether the underlying facts are read correctly and stay
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
        Assert.False(status.HasSentInvitationOrMember);
        Assert.False(status.HasProject);
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
        Assert.False(status.HasSentInvitationOrMember);
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

        Assert.True(status.HasSentInvitationOrMember);
        Assert.True(status.InviteComplete);
    }

    [Fact] // ADR-0026: reverses ADR-0020's original rule — sending an invite is now enough on its
           // own, since that is the Admin's own action, not an outcome the invitee controls.
    public async Task PendingInvitationAlone_CompletesInviteItem()
    {
        var world = await NewOrganizationAsync("Setup3b");
        var invitations = new InvitationService(world.Context, world.UserManager, world.UserManager.KeyNormalizer, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var admin = AsCurrentUser(world);

        var result = await invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        Assert.True(result.Succeeded);
        var status = await GetStatusAsync(world);
        Assert.True(status.HasSentInvitationOrMember);
        Assert.True(status.InviteComplete);
    }

    [Fact] // A deactivated second member, with no invitation ever sent, still does not count.
    public async Task DeactivatedSecondMemberWithNoInvitationSent_DoesNotCompleteInviteItem()
    {
        var world = await NewOrganizationAsync("Setup3c");
        var secondUserId = await TicketTestData.AddUserAsync(world.Context);
        var secondUser = await world.Context.Users.SingleAsync(u => u.Id == secondUserId);
        secondUser.IsActive = false;
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, secondUserId, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();

        var status = await GetStatusAsync(world);

        Assert.False(status.HasSentInvitationOrMember);
        Assert.False(status.InviteComplete);
    }

    [Fact]
    public async Task OrganizationWithProject_ProjectItemIsComplete()
    {
        var world = await NewOrganizationAsync("Setup3d");
        world.Context.Projects.Add(new Project(0, world.OrganizationId, "A Project", Now));
        await world.Context.SaveChangesAsync();

        var status = await GetStatusAsync(world);

        Assert.True(status.HasProject);
        Assert.True(status.ProjectComplete);
    }

    [Fact]
    public async Task SkippedInviteStep_CompletesInviteItemWithoutASentInvitation()
    {
        var world = await NewOrganizationAsync("Setup3e");
        var service = new WorkspaceSetupService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var admin = AsCurrentUser(world);

        await service.SkipInviteStepAsync(admin);
        var status = await service.GetWorkspaceSetupStatusAsync(admin);

        Assert.False(status.HasSentInvitationOrMember);
        Assert.True(status.InviteStepSkipped);
        Assert.True(status.InviteComplete);
    }

    [Fact]
    public async Task SkippedProjectStep_CompletesProjectItemWithoutAProject()
    {
        var world = await NewOrganizationAsync("Setup3f");
        var service = new WorkspaceSetupService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var admin = AsCurrentUser(world);

        await service.SkipProjectStepAsync(admin);
        var status = await service.GetWorkspaceSetupStatusAsync(admin);

        Assert.False(status.HasProject);
        Assert.True(status.ProjectStepSkipped);
        Assert.True(status.ProjectComplete);
    }

    [Fact]
    public async Task NonAdmin_CannotSkipASetupStep()
    {
        var world = await NewOrganizationAsync("Setup3g");
        var service = new WorkspaceSetupService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var viewer = new CurrentUser(world.AdminId, world.OrganizationId, UserRole.Viewer, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() => service.SkipInviteStepAsync(viewer));
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
        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
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
        world.Context.Projects.Add(new Project(0, world.OrganizationId, "A Project", Now));
        await world.Context.SaveChangesAsync();

        var secondUserId = await TicketTestData.AddUserAsync(world.Context);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, secondUserId, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();

        var admin = AsCurrentUser(world);
        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
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
        Assert.False(statusA.HasSentInvitationOrMember);
        Assert.True(statusB.HasTeam);
        Assert.True(statusB.HasSentInvitationOrMember);
    }

    private static async Task<WorkspaceSetupStatus> GetStatusAsync(World world)
    {
        var admin = AsCurrentUser(world);
        var service = new WorkspaceSetupService(world.Context, TimeProvider.System);
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
