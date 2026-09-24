using FlowOps.Application.Accounts;
using FlowOps.Application.Directory;
using FlowOps.Application.Organizations;
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

namespace FlowOps.Application.Tests.Organizations;

[Collection("Postgres")]
public sealed class InvitationServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public InvitationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, InvitationService Invitations, AccountService Accounts, Guid AdminId, int OrganizationId);

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var clock = new TicketTestData.FixedTimeProvider(Now);
        var accounts = new AccountService(context, userManager, clock);
        var invitations = new InvitationService(context, userManager, ResolveNormalizer(context), clock, TestEmail.Sender, TestEmail.Options);

        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", UniqueEmail(), Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        var currentUserAccessor = new CurrentUserAccessor(context, userManager);
        var admin = await currentUserAccessor.GetCurrentUserAsync(registration.UserId!.Value);

        return new World(context, userManager, invitations, accounts, registration.UserId!.Value, admin!.OrganizationId);
    }

    private static ILookupNormalizer ResolveNormalizer(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        return services.BuildServiceProvider().GetRequiredService<ILookupNormalizer>();
    }

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }

    /// <summary>Same as <see cref="NewOrganizationAsync"/> but wired to a caller-visible
    /// <see cref="RecordingEmailSender"/> so a test can inspect exactly what was sent. Uses a
    /// "Resend"-flavoured options record (verification pass) rather than the shared
    /// <see cref="TestEmail.Options"/> default of <c>Provider = "Log"</c> — this section's tests
    /// specifically exercise "a live provider is configured," which
    /// <see cref="CreateInvitationResult.EmailProviderConfigured"/> now distinguishes from the
    /// no-provider-configured case.</summary>
    private async Task<(World World, RecordingEmailSender EmailSender)> NewOrganizationWithEmailAsync(string label)
    {
        var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var clock = new TicketTestData.FixedTimeProvider(Now);
        var accounts = new AccountService(context, userManager, clock);
        var emailSender = new RecordingEmailSender();
        var liveProviderOptions = new FlowOps.Infrastructure.Email.EmailOptions
        {
            Provider = "Resend",
            FromAddress = TestEmail.Options.FromAddress,
            FromName = TestEmail.Options.FromName,
            BaseUrl = TestEmail.Options.BaseUrl,
        };
        var invitations = new InvitationService(context, userManager, ResolveNormalizer(context), clock, emailSender, liveProviderOptions);

        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", UniqueEmail(), Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        var currentUserAccessor = new CurrentUserAccessor(context, userManager);
        var admin = await currentUserAccessor.GetCurrentUserAsync(registration.UserId!.Value);

        return (new World(context, userManager, invitations, accounts, registration.UserId!.Value, admin!.OrganizationId), emailSender);
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@invitations.test.local";

    private static CurrentUser AsCurrentUser(Guid userId, int organizationId, UserRole role) =>
        new(userId, organizationId, role, new HashSet<int>(), new HashSet<int>());

    // ---------------------------------------------------------------------------------------
    // Invitation creation — authorization
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_Admin_Succeeds()
    {
        var world = await NewOrganizationAsync("Admin");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Manager));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.RawToken);
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task CreateInvitationAsync_AgentOrViewer_IsDenied(UserRole role)
    {
        var world = await NewOrganizationAsync("Denied" + role);
        var actor = AsCurrentUser(Guid.NewGuid(), world.OrganizationId, role);

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() =>
            world.Invitations.CreateInvitationAsync(actor, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent)));
    }

    [Theory]
    [InlineData(UserRole.Agent, true)]
    [InlineData(UserRole.Viewer, true)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Admin, false)]
    public async Task CreateInvitationAsync_Manager_LeastPrivilegeRoleAssignment(UserRole targetRole, bool shouldSucceed)
    {
        var world = await NewOrganizationAsync("Mgr" + targetRole);
        // A real, persisted user — Invitation.InvitedByUserId is a real FK to AspNetUsers, so a
        // fabricated id would fail at the database regardless of what this test is trying to check.
        var managerUserId = await TicketTestData.AddUserAsync(world.Context);
        var manager = AsCurrentUser(managerUserId, world.OrganizationId, UserRole.Manager);

        if (shouldSucceed)
        {
            var result = await world.Invitations.CreateInvitationAsync(manager, new CreateInvitationRequest(UniqueEmail(), targetRole));
            Assert.True(result.Succeeded);
        }
        else
        {
            await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() =>
                world.Invitations.CreateInvitationAsync(manager, new CreateInvitationRequest(UniqueEmail(), targetRole)));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Invitation creation — data / token
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_NormalizesEmail_AndPersistsHashNotRawToken()
    {
        var world = await NewOrganizationAsync("Norm");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var email = $"MixedCase.{Guid.NewGuid():N}@Invitations.Test.Local";

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Agent));

        Assert.True(result.Succeeded);
        var stored = await world.Context.Invitations.AsNoTracking().SingleAsync(i => i.OrganizationId == world.OrganizationId);
        Assert.Equal(email.ToUpperInvariant(), stored.NormalizedInvitedEmail);
        Assert.NotEqual(result.RawToken, stored.TokenHash); // raw token never equals its own hash
        Assert.DoesNotContain(result.RawToken!, stored.TokenHash, StringComparison.Ordinal); // and isn't embedded in it either
    }

    [Fact]
    public async Task CreateInvitationAsync_TwoInvitations_ProduceDifferentRandomTokens()
    {
        var world = await NewOrganizationAsync("Rand");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var first = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));
        var second = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        Assert.NotEqual(first.RawToken, second.RawToken);
    }

    [Fact]
    public async Task CreateInvitationAsync_SetsExpiration()
    {
        var world = await NewOrganizationAsync("Expiry");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        var stored = await world.Context.Invitations.AsNoTracking().SingleAsync(i => i.OrganizationId == world.OrganizationId);
        Assert.Equal(Now + FlowOps.Domain.Organizations.Invitation.DefaultLifetime, stored.ExpiresAt);
    }

    // ---------------------------------------------------------------------------------------
    // Invitation creation — duplicate / existing-member behavior
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_AlreadyAMember_IsRejected()
    {
        var world = await NewOrganizationAsync("Dup");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var adminEmail = await world.UserManager.Users.Where(u => u.Id == world.AdminId).Select(u => u.Email).SingleAsync();

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(adminEmail!, UserRole.Agent));

        Assert.Equal(CreateInvitationOutcome.AlreadyMember, result.Outcome);
    }

    [Fact]
    public async Task CreateInvitationAsync_ActiveInvitationAlreadyExists_IsRejected()
    {
        var world = await NewOrganizationAsync("ActiveDup");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var email = UniqueEmail();
        await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Agent));

        var second = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Viewer));

        Assert.Equal(CreateInvitationOutcome.ActiveInvitationExists, second.Outcome);
    }

    [Fact] // Step 10: an expired invitation never blocks a fresh one for the same email.
    public async Task CreateInvitationAsync_ExpiredInvitationExists_AllowsANewOne()
    {
        var world = await NewOrganizationAsync("Reissue");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var email = UniqueEmail();

        var expiredClockService = new InvitationService(world.Context, world.UserManager, ResolveNormalizer(world.Context), new TicketTestData.FixedTimeProvider(Now.AddDays(-10)), TestEmail.Sender, TestEmail.Options);
        await expiredClockService.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Agent));

        var second = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Viewer));

        Assert.True(second.Succeeded);
    }

    // ---------------------------------------------------------------------------------------
    // Pending invitations list (verification pass)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingInvitationsAsync_Admin_SeesAnInvitationTheyCreated()
    {
        var world = await NewOrganizationAsync("PendingAdmin");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var email = UniqueEmail();
        await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, UserRole.Agent));

        var pending = await world.Invitations.GetPendingInvitationsAsync(admin);

        var invitation = Assert.Single(pending);
        Assert.Equal(email, invitation.Email);
        Assert.Equal(UserRole.Agent, invitation.Role);
        Assert.False(invitation.IsExpired);
    }

    [Fact] // ORG-RULE-11: whoever may manage members sees every pending invitation, not only the
           // ones they personally created.
    public async Task GetPendingInvitationsAsync_ManagerSeesInvitationsCreatedByAdmin()
    {
        var world = await NewOrganizationAsync("PendingManager");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        var managerUserId = await CreateAdditionalMemberAsync(world, UserRole.Manager);
        var manager = AsCurrentUser(managerUserId, world.OrganizationId, UserRole.Manager);

        var pending = await world.Invitations.GetPendingInvitationsAsync(manager);

        Assert.Single(pending);
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task GetPendingInvitationsAsync_AgentOrViewer_IsDenied(UserRole role)
    {
        var world = await NewOrganizationAsync("PendingDenied");
        var actor = AsCurrentUser(Guid.NewGuid(), world.OrganizationId, role);

        await Assert.ThrowsAsync<OrganizationAccessDeniedException>(() =>
            world.Invitations.GetPendingInvitationsAsync(actor));
    }

    [Fact]
    public async Task GetPendingInvitationsAsync_AcceptedInvitation_DoesNotAppear()
    {
        var orgA = await NewOrganizationAsync("PendingAcceptedA");
        var orgB = await NewOrganizationAsync("PendingAcceptedB");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();
        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Viewer));
        await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        var pending = await orgA.Invitations.GetPendingInvitationsAsync(adminA);

        Assert.Empty(pending);
    }

    [Fact] // Expired invitations are shown, not silently hidden — the caller sees the honest state.
    public async Task GetPendingInvitationsAsync_ExpiredInvitation_AppearsMarkedAsExpired()
    {
        var world = await NewOrganizationAsync("PendingExpired");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var expiredClockService = new InvitationService(world.Context, world.UserManager, ResolveNormalizer(world.Context), new TicketTestData.FixedTimeProvider(Now.AddDays(-10)), TestEmail.Sender, TestEmail.Options);
        await expiredClockService.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        var pending = await world.Invitations.GetPendingInvitationsAsync(admin);

        var invitation = Assert.Single(pending);
        Assert.True(invitation.IsExpired);
    }

    [Fact]
    public async Task GetPendingInvitationsAsync_NeverCrossesOrganizationBoundary()
    {
        var orgA = await NewOrganizationAsync("PendingIsoA");
        var orgB = await NewOrganizationAsync("PendingIsoB");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var adminB = AsCurrentUser(orgB.AdminId, orgB.OrganizationId, UserRole.Admin);
        await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        var pendingForB = await orgB.Invitations.GetPendingInvitationsAsync(adminB);

        Assert.Empty(pendingForB);
    }

    /// <summary>A second, real registered-and-accepted member of <paramref name="world"/>'s
    /// organization, for a test that needs an authorization actor beyond the seeded Admin.</summary>
    private async Task<Guid> CreateAdditionalMemberAsync(World world, UserRole role)
    {
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var email = UniqueEmail();
        var invite = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(email, role));
        var accept = await world.Invitations.AcceptForNewUserAsync(invite.RawToken!, "Additional Member", Password);
        Assert.True(accept.Succeeded);
        return accept.UserId!.Value;
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance — existing user
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptForCurrentUserAsync_ValidInvitation_CreatesMembershipWithInvitedRole()
    {
        var orgA = await NewOrganizationAsync("A1");
        var orgB = await NewOrganizationAsync("B1");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();
        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Viewer));

        var accept = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.Success, accept.Outcome);
        var membership = await orgA.Context.OrganizationMemberships.AsNoTracking()
            .SingleAsync(m => m.OrganizationId == orgA.OrganizationId && m.UserId == orgB.AdminId);
        Assert.Equal(UserRole.Viewer, membership.Role);
    }

    [Fact] // Step 14: accepting into Org A must never touch the user's Org B membership.
    public async Task AcceptForCurrentUserAsync_ExistingUsersOtherMembershipsAreUnaffected()
    {
        var orgA = await NewOrganizationAsync("A2");
        var orgB = await NewOrganizationAsync("B2");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();

        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent));
        await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        var orgBMembership = await orgB.Context.OrganizationMemberships.AsNoTracking()
            .SingleAsync(m => m.OrganizationId == orgB.OrganizationId && m.UserId == orgB.AdminId);
        Assert.Equal(UserRole.Admin, orgBMembership.Role); // untouched

        var allMemberships = await orgA.Context.OrganizationMemberships.AsNoTracking().Where(m => m.UserId == orgB.AdminId).ToListAsync();
        Assert.Equal(2, allMemberships.Count); // now in both organizations, never merged/overwritten
    }

    [Fact]
    public async Task AcceptForCurrentUserAsync_WrongAccount_IsRejected()
    {
        var orgA = await NewOrganizationAsync("A3");
        var orgB = await NewOrganizationAsync("B3");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);

        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        // orgB's admin (a real, different account) tries to claim an invitation addressed to
        // someone else entirely, using a genuine, valid token they happen to have obtained.
        var accept = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.EmailMismatch, accept.Outcome);
        Assert.False(await orgA.Context.OrganizationMemberships.AnyAsync(m => m.UserId == orgB.AdminId && m.OrganizationId == orgA.OrganizationId));
    }

    [Fact]
    public async Task AcceptForCurrentUserAsync_ExpiredInvitation_IsRejected()
    {
        var orgA = await NewOrganizationAsync("A4");
        var orgB = await NewOrganizationAsync("B4");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();

        var pastClockService = new InvitationService(orgA.Context, orgA.UserManager, ResolveNormalizer(orgA.Context), new TicketTestData.FixedTimeProvider(Now.AddDays(-10)), TestEmail.Sender, TestEmail.Options);
        var invite = await pastClockService.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent));

        var accept = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.Expired, accept.Outcome);
    }

    [Fact]
    public async Task AcceptForCurrentUserAsync_InvalidToken_IsRejected()
    {
        var orgA = await NewOrganizationAsync("A5");

        var accept = await orgA.Invitations.AcceptForCurrentUserAsync("not-a-real-token", orgA.AdminId);

        Assert.Equal(AcceptInvitationOutcome.NotFound, accept.Outcome);
    }

    [Fact]
    public async Task AcceptForCurrentUserAsync_AlreadyUsed_CannotBeAcceptedAgain()
    {
        var orgA = await NewOrganizationAsync("A6");
        var orgB = await NewOrganizationAsync("B6");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();

        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent));
        var first = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);
        Assert.Equal(AcceptInvitationOutcome.Success, first.Outcome);

        var second = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.AlreadyUsed, second.Outcome);
        Assert.Equal(1, await orgA.Context.OrganizationMemberships.CountAsync(m => m.UserId == orgB.AdminId && m.OrganizationId == orgA.OrganizationId));
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance — new user
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptForNewUserAsync_CreatesAccountAndMembership_NoSecondOrganization()
    {
        var world = await NewOrganizationAsync("New1");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var invitedEmail = UniqueEmail();
        var invite = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(invitedEmail, UserRole.Viewer));

        var organizationCountBefore = await world.Context.Organizations.CountAsync();

        var accept = await world.Invitations.AcceptForNewUserAsync(invite.RawToken!, "New Person", Password);

        Assert.Equal(AcceptInvitationOutcome.Success, accept.Outcome);
        Assert.Equal(world.OrganizationId, accept.OrganizationId); // joined the INVITING org, not a new one
        Assert.Equal(organizationCountBefore, await world.Context.Organizations.CountAsync()); // no new org created

        var user = await world.Context.Users.AsNoTracking().SingleAsync(u => u.Id == accept.UserId);
        Assert.Equal(invitedEmail, user.Email); // exactly the invited email, never client-supplied

        var membership = await world.Context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == accept.UserId);
        Assert.Equal(UserRole.Viewer, membership.Role); // the inviter's chosen role, not caller-selectable
    }

    [Fact]
    public async Task AcceptForNewUserAsync_ExpiredInvitation_CreatesNoAccountAtAll()
    {
        var world = await NewOrganizationAsync("New2");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var invitedEmail = UniqueEmail();

        var pastClockService = new InvitationService(world.Context, world.UserManager, ResolveNormalizer(world.Context), new TicketTestData.FixedTimeProvider(Now.AddDays(-10)), TestEmail.Sender, TestEmail.Options);
        var invite = await pastClockService.CreateInvitationAsync(admin, new CreateInvitationRequest(invitedEmail, UserRole.Viewer));

        var accept = await world.Invitations.AcceptForNewUserAsync(invite.RawToken!, "Too Late", Password);

        Assert.Equal(AcceptInvitationOutcome.Expired, accept.Outcome);
        Assert.False(await world.Context.Users.AnyAsync(u => u.Email == invitedEmail));
    }

    [Fact]
    public async Task AcceptForNewUserAsync_WeakPassword_CreatesNoAccountAtAll()
    {
        var world = await NewOrganizationAsync("New3");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var invitedEmail = UniqueEmail();
        var invite = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(invitedEmail, UserRole.Viewer));

        var accept = await world.Invitations.AcceptForNewUserAsync(invite.RawToken!, "Weak", "short");

        Assert.Equal(AcceptInvitationOutcome.ValidationFailed, accept.Outcome);
        Assert.False(await world.Context.Users.AnyAsync(u => u.Email == invitedEmail));
        // The invitation must still be usable after a failed account-creation attempt.
        var stillValid = await world.Invitations.GetInvitationDetailsAsync(invite.RawToken!);
        Assert.Equal(FlowOps.Application.Organizations.InvitationState.Valid, stillValid.State);
    }

    // ---------------------------------------------------------------------------------------
    // Concurrency — Step 24
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptForCurrentUserAsync_ConcurrentAcceptance_OnlyOneSucceeds()
    {
        var orgA = await NewOrganizationAsync("Race1");
        var orgB = await NewOrganizationAsync("Race2");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();
        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent));

        // Two independent DbContexts/services racing to accept the exact same token — simulates
        // two simultaneous HTTP requests, not two calls sharing one tracked context.
        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();
        var serviceA = new InvitationService(contextA, CreateUserManager(contextA), ResolveNormalizer(contextA), new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var serviceB = new InvitationService(contextB, CreateUserManager(contextB), ResolveNormalizer(contextB), new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);

        var taskA = serviceA.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);
        var taskB = serviceB.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Outcome == AcceptInvitationOutcome.Success);
        Assert.Single(results, r => r.Outcome == AcceptInvitationOutcome.AlreadyUsed);

        var membershipCount = await orgA.Context.OrganizationMemberships
            .CountAsync(m => m.OrganizationId == orgA.OrganizationId && m.UserId == orgB.AdminId);
        Assert.Equal(1, membershipCount); // exactly one membership, never two
    }

    // ---------------------------------------------------------------------------------------
    // Invitation details page (Step 11) — never discloses internal ids
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetInvitationDetailsAsync_ValidInvitation_ShowsOrgEmailAndRole()
    {
        var world = await NewOrganizationAsync("Details1");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var invitedEmail = UniqueEmail();
        var invite = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(invitedEmail, UserRole.Manager));

        var details = await world.Invitations.GetInvitationDetailsAsync(invite.RawToken!);

        Assert.Equal(FlowOps.Application.Organizations.InvitationState.Valid, details.State);
        Assert.Equal(invitedEmail, details.InvitedEmail);
        Assert.Equal(UserRole.Manager, details.Role);
        Assert.NotNull(details.OrganizationName);
    }

    [Fact]
    public async Task GetInvitationDetailsAsync_UnknownToken_ReturnsNotFound()
    {
        var world = await NewOrganizationAsync("Details2");

        var details = await world.Invitations.GetInvitationDetailsAsync("bogus-token-value");

        Assert.Equal(FlowOps.Application.Organizations.InvitationState.NotFound, details.State);
    }

    // ---------------------------------------------------------------------------------------
    // ADR-0027: inviting into a specific team
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_WithTeamId_AndAccepting_AddsTeamMembership()
    {
        var orgA = await NewOrganizationAsync("Team1A");
        var orgB = await NewOrganizationAsync("Team1B");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var teamService = new TeamService(orgA.Context, new TicketTestData.FixedTimeProvider(Now));
        var team = await teamService.CreateAsync(adminA, "Service Desk");
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();

        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent, team.TeamId));
        Assert.True(invite.Succeeded);

        var accept = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.Success, accept.Outcome);
        Assert.True(await orgA.Context.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == team.TeamId && m.UserId == orgB.AdminId));
    }

    [Fact] // Never a client-trusted id — a team from a different organization must be refused.
    public async Task CreateInvitationAsync_WithTeamIdFromAnotherOrganization_IsRejected()
    {
        var orgA = await NewOrganizationAsync("Team2A");
        var orgB = await NewOrganizationAsync("Team2B");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var adminB = AsCurrentUser(orgB.AdminId, orgB.OrganizationId, UserRole.Admin);
        var teamInB = await new TeamService(orgB.Context, new TicketTestData.FixedTimeProvider(Now)).CreateAsync(adminB, "Org B Team");

        var result = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent, teamInB.TeamId));

        Assert.Equal(CreateInvitationOutcome.ValidationFailed, result.Outcome);
    }

    [Fact] // The named team may be deactivated between invite and accept — acceptance itself must
           // still succeed at the organization-membership level, just without the team join.
    public async Task AcceptForCurrentUserAsync_NamedTeamDeactivatedMeanwhile_StillCreatesMembership()
    {
        var orgA = await NewOrganizationAsync("Team3A");
        var orgB = await NewOrganizationAsync("Team3B");
        var adminA = AsCurrentUser(orgA.AdminId, orgA.OrganizationId, UserRole.Admin);
        var teamService = new TeamService(orgA.Context, new TicketTestData.FixedTimeProvider(Now));
        var team = await teamService.CreateAsync(adminA, "Service Desk");
        var existingUserEmail = await orgB.UserManager.Users.Where(u => u.Id == orgB.AdminId).Select(u => u.Email).SingleAsync();
        var invite = await orgA.Invitations.CreateInvitationAsync(adminA, new CreateInvitationRequest(existingUserEmail!, UserRole.Agent, team.TeamId));

        await teamService.DeactivateAsync(adminA, team.TeamId!.Value);
        var accept = await orgA.Invitations.AcceptForCurrentUserAsync(invite.RawToken!, orgB.AdminId);

        Assert.Equal(AcceptInvitationOutcome.Success, accept.Outcome);
        Assert.False(await orgA.Context.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == team.TeamId && m.UserId == orgB.AdminId));
        Assert.True(await orgA.Context.OrganizationMemberships.AsNoTracking().AnyAsync(m => m.OrganizationId == orgA.OrganizationId && m.UserId == orgB.AdminId));
    }

    // ---------------------------------------------------------------------------------------
    // Transactional email (Phase 30 / ADR-0035)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_SendsEmailToTheInvitedAddress()
    {
        var (world, emailSender) = await NewOrganizationWithEmailAsync("EmailOk");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        var invitedEmail = UniqueEmail();

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(invitedEmail, UserRole.Agent));

        Assert.True(result.Succeeded);
        Assert.True(result.EmailDeliverySucceeded);
        Assert.True(result.EmailProviderConfigured);
        var sent = Assert.Single(emailSender.SentMessages);
        Assert.Equal(invitedEmail, sent.ToEmail);
        Assert.Contains("invited", sent.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Verification pass: the actual default (no live provider configured) — the case every
           // local/CI environment hits, and the one the old wording ("emailed to the invited
           // address") misrepresented as a real send.
    public async Task CreateInvitationAsync_NoLiveProviderConfigured_ReportsEmailProviderNotConfigured()
    {
        var world = await NewOrganizationAsync("EmailNotConfigured"); // TestEmail.Options: Provider = "Log"
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        Assert.True(result.Succeeded);
        Assert.False(result.EmailProviderConfigured);
        // EmailDeliverySucceeded reflects the injected sender's own outcome (true here, from
        // TestEmail.Sender's double) and is a distinct fact from EmailProviderConfigured above —
        // in production, LogEmailSender itself always reports success too (nothing was attempted),
        // which is exactly why the UI must key its wording on EmailProviderConfigured, not this.
        Assert.True(result.EmailDeliverySucceeded);
    }

    [Fact] // The token appears only inside the one accept URL — never bare or duplicated elsewhere.
    public async Task CreateInvitationAsync_EmailContainsTheAcceptUrl_AndNoRawTokenLeaksOutsideIt()
    {
        var (world, emailSender) = await NewOrganizationWithEmailAsync("EmailUrl");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        var sent = Assert.Single(emailSender.SentMessages);
        var expectedUrl = $"{TestEmail.Options.BaseUrl.TrimEnd('/')}/Account/AcceptInvitation?token={Uri.EscapeDataString(result.RawToken!)}";
        Assert.Contains(expectedUrl, sent.TextBody, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, sent.HtmlBody, StringComparison.Ordinal);

        // The raw token's only appearance in either body is inside that one URL.
        Assert.Equal(1, CountOccurrences(sent.TextBody, result.RawToken!));
        Assert.Equal(1, CountOccurrences(sent.HtmlBody, result.RawToken!));
    }

    [Fact] // Failure semantics: a failed send never rolls back the already-created invitation.
    public async Task CreateInvitationAsync_EmailDeliveryFails_InvitationIsStillCreated()
    {
        var (world, emailSender) = await NewOrganizationWithEmailAsync("EmailFail");
        var admin = AsCurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin);
        emailSender.FailNextSends = true;

        var result = await world.Invitations.CreateInvitationAsync(admin, new CreateInvitationRequest(UniqueEmail(), UserRole.Agent));

        Assert.True(result.Succeeded);
        Assert.False(result.EmailDeliverySucceeded);
        Assert.True(result.EmailProviderConfigured); // a real send was attempted and genuinely failed
        Assert.NotNull(result.RawToken);
        Assert.Empty(emailSender.SentMessages);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
