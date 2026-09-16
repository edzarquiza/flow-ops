using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
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

namespace FlowOps.Application.Tests.Organizations;

[Collection("Postgres")]
public sealed class MembershipServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public MembershipServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, MembershipService Memberships, Guid AdminId, int OrganizationId);

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", UniqueEmail(), Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        // Phase 24A (ADR-0024): self-registration alone leaves the founder Pending, which would
        // stop them counting as "another Admin" for SoleAdminGuard — approved immediately, since
        // this fixture exists to exercise membership management, not the registration-approval gate.
        var founder = await context.Users.SingleAsync(u => u.Id == registration.UserId!.Value);
        founder.RegistrationApprovedAt = Now;
        await context.SaveChangesAsync();

        return new World(context, userManager, new MembershipService(context), registration.UserId!.Value, registration.OrganizationId!.Value);
    }

    private static async Task<Guid> AddMemberAsync(World world, UserRole role) =>
        await AddMemberAsync(world, role, "Phase 5 Test User");

    private static async Task<Guid> AddMemberAsync(World world, UserRole role, string displayName)
    {
        var userId = await TicketTestData.AddUserAsync(world.Context, displayName);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, userId, role, Now));
        await world.Context.SaveChangesAsync();
        return userId;
    }

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@members.test.local";

    private static CurrentUser AsCurrentUser(Guid userId, int organizationId, UserRole role) =>
        new(userId, organizationId, role, new HashSet<int>(), new HashSet<int>());

    // ---------------------------------------------------------------------------------------
    // Listing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMembersAsync_Admin_SeesAllMembers()
    {
        var world = await NewOrganizationAsync("List1");
        await AddMemberAsync(world, UserRole.Agent);
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var members = await world.Memberships.GetMembersAsync(admin);

        Assert.Equal(2, members.Count);
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task GetMembersAsync_UnauthorizedRole_IsDenied(UserRole role)
    {
        var world = await NewOrganizationAsync("List2" + role);
        var actor = AsCurrentUser(world.AdminId, world.OrganizationId, role);

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() => world.Memberships.GetMembersAsync(actor));
    }

    [Fact] // Multi-tenant: Org A never sees Org B's members.
    public async Task GetMembersAsync_NeverIncludesAnotherOrganizationsMembers()
    {
        var orgA = await NewOrganizationAsync("CrossA");
        var orgB = await NewOrganizationAsync("CrossB");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        var members = await orgA.Memberships.GetMembersAsync(adminA);

        Assert.DoesNotContain(members, m => m.UserId == orgB.AdminId);
    }

    // ---------------------------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMembersAsync_SearchByName_ReturnsOnlyMatchingMember()
    {
        var world = await NewOrganizationAsync("SearchName1");
        await AddMemberAsync(world, UserRole.Agent, "Casey Nguyen");
        await AddMemberAsync(world, UserRole.Agent, "Jordan Ellis");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var members = await world.Memberships.GetMembersAsync(admin, search: "casey");

        var member = Assert.Single(members);
        Assert.Equal("Casey Nguyen", member.DisplayName);
    }

    [Fact]
    public async Task GetMembersAsync_SearchByName_IsCaseInsensitiveAndPartial()
    {
        var world = await NewOrganizationAsync("SearchName2");
        await AddMemberAsync(world, UserRole.Agent, "Casey Nguyen");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var members = await world.Memberships.GetMembersAsync(admin, search: "NGUYEN");

        var member = Assert.Single(members);
        Assert.Equal("Casey Nguyen", member.DisplayName);
    }

    [Fact]
    public async Task GetMembersAsync_SearchByEmail_ReturnsOnlyMatchingMember()
    {
        var world = await NewOrganizationAsync("SearchEmail1");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var expected = await world.Memberships.GetMembersAsync(admin);
        var adminMember = expected.Single(m => m.UserId == world.AdminId);
        await AddMemberAsync(world, UserRole.Agent, "Someone Else");

        var members = await world.Memberships.GetMembersAsync(admin, search: adminMember.Email);

        var member = Assert.Single(members);
        Assert.Equal(world.AdminId, member.UserId);
    }

    [Fact] // An empty/whitespace search reproduces the exact pre-search member list.
    public async Task GetMembersAsync_EmptyOrWhitespaceSearch_ReturnsExistingMembers()
    {
        var world = await NewOrganizationAsync("SearchEmpty1");
        await AddMemberAsync(world, UserRole.Agent, "Casey Nguyen");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var withoutSearch = await world.Memberships.GetMembersAsync(admin);
        var withEmptySearch = await world.Memberships.GetMembersAsync(admin, search: "");
        var withWhitespaceSearch = await world.Memberships.GetMembersAsync(admin, search: "   ");

        Assert.Equal(withoutSearch.Select(m => m.UserId), withEmptySearch.Select(m => m.UserId));
        Assert.Equal(withoutSearch.Select(m => m.UserId), withWhitespaceSearch.Select(m => m.UserId));
    }

    [Fact]
    public async Task GetMembersAsync_SearchWithNoMatches_ReturnsEmpty()
    {
        var world = await NewOrganizationAsync("SearchNoMatch1");
        await AddMemberAsync(world, UserRole.Agent, "Casey Nguyen");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var members = await world.Memberships.GetMembersAsync(admin, search: $"no-such-name-{Guid.NewGuid():N}");

        Assert.Empty(members);
    }

    [Fact] // Multi-tenant: search never crosses the organization boundary.
    public async Task GetMembersAsync_SearchNeverIncludesAnotherOrganizationsMembers()
    {
        var orgA = await NewOrganizationAsync("SearchCrossA");
        var orgB = await NewOrganizationAsync("SearchCrossB");
        await AddMemberAsync(orgB, UserRole.Agent, "Alex Cross-Org Target");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        var members = await orgA.Memberships.GetMembersAsync(adminA, search: "Cross-Org");

        Assert.Empty(members);
    }

    // ---------------------------------------------------------------------------------------
    // Role change
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeRoleAsync_Admin_CanPromoteAgentToManager()
    {
        var world = await NewOrganizationAsync("Role1");
        var agentId = await AddMemberAsync(world, UserRole.Agent);
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Memberships.ChangeRoleAsync(admin, agentId, UserRole.Manager);

        Assert.True(result.Succeeded);
        var membership = await world.Context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == agentId);
        Assert.Equal(UserRole.Manager, membership.Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_ManagerTargetingAdmin_IsDenied()
    {
        var world = await NewOrganizationAsync("Role2");
        var managerId = await AddMemberAsync(world, UserRole.Manager);
        var manager = AsCurrentUser(managerId, world.OrganizationId, UserRole.Manager);

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() =>
            world.Memberships.ChangeRoleAsync(manager, world.AdminId, UserRole.Manager));
    }

    [Fact] // CRITICAL: cannot demote the organization's only Admin.
    public async Task ChangeRoleAsync_SoleAdmin_CannotBeDemoted()
    {
        var world = await NewOrganizationAsync("Role3");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Memberships.ChangeRoleAsync(admin, world.AdminId, UserRole.Manager);

        Assert.False(result.Succeeded);
        var membership = await world.Context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == world.AdminId);
        Assert.Equal(UserRole.Admin, membership.Role); // unchanged
    }

    [Fact] // Alice may demote Bob because Alice remains as Admin.
    public async Task ChangeRoleAsync_AnotherAdminExists_DemotionSucceeds()
    {
        var world = await NewOrganizationAsync("Role4");
        var secondAdminId = await AddMemberAsync(world, UserRole.Admin);
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Memberships.ChangeRoleAsync(admin, secondAdminId, UserRole.Agent);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ChangeRoleAsync_TargetNotInOrganization_Fails()
    {
        var orgA = await NewOrganizationAsync("Role5A");
        var orgB = await NewOrganizationAsync("Role5B");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        var result = await orgA.Memberships.ChangeRoleAsync(adminA, orgB.AdminId, UserRole.Agent);

        Assert.False(result.Succeeded);
        var orgBMembership = await orgB.Context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == orgB.AdminId);
        Assert.Equal(UserRole.Admin, orgBMembership.Role); // untouched
    }

    // ---------------------------------------------------------------------------------------
    // Removal
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveMemberAsync_RemovesMembership_ButNotTheUser()
    {
        var world = await NewOrganizationAsync("Remove1");
        var agentId = await AddMemberAsync(world, UserRole.Agent);
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Memberships.RemoveMemberAsync(admin, agentId);

        Assert.True(result.Succeeded);
        Assert.False(await world.Context.OrganizationMemberships.AnyAsync(m => m.UserId == agentId));
        Assert.True(await world.Context.Users.AnyAsync(u => u.Id == agentId)); // user preserved
    }

    [Fact] // Step 18 example: removing from Org A must not touch the same user's Org B membership.
    public async Task RemoveMemberAsync_OtherOrganizationMembershipUnaffected()
    {
        var orgA = await NewOrganizationAsync("Remove2A");
        var orgB = await NewOrganizationAsync("Remove2B");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        // The same physical user is a member of both organizations.
        orgA.Context.OrganizationMemberships.Add(new OrganizationMembership(0, orgA.OrganizationId, orgB.AdminId, UserRole.Agent, Now));
        await orgA.Context.SaveChangesAsync();

        var result = await orgA.Memberships.RemoveMemberAsync(adminA, orgB.AdminId);

        Assert.True(result.Succeeded);
        Assert.False(await orgA.Context.OrganizationMemberships.AnyAsync(m => m.OrganizationId == orgA.OrganizationId && m.UserId == orgB.AdminId));
        Assert.True(await orgB.Context.OrganizationMemberships.AnyAsync(m => m.OrganizationId == orgB.OrganizationId && m.UserId == orgB.AdminId)); // untouched
    }

    [Fact] // CRITICAL: cannot remove the organization's only Admin.
    public async Task RemoveMemberAsync_SoleAdmin_IsBlocked()
    {
        var world = await NewOrganizationAsync("Remove3");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Memberships.RemoveMemberAsync(admin, world.AdminId);

        Assert.False(result.Succeeded);
        Assert.True(await world.Context.OrganizationMemberships.AnyAsync(m => m.UserId == world.AdminId));
    }

    [Fact]
    public async Task RemoveMemberAsync_ManagerRemovingAgent_Succeeds()
    {
        var world = await NewOrganizationAsync("Remove4");
        var managerId = await AddMemberAsync(world, UserRole.Manager);
        var agentId = await AddMemberAsync(world, UserRole.Agent);
        var manager = AsCurrentUser(managerId, world.OrganizationId, UserRole.Manager);

        var result = await world.Memberships.RemoveMemberAsync(manager, agentId);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RemoveMemberAsync_ManagerRemovingAnotherManager_IsDenied()
    {
        var world = await NewOrganizationAsync("Remove5");
        var managerId = await AddMemberAsync(world, UserRole.Manager);
        var otherManagerId = await AddMemberAsync(world, UserRole.Manager);
        var manager = AsCurrentUser(managerId, world.OrganizationId, UserRole.Manager);

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() => world.Memberships.RemoveMemberAsync(manager, otherManagerId));
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovedMembershipNoLongerGrantsAccess()
    {
        var world = await NewOrganizationAsync("Remove6");
        var agentId = await AddMemberAsync(world, UserRole.Agent);
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        await world.Memberships.RemoveMemberAsync(admin, agentId);

        var accessor = new CurrentUserAccessor(world.Context, world.UserManager);
        var resolved = await accessor.GetCurrentUserAsync(agentId);

        Assert.Null(resolved); // no membership anywhere -> CurrentUserAccessor resolves nothing
    }

    // ---------------------------------------------------------------------------------------
    // Demo protection
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeRoleAsync_DemoProtectedMember_IsRejected()
    {
        var world = await NewOrganizationAsync("Demo1");
        var demoUser = new ApplicationUser
        {
            UserName = UniqueEmail(),
            Email = UniqueEmail(),
            EmailConfirmed = true,
            DisplayName = "Demo Persona",
            IsActive = true,
            IsDemoProtected = true,
        };
        await world.UserManager.CreateAsync(demoUser, Password);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, demoUser.Id, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => world.Memberships.ChangeRoleAsync(admin, demoUser.Id, UserRole.Manager));
    }

    [Fact]
    public async Task RemoveMemberAsync_DemoProtectedMember_IsRejected()
    {
        var world = await NewOrganizationAsync("Demo2");
        var demoUser = new ApplicationUser
        {
            UserName = UniqueEmail(),
            Email = UniqueEmail(),
            EmailConfirmed = true,
            DisplayName = "Demo Persona",
            IsActive = true,
            IsDemoProtected = true,
        };
        await world.UserManager.CreateAsync(demoUser, Password);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, demoUser.Id, UserRole.Agent, Now));
        await world.Context.SaveChangesAsync();
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => world.Memberships.RemoveMemberAsync(admin, demoUser.Id));
    }
}
