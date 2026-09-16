using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
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

namespace FlowOps.Application.Tests.Accounts;

/// <summary>
/// Phase 17: registration, profile/account changes, and account deletion (including the sole-admin
/// safety rule) against real PostgreSQL — the same "Postgres" collection/container every other
/// Application.Tests class shares.
/// </summary>
[Collection("Postgres")]
public sealed class AccountServiceTests
{
    private const string Password = "Correct-Horse-Battery-Staple9!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public AccountServiceTests(PostgresFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_ValidRequest_CreatesUserOrganizationAndAdminMembership()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RegisterAsync(new RegisterRequest("Alice Example", UniqueEmail(), Password, "Alice's Org"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.UserId);
        Assert.NotNull(result.OrganizationId);

        await using var verify = _fixture.CreateContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == result.UserId);
        Assert.Equal("Alice Example", user.DisplayName);
        Assert.True(user.IsActive);

        var organization = await verify.Organizations.AsNoTracking().SingleAsync(o => o.Id == result.OrganizationId);
        Assert.Equal("Alice's Org", organization.Name);

        var membership = await verify.OrganizationMemberships.AsNoTracking()
            .SingleAsync(m => m.UserId == result.UserId);
        Assert.Equal(result.OrganizationId, membership.OrganizationId);
        Assert.Equal(UserRole.Admin, membership.Role);
    }

    [Fact] // Structural: RegisterRequest carries no OrganizationId at all — nothing for a caller to select.
    public async Task RegisterAsync_TwoRegistrations_EachGetsItsOwnDistinctOrganization()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var first = await service.RegisterAsync(new RegisterRequest("Alice", UniqueEmail(), Password, "Acme Support"));
        var second = await service.RegisterAsync(new RegisterRequest("Bob", UniqueEmail(), Password, "Acme Support"));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.OrganizationId, second.OrganizationId); // same name, genuinely different orgs
    }

    [Fact] // Structural: RegisterRequest carries no Role field — Admin is the only possible outcome.
    public async Task RegisterAsync_AlwaysAssignsAdminRole_RegardlessOfInput()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RegisterAsync(new RegisterRequest("Carol", UniqueEmail(), Password, "Carol's Org"));

        var membership = await context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == result.UserId);
        Assert.Equal(UserRole.Admin, membership.Role);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateEmail_IsRejectedAndPersistsNoNewOrganization()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var email = UniqueEmail();
        var first = await service.RegisterAsync(new RegisterRequest("Dana", email, Password, "Dana Org 1"));
        Assert.True(first.Succeeded);

        var organizationCountBefore = await context.Organizations.CountAsync();

        var second = await service.RegisterAsync(new RegisterRequest("Dana Again", email, Password, "Dana Org 2"));

        Assert.False(second.Succeeded);
        Assert.NotEmpty(second.Errors);
        Assert.Equal(organizationCountBefore, await context.Organizations.CountAsync()); // no orphan org
    }

    [Fact]
    public async Task RegisterAsync_WeakPassword_IsRejectedByExistingIdentityPolicy()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RegisterAsync(new RegisterRequest("Erin", UniqueEmail(), "short", "Erin Org"));

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task RegisterAsync_BlankOrganizationName_IsRejectedAndCreatesNoUser()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var email = UniqueEmail();
        var result = await service.RegisterAsync(new RegisterRequest("Frank", email, Password, "   "));

        Assert.False(result.Succeeded);
        Assert.False(await context.Users.AnyAsync(u => u.Email == email)); // no orphan user either
    }

    // ---------------------------------------------------------------------------------------
    // Multi-tenant registration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_NewUser_StartsWithExactlyOneMembership_AndCurrentOrganizationResolvesCorrectly()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RegisterAsync(new RegisterRequest("Grace", UniqueEmail(), Password, "Grace Org"));

        var memberships = await context.OrganizationMemberships.AsNoTracking().Where(m => m.UserId == result.UserId).ToListAsync();
        Assert.Single(memberships);

        var accessor = new CurrentUserAccessor(context, userManager);
        var currentUser = await accessor.GetCurrentUserAsync(result.UserId!.Value);

        Assert.NotNull(currentUser);
        Assert.Equal(result.OrganizationId, currentUser.OrganizationId);
        Assert.Equal(UserRole.Admin, currentUser.Role);
    }

    [Fact] // The real cross-tenant guarantee: a brand-new organization's queries never surface a
           // ticket seeded under a completely unrelated organization (here, the shared ambient
           // TicketTestData organization used by every other Application.Tests test).
    public async Task NewOrganization_CannotSeeTicketsFromAnUnrelatedOrganization()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var accountService = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var otherTeamId = await TicketTestData.AddTeamAsync(context); // unrelated organization's team
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);
        var otherOrganizationId = await TicketTestData.GetOrganizationIdAsync(context);
        var otherRequester = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, otherTeamId);
        var ticketService = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));
        await ticketService.CreateAsync(
            new CreateTicketRequest("Unrelated org's ticket", "Should never be visible to the new org.", WorkType.Incident, Priority.Medium, otherTeamId, otherCategoryId, null),
            otherRequester);

        var registration = await accountService.RegisterAsync(new RegisterRequest("Henry", UniqueEmail(), Password, "Henry Org"));
        var accessor = new CurrentUserAccessor(context, userManager);
        var newOrgAdmin = await accessor.GetCurrentUserAsync(registration.UserId!.Value);

        var queryService = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await queryService.GetQueueAsync(newOrgAdmin!, pageNumber: 1);

        Assert.Empty(page.Items);
        Assert.NotEqual(otherOrganizationId, newOrgAdmin!.OrganizationId);
    }

    // ---------------------------------------------------------------------------------------
    // Profile
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateDisplayNameAsync_ChangesOwnName()
    {
        var (context, userManager, service, userId, _) = await RegisteredUserAsync();

        var result = await service.UpdateDisplayNameAsync(userId, "New Name");

        Assert.True(result.Succeeded);
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal("New Name", user.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_Blank_IsRejected()
    {
        var (_, _, service, userId, _) = await RegisteredUserAsync();

        var result = await service.UpdateDisplayNameAsync(userId, "   ");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_NeverAffectsAnotherUser()
    {
        var (context, userManager, service, userId, _) = await RegisteredUserAsync();
        var otherRegistration = await service.RegisterAsync(new RegisterRequest("Other Original", UniqueEmail(), Password, "Other Org"));

        await service.UpdateDisplayNameAsync(userId, "Changed");

        var other = await context.Users.AsNoTracking().SingleAsync(u => u.Id == otherRegistration.UserId);
        Assert.Equal("Other Original", other.DisplayName);
    }

    [Fact]
    public async Task ChangeEmailAsync_ChangesEmailAndUserName()
    {
        var (context, _, service, userId, _) = await RegisteredUserAsync();
        var newEmail = UniqueEmail();

        var result = await service.ChangeEmailAsync(userId, newEmail);

        Assert.True(result.Succeeded);
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(newEmail, user.Email);
        Assert.Equal(newEmail, user.UserName); // login resolves by UserName — must stay in sync
        Assert.True(user.EmailConfirmed);
    }

    [Fact]
    public async Task ChangeEmailAsync_Duplicate_IsRejected()
    {
        var (context, userManager, service, userId, _) = await RegisteredUserAsync();
        var takenEmail = UniqueEmail();
        await service.RegisterAsync(new RegisterRequest("Someone Else", takenEmail, Password, "Someone Else Org"));

        var result = await service.ChangeEmailAsync(userId, takenEmail);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ChangePasswordAsync_CorrectCurrentPassword_Succeeds()
    {
        var (context, userManager, service, userId, _) = await RegisteredUserAsync();

        var result = await service.ChangePasswordAsync(userId, Password, "A-New-Passw0rd!2");

        Assert.True(result.Succeeded);
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.True(await userManager.CheckPasswordAsync(user!, "A-New-Passw0rd!2"));
    }

    [Fact]
    public async Task ChangePasswordAsync_IncorrectCurrentPassword_IsRejected()
    {
        var (context, userManager, service, userId, _) = await RegisteredUserAsync();

        var result = await service.ChangePasswordAsync(userId, "totally-wrong-password", "A-New-Passw0rd!2");

        Assert.False(result.Succeeded);
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.True(await userManager.CheckPasswordAsync(user!, Password)); // unchanged
    }

    // ---------------------------------------------------------------------------------------
    // Deletion — sole-admin safety rule
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAccountAsync_SoleAdminOfAnOrganization_IsBlocked()
    {
        var (context, userManager, service, userId, organizationId) = await RegisteredUserAsync();

        var result = await service.DeleteAccountAsync(userId);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.BlockedOrganizationNames);

        // Untouched: membership still exists, account still active.
        Assert.True(await context.OrganizationMemberships.AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId));
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.True(user!.IsActive);
    }

    [Fact]
    public async Task DeleteAccountAsync_AnotherAdminExists_Succeeds()
    {
        var (context, userManager, service, userId, organizationId) = await RegisteredUserAsync();
        var secondAdminId = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, organizationId, secondAdminId, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        var result = await service.DeleteAccountAsync(userId);

        Assert.True(result.Succeeded);
        Assert.False(await context.OrganizationMemberships.AnyAsync(m => m.UserId == userId));
        // The organization and its other Admin are completely untouched.
        Assert.True(await context.Organizations.AnyAsync(o => o.Id == organizationId));
        Assert.True(await context.OrganizationMemberships.AnyAsync(m => m.UserId == secondAdminId && m.OrganizationId == organizationId));
    }

    [Fact] // "A user with Admin membership in multiple organizations must be evaluated against ALL
           // organizations they belong to" — blocked if ANY of them would lose its last Admin.
    public async Task DeleteAccountAsync_AdminOfMultipleOrganizations_BlockedIfAnyWouldLoseItsLastAdmin()
    {
        var (context, userManager, service, userId, orgAId) = await RegisteredUserAsync();

        // A second organization where this same user is also the sole Admin.
        var orgB = new Organization(0, "Org B", Now);
        context.Organizations.Add(orgB);
        await context.SaveChangesAsync();
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.Id, userId, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        // A third organization where another Admin already exists.
        var orgC = new Organization(0, "Org C", Now);
        context.Organizations.Add(orgC);
        await context.SaveChangesAsync();
        var otherAdminId = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgC.Id, userId, UserRole.Admin, Now));
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgC.Id, otherAdminId, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        var result = await service.DeleteAccountAsync(userId);

        Assert.False(result.Succeeded);
        // Org A and Org B (both would lose their last Admin) are named; Org C is fine on its own
        // but the whole deletion is still blocked because at least one organization would break.
        Assert.Contains(result.BlockedOrganizationNames, n => n is "Org B");
    }

    [Fact]
    public async Task DeleteAccountAsync_AllOrganizationsHaveAnotherAdmin_Succeeds()
    {
        var (context, userManager, service, userId, orgAId) = await RegisteredUserAsync();
        var anotherAdminForA = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgAId, anotherAdminForA, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        var orgB = new Organization(0, "Org B With Backup", Now);
        context.Organizations.Add(orgB);
        await context.SaveChangesAsync();
        var anotherAdminForB = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.Id, userId, UserRole.Admin, Now));
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.Id, anotherAdminForB, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        var result = await service.DeleteAccountAsync(userId);

        Assert.True(result.Succeeded);
    }

    [Fact] // A non-Admin membership never triggers the safety rule, and removing it never touches
           // the organization it belonged to.
    public async Task DeleteAccountAsync_NonAdminMembership_NeverBlocksAndOrganizationUnaffected()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var registration = await service.RegisterAsync(new RegisterRequest("Owner", UniqueEmail(), Password, "Agent Org"));
        // Created via UserManager (like RegisteredUserAsync's own user), not TicketTestData.AddUserAsync's
        // raw insert — a real account always has a SecurityStamp, which DeleteAccountAsync's
        // UpdateAsync call requires.
        var agentUser = new ApplicationUser { UserName = UniqueEmail(), Email = UniqueEmail(), EmailConfirmed = true, DisplayName = "Agent", IsActive = true };
        await userManager.CreateAsync(agentUser, Password);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, registration.OrganizationId!.Value, agentUser.Id, UserRole.Agent, Now));
        await context.SaveChangesAsync();

        var result = await service.DeleteAccountAsync(agentUser.Id);

        Assert.True(result.Succeeded);
        Assert.True(await context.Organizations.AnyAsync(o => o.Id == registration.OrganizationId));
        Assert.True(await context.OrganizationMemberships.AnyAsync(m => m.UserId == registration.UserId)); // the Admin, untouched
    }

    [Fact]
    public async Task DeleteAccountAsync_Deactivates_DoesNotDeleteTheApplicationUserRow()
    {
        var (context, userManager, service, userId, organizationId) = await RegisteredUserAsync();
        var anotherAdmin = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, organizationId, anotherAdmin, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        await service.DeleteAccountAsync(userId);

        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(user); // row preserved — a literal delete would violate RESTRICT FKs anyway
        Assert.False(user!.IsActive);
        Assert.True(user.LockoutEnabled);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow.AddYears(50));
    }

    // ---------------------------------------------------------------------------------------
    // Historical data survives deletion (representative records, not just counts)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAccountAsync_PreservesTicketCommentAndEventHistory_AsQueryableValidRecords()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var registration = await service.RegisterAsync(new RegisterRequest("Historian", UniqueEmail(), Password, "Historian Org"));
        var organizationId = registration.OrganizationId!.Value;
        var userId = registration.UserId!.Value;

        // A team/category in the SAME organization the new Admin now belongs to (team/category
        // creation is not part of Phase 17's own flow — inserted directly, the same way every
        // other Application.Tests fixture builds its own minimal reference data).
        var team = new Team(0, organizationId, $"Team-{Guid.NewGuid():N}", Now);
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        var category = new Category(0, team.Id, $"Category-{Guid.NewGuid():N}", WorkType.Incident, Now);
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        context.TeamMembers.Add(new TeamMember(team.Id, userId, isTeamManager: false, Now));
        await context.SaveChangesAsync();

        var requester = new CurrentUser(userId, organizationId, UserRole.Admin, new HashSet<int> { team.Id }, new HashSet<int>());
        var ticketService = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));
        var (ticketId, reference) = await ticketService.CreateAsync(
            new CreateTicketRequest("Ticket created before account deletion", "Must remain intact afterward.", WorkType.Incident, Priority.Medium, team.Id, category.Id, null),
            requester);
        await ticketService.AddCommentAsync(ticketId, "A comment authored before deletion.", isInternal: false, requester);

        // A second Admin so deletion is actually allowed to proceed.
        var secondAdmin = await TicketTestData.AddUserAsync(context);
        context.OrganizationMemberships.Add(new OrganizationMembership(0, organizationId, secondAdmin, UserRole.Admin, Now));
        await context.SaveChangesAsync();

        var deleteResult = await service.DeleteAccountAsync(userId);
        Assert.True(deleteResult.Succeeded);

        await using var verify = _fixture.CreateContext();

        var ticket = await verify.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId);
        Assert.NotNull(ticket);
        Assert.Equal("Ticket created before account deletion", ticket!.Title);
        Assert.Equal(reference, ticket.Reference);
        Assert.Equal(userId, ticket.RequesterId); // actor reference preserved, not nulled or rewritten

        var comment = await verify.TicketComments.AsNoTracking().SingleOrDefaultAsync(c => c.TicketId == ticketId);
        Assert.NotNull(comment);
        Assert.Equal("A comment authored before deletion.", comment!.Body);
        Assert.Equal(userId, comment.AuthorId);

        var createdEvent = await verify.TicketEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TicketId == ticketId && e.EventType == TicketEventType.Created);
        Assert.NotNull(createdEvent);
        Assert.Equal(userId, createdEvent!.ActorUserId);

        // The actor's display name still resolves via the same join every read path already uses —
        // preserved deliberately (STEP 6/9: names are never rewritten or anonymized on deletion).
        var actorDisplayName = await verify.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync();
        Assert.Equal("Historian", actorDisplayName);

        var organization = await verify.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == organizationId);
        Assert.NotNull(organization);
    }

    // ---------------------------------------------------------------------------------------
    // Demo protection (existing DemoProtectionPolicy — now has its first real caller)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DemoProtectedAccount_CannotBeRenamedEmailChangedPasswordChangedOrDeleted()
    {
        await using var context = _fixture.CreateContext();
        using var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var user = new ApplicationUser
        {
            UserName = UniqueEmail(),
            Email = UniqueEmail(),
            EmailConfirmed = true,
            DisplayName = "Protected Persona",
            IsActive = true,
            IsDemoProtected = true,
        };
        await userManager.CreateAsync(user, Password);

        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => service.UpdateDisplayNameAsync(user.Id, "New Name"));
        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => service.ChangeEmailAsync(user.Id, UniqueEmail()));
        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => service.ChangePasswordAsync(user.Id, Password, "Another-Passw0rd!3"));
        await Assert.ThrowsAsync<DemoProtectedAccountException>(() => service.DeleteAccountAsync(user.Id));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, AccountService Service, Guid UserId, int OrganizationId)> RegisteredUserAsync()
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var service = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var registration = await service.RegisterAsync(new RegisterRequest("Original Name", UniqueEmail(), Password, "Original Org"));
        Assert.True(registration.Succeeded);

        return (context, userManager, service, registration.UserId!.Value, registration.OrganizationId!.Value);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@accounts.test.local";

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<FlowOpsDbContext>();

        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }
}
