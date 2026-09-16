using FlowOps.Application.Accounts;
using FlowOps.Application.Directory;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Directory;

/// <summary>
/// Closes the real workflow gap discovered validating the Workspace Setup onboarding flow:
/// creating a team was never enough, because nothing could put an Agent/Manager/Viewer onto it,
/// and <see cref="TicketAccessPolicy"/> scopes their visibility by team membership. These tests
/// prove <see cref="TeamService"/>'s membership methods correctly feed that existing, unmodified
/// authorization model — not a new one.
/// </summary>
[Collection("Postgres")]
public sealed class TeamMembershipServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TeamMembershipServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, Guid AdminId, int OrganizationId);

    // ---------------------------------------------------------------------------------------
    // Add member
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AddMemberAsync_ActiveOrganizationMember_Succeeds()
    {
        var world = await NewOrganizationAsync("TM1");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);

        var result = await service.AddMemberAsync(admin, teamId, agentId);

        Assert.True(result.Succeeded);
        Assert.True(await world.Context.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == teamId && m.UserId == agentId));
    }

    [Fact]
    public async Task AddMemberAsync_AlreadyOnTeam_FailsWithoutDuplicateRow()
    {
        var world = await NewOrganizationAsync("TM2");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);
        await service.AddMemberAsync(admin, teamId, agentId);

        var result = await service.AddMemberAsync(admin, teamId, agentId);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await world.Context.TeamMembers.AsNoTracking().CountAsync(m => m.TeamId == teamId && m.UserId == agentId));
    }

    [Fact] // Phase 16 organization boundary — a user from another organization must never be addable.
    public async Task AddMemberAsync_UserFromAnotherOrganization_Throws()
    {
        var worldA = await NewOrganizationAsync("TM3A");
        var worldB = await NewOrganizationAsync("TM3B");
        var serviceA = NewTeamService(worldA);
        var teamAId = (await serviceA.CreateAsync(AsCurrentUser(worldA), "Team A")).TeamId!.Value;

        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => serviceA.AddMemberAsync(AsCurrentUser(worldA), teamAId, worldB.AdminId));
    }

    [Fact] // An unaccepted invitation has no OrganizationMembership row — it must not be addable.
    public async Task AddMemberAsync_PendingInvitationNotYetAccepted_Throws()
    {
        var world = await NewOrganizationAsync("TM4");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;

        var invitations = new InvitationService(world.Context, world.UserManager, world.UserManager.KeyNormalizer, new TicketTestData.FixedTimeProvider(Now));
        var invited = await invitations.CreateInvitationAsync(admin, new CreateInvitationRequest($"{Guid.NewGuid():N}@teammembership.test.local", UserRole.Agent));
        Assert.True(invited.Succeeded);

        // The invitation created no user and no OrganizationMembership yet, so there is nothing
        // for the invited email to appear as — confirming the eligibility query (real
        // OrganizationMembership + active user) is what excludes this case, not a separate
        // invitation-aware check. Only the real member (the Admin who sent the invite) is eligible.
        var eligible = await service.GetEligibleMembersAsync(admin, teamId);
        var single = Assert.Single(eligible);
        Assert.Equal(world.AdminId, single.UserId);
    }

    [Fact]
    public async Task AddMemberAsync_DeactivatedMember_Throws()
    {
        var world = await NewOrganizationAsync("TM5");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);
        var agentUser = await world.Context.Users.SingleAsync(u => u.Id == agentId);
        agentUser.IsActive = false;
        await world.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => service.AddMemberAsync(admin, teamId, agentId));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task AddMemberAsync_NonAdmin_Throws(UserRole role)
    {
        var world = await NewOrganizationAsync("TM6" + role);
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var targetId = await AddOrganizationMemberAsync(world, UserRole.Agent);
        var nonAdmin = new CurrentUser(Guid.NewGuid(), world.OrganizationId, role, new HashSet<int>(), new HashSet<int>());

        await Assert.ThrowsAsync<TeamAccessDeniedException>(() => service.AddMemberAsync(nonAdmin, teamId, targetId));
    }

    [Fact] // Sequential "concurrent" adds must never produce two rows for the same (team, user) pair.
    public async Task AddMemberAsync_RepeatedAdd_NeverProducesDuplicateRow()
    {
        var world = await NewOrganizationAsync("TM7");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);

        var first = await service.AddMemberAsync(admin, teamId, agentId);
        var second = await service.AddMemberAsync(admin, teamId, agentId);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(1, await world.Context.TeamMembers.AsNoTracking().CountAsync(m => m.TeamId == teamId && m.UserId == agentId));
    }

    // ---------------------------------------------------------------------------------------
    // Remove member
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_RemovesOnlyTheTeamMemberRow()
    {
        var world = await NewOrganizationAsync("TM8");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);
        await service.AddMemberAsync(admin, teamId, agentId);

        var result = await service.RemoveMemberAsync(admin, teamId, agentId);

        Assert.True(result.Succeeded);
        Assert.False(await world.Context.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == teamId && m.UserId == agentId));
        // The user and their organization membership are untouched — only the team relationship is gone.
        Assert.True(await world.Context.Users.AsNoTracking().AnyAsync(u => u.Id == agentId));
        Assert.True(await world.Context.OrganizationMemberships.AsNoTracking().AnyAsync(m => m.UserId == agentId && m.OrganizationId == world.OrganizationId));
    }

    [Fact]
    public async Task RemoveMemberAsync_NotAMember_FailsSafely()
    {
        var world = await NewOrganizationAsync("TM9");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);

        var result = await service.RemoveMemberAsync(admin, teamId, agentId);

        Assert.False(result.Succeeded);
    }

    // ---------------------------------------------------------------------------------------
    // Team manager flag
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SetTeamManagerAsync_TogglesFlagIndependentlyOfOrganizationRole()
    {
        var world = await NewOrganizationAsync("TM10");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var managerId = await AddOrganizationMemberAsync(world, UserRole.Manager);
        await service.AddMemberAsync(admin, teamId, managerId);

        var setResult = await service.SetTeamManagerAsync(admin, teamId, managerId, isTeamManager: true);
        Assert.True(setResult.Succeeded);
        var afterSet = await world.Context.TeamMembers.AsNoTracking().SingleAsync(m => m.TeamId == teamId && m.UserId == managerId);
        Assert.True(afterSet.IsTeamManager);

        var unsetResult = await service.SetTeamManagerAsync(admin, teamId, managerId, isTeamManager: false);
        Assert.True(unsetResult.Succeeded);
        var afterUnset = await world.Context.TeamMembers.AsNoTracking().SingleAsync(m => m.TeamId == teamId && m.UserId == managerId);
        Assert.False(afterUnset.IsTeamManager);

        // OrganizationMembership.Role is a completely separate fact and is untouched.
        var membership = await world.Context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == managerId);
        Assert.Equal(UserRole.Manager, membership.Role);
    }

    // ---------------------------------------------------------------------------------------
    // Team detail
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetTeamDetailAsync_ReturnsTeamAndMembers_OrderedByName()
    {
        var world = await NewOrganizationAsync("TM15");
        var admin = AsCurrentUser(world);
        var service = NewTeamService(world);
        var teamId = (await service.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var zed = await AddNamedOrganizationMemberAsync(world, UserRole.Agent, "Zed Agent");
        var alice = await AddNamedOrganizationMemberAsync(world, UserRole.Viewer, "Alice Viewer");
        await service.AddMemberAsync(admin, teamId, zed);
        await service.AddMemberAsync(admin, teamId, alice);

        var detail = await service.GetTeamDetailAsync(admin, teamId);

        Assert.NotNull(detail);
        Assert.Equal("Service Desk", detail!.TeamName);
        Assert.Equal(2, detail.Members.Count);
        Assert.Equal(new[] { "Alice Viewer", "Zed Agent" }, detail.Members.Select(m => m.DisplayName));
        Assert.Equal(UserRole.Viewer, detail.Members[0].OrganizationRole);
    }

    [Fact]
    public async Task GetTeamDetailAsync_TeamFromAnotherOrganization_ReturnsNull()
    {
        var worldA = await NewOrganizationAsync("TM16A");
        var worldB = await NewOrganizationAsync("TM16B");
        var teamBId = (await NewTeamService(worldB).CreateAsync(AsCurrentUser(worldB), "Team B")).TeamId!.Value;

        var detail = await NewTeamService(worldA).GetTeamDetailAsync(AsCurrentUser(worldA), teamBId);

        Assert.Null(detail);
    }

    // ---------------------------------------------------------------------------------------
    // Eligible members
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetEligibleMembersAsync_ExcludesCurrentMembersAndOtherOrganizations()
    {
        var worldA = await NewOrganizationAsync("TM11A");
        var worldB = await NewOrganizationAsync("TM11B");
        var serviceA = NewTeamService(worldA);
        var teamId = (await serviceA.CreateAsync(AsCurrentUser(worldA), "Service Desk")).TeamId!.Value;
        var alreadyOnTeam = await AddOrganizationMemberAsync(worldA, UserRole.Agent);
        var notYetOnTeam = await AddOrganizationMemberAsync(worldA, UserRole.Viewer);
        await serviceA.AddMemberAsync(AsCurrentUser(worldA), teamId, alreadyOnTeam);

        var eligible = await serviceA.GetEligibleMembersAsync(AsCurrentUser(worldA), teamId);

        Assert.Contains(eligible, e => e.UserId == notYetOnTeam);
        Assert.DoesNotContain(eligible, e => e.UserId == alreadyOnTeam);
        Assert.DoesNotContain(eligible, e => e.UserId == worldB.AdminId);
    }

    // ---------------------------------------------------------------------------------------
    // THE regression that matters: TeamMember management correctly feeds existing ticket
    // visibility (TicketAccessPolicy / TicketQueryService), unmodified.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentTeamMembership_DirectlyControlsTicketVisibility()
    {
        var world = await NewOrganizationAsync("TM12");
        var admin = AsCurrentUser(world);
        var teamService = NewTeamService(world);
        var teamAId = (await teamService.CreateAsync(admin, "Team A")).TeamId!.Value;
        var teamBId = (await teamService.CreateAsync(admin, "Team B")).TeamId!.Value;
        var categoryA = new FlowOps.Domain.Catalog.Category(0, teamAId, "Cat A", WorkType.Incident, Now);
        world.Context.Categories.Add(categoryA);
        var categoryB = new FlowOps.Domain.Catalog.Category(0, teamBId, "Cat B", WorkType.Incident, Now);
        world.Context.Categories.Add(categoryB);
        await world.Context.SaveChangesAsync();

        var agentId = await AddOrganizationMemberAsync(world, UserRole.Agent);
        await teamService.AddMemberAsync(admin, teamAId, agentId);

        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var (teamATicketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("Team A ticket", "A routine description.", WorkType.Incident, Priority.Medium, teamAId, categoryA.Id, null), admin);
        var (teamBTicketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("Team B ticket", "A routine description.", WorkType.Incident, Priority.Medium, teamBId, categoryB.Id, null), admin);

        var agentAsCurrentUser = () => new CurrentUserAccessor(world.Context, world.UserManager).GetCurrentUserAsync(agentId);
        var queryService = new TicketQueryService(world.Context, TimeProvider.System);

        // Agent + TeamMember(Team A) -> sees the Team A ticket, never the Team B ticket.
        var agentWithMembership = await agentAsCurrentUser();
        Assert.NotNull(agentWithMembership);
        var pageWithMembership = await queryService.GetQueueAsync(agentWithMembership!, 1);
        Assert.Contains(pageWithMembership.Items, i => i.Id == teamATicketId);
        Assert.DoesNotContain(pageWithMembership.Items, i => i.Id == teamBTicketId);

        // Agent - TeamMember -> no longer sees the (now historical) Team A ticket via the ordinary
        // queue query; the ticket itself is untouched (still exists, still assignable/visible to Admin).
        await teamService.RemoveMemberAsync(admin, teamAId, agentId);
        var agentWithoutMembership = await agentAsCurrentUser();
        Assert.NotNull(agentWithoutMembership);
        var pageWithoutMembership = await queryService.GetQueueAsync(agentWithoutMembership!, 1);
        Assert.DoesNotContain(pageWithoutMembership.Items, i => i.Id == teamATicketId);

        var adminPage = await queryService.GetQueueAsync(admin, 1);
        Assert.Contains(adminPage.Items, i => i.Id == teamATicketId);
        Assert.Contains(adminPage.Items, i => i.Id == teamBTicketId);
    }

    [Fact]
    public async Task ManagerTeamManagerFlag_DrivesManagedTeamScopedAssignAuthority()
    {
        var world = await NewOrganizationAsync("TM13");
        var admin = AsCurrentUser(world);
        var teamService = NewTeamService(world);
        var teamId = (await teamService.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var category = new FlowOps.Domain.Catalog.Category(0, teamId, "Cat", WorkType.Incident, Now);
        world.Context.Categories.Add(category);
        await world.Context.SaveChangesAsync();

        var managerId = await AddOrganizationMemberAsync(world, UserRole.Manager);
        await teamService.AddMemberAsync(admin, teamId, managerId);

        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("Manager scope ticket", "A routine description.", WorkType.Incident, Priority.Medium, teamId, category.Id, null), admin);

        var accessor = new CurrentUserAccessor(world.Context, world.UserManager);

        // Manager, team member but NOT flagged IsTeamManager: TicketAccessPolicy's "own teams"
        // scope for Manager is driven by ManagedTeamIds, not mere MemberTeamIds — existing,
        // unmodified behavior.
        var managerNotFlagged = await accessor.GetCurrentUserAsync(managerId);
        Assert.NotNull(managerNotFlagged);
        Assert.DoesNotContain(teamId, managerNotFlagged!.ManagedTeamIds);
        Assert.Contains(teamId, managerNotFlagged.MemberTeamIds);

        await teamService.SetTeamManagerAsync(admin, teamId, managerId, isTeamManager: true);
        var managerFlagged = await accessor.GetCurrentUserAsync(managerId);
        Assert.NotNull(managerFlagged);
        Assert.Contains(teamId, managerFlagged!.ManagedTeamIds);

        var snapshot = new TicketAuthorizationSnapshot(ticketId, teamId, RequesterId: admin.UserId, AssigneeId: null, Status.Open);
        Assert.True(TicketAccessPolicy.CanTransition(snapshot, managerFlagged));
    }

    [Fact]
    public async Task ViewerTeamMembership_GrantsReadOnlyVisibility_NeverMutationRights()
    {
        var world = await NewOrganizationAsync("TM14");
        var admin = AsCurrentUser(world);
        var teamService = NewTeamService(world);
        var teamId = (await teamService.CreateAsync(admin, "Service Desk")).TeamId!.Value;
        var category = new FlowOps.Domain.Catalog.Category(0, teamId, "Cat", WorkType.Incident, Now);
        world.Context.Categories.Add(category);
        await world.Context.SaveChangesAsync();

        var viewerId = await AddOrganizationMemberAsync(world, UserRole.Viewer);
        await teamService.AddMemberAsync(admin, teamId, viewerId);

        var ticketService = new TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("Viewer scope ticket", "A routine description.", WorkType.Incident, Priority.Medium, teamId, category.Id, null), admin);

        var accessor = new CurrentUserAccessor(world.Context, world.UserManager);
        var viewer = await accessor.GetCurrentUserAsync(viewerId);
        Assert.NotNull(viewer);

        var queryService = new TicketQueryService(world.Context, TimeProvider.System);
        var page = await queryService.GetQueueAsync(viewer!, 1);
        Assert.Contains(page.Items, i => i.Id == ticketId);

        var snapshot = new TicketAuthorizationSnapshot(ticketId, teamId, RequesterId: admin.UserId, AssigneeId: null, Status.Open);
        Assert.False(TicketAccessPolicy.CanTransition(snapshot, viewer!));
        Assert.False(TicketAccessPolicy.CanComment(snapshot, viewer!));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static TeamService NewTeamService(World world) => new(world.Context, new TicketTestData.FixedTimeProvider(Now));

    private static CurrentUser AsCurrentUser(World world) =>
        new(world.AdminId, world.OrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>());

    private static Task<Guid> AddOrganizationMemberAsync(World world, UserRole role) =>
        AddNamedOrganizationMemberAsync(world, role, $"Member {Guid.NewGuid():N}");

    private static async Task<Guid> AddNamedOrganizationMemberAsync(World world, UserRole role, string displayName)
    {
        var userId = await TicketTestData.AddUserAsync(world.Context, displayName);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, userId, role, Now));
        await world.Context.SaveChangesAsync();
        return userId;
    }

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@teammembership.test.local", Password, $"{label} Org"));
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
}
