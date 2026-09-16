using FlowOps.Application.Accounts;
using FlowOps.Application.Organizations;
using FlowOps.Application.Platform;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Platform;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Platform;

/// <summary>Phase 24 (ADR-0023): <see cref="PlatformUserService"/> — user deactivation reuses
/// <see cref="AccountService.DeleteAccountAsync"/>'s lockout recipe but must NEVER remove any
/// <see cref="OrganizationMembership"/> row, unlike that self-service path.</summary>
[Collection("Postgres")]
public sealed class PlatformUserServiceTests
{
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public PlatformUserServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(FlowOpsDbContext Context, UserManager<ApplicationUser> UserManager, Guid AdminId, int OrganizationId);

    [Fact]
    public async Task ListUsersAsync_SearchByEmailFindsTheUser()
    {
        var world = await NewOrganizationAsync("PU1");
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var email = (await world.UserManager.FindByIdAsync(world.AdminId.ToString()))!.Email!;

        var result = await service.ListUsersAsync(pageNumber: 1, search: email);

        Assert.Contains(result.Items, u => u.UserId == world.AdminId);
    }

    [Fact]
    public async Task GetUserDetailAsync_ShowsEveryOrganizationMembershipAndRole()
    {
        var worldA = await NewOrganizationAsync("PU2A");
        var worldB = await NewOrganizationAsync("PU2B");
        worldA.Context.OrganizationMemberships.Add(new OrganizationMembership(0, worldB.OrganizationId, worldA.AdminId, UserRole.Viewer, Now));
        await worldA.Context.SaveChangesAsync();
        var service = new PlatformUserService(worldA.Context, worldA.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var detail = await service.GetUserDetailAsync(worldA.AdminId);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Memberships.Count);
        Assert.Contains(detail.Memberships, m => m.OrganizationId == worldA.OrganizationId && m.Role == "Admin");
        Assert.Contains(detail.Memberships, m => m.OrganizationId == worldB.OrganizationId && m.Role == "Viewer");
    }

    [Fact]
    public async Task GetUserDetailAsync_UnknownId_ReturnsNull()
    {
        var world = await NewOrganizationAsync("PU3");
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var detail = await service.GetUserDetailAsync(Guid.NewGuid());

        Assert.Null(detail);
    }

    [Fact]
    public async Task DeactivateAsync_NonSoleAdminUser_Succeeds()
    {
        var world = await NewOrganizationAsync("PU4");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Admin);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.DeactivateUserAsync(PlatformAdmin(world), secondAdminId);

        Assert.True(result.Succeeded);
        var user = await world.UserManager.FindByIdAsync(secondAdminId.ToString());
        Assert.False(user!.IsActive);
        Assert.True(user.LockoutEnabled);
        Assert.Equal(DateTimeOffset.MaxValue, user.LockoutEnd);
    }

    [Fact] // ORG-RULE-12, reused: deactivating the sole Admin of an organization is blocked.
    public async Task DeactivateAsync_SoleAdminOfAnOrganization_IsBlocked()
    {
        var world = await NewOrganizationAsync("PU5");
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.DeactivateUserAsync(PlatformAdmin(world), world.AdminId);

        Assert.False(result.Succeeded);
        var user = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.True(user!.IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_PreservesOrganizationMembership_UnlikeSelfAccountDeletion()
    {
        var world = await NewOrganizationAsync("PU6");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Agent);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await service.DeactivateUserAsync(PlatformAdmin(world), secondAdminId);

        var membership = await world.Context.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.UserId == secondAdminId && m.OrganizationId == world.OrganizationId);
        Assert.NotNull(membership);
    }

    [Fact]
    public async Task DeactivateThenReactivate_UserCanAuthenticateAgain()
    {
        var world = await NewOrganizationAsync("PU7");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Agent);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateUserAsync(PlatformAdmin(world), secondAdminId);

        var result = await service.ReactivateUserAsync(PlatformAdmin(world), secondAdminId);

        Assert.True(result.Succeeded);
        var user = await world.UserManager.FindByIdAsync(secondAdminId.ToString());
        Assert.True(user!.IsActive);
        Assert.False(user.LockoutEnabled);
        Assert.Null(user.LockoutEnd);
    }

    [Fact]
    public async Task DeactivateAndReactivate_RecordAuditEvents()
    {
        var world = await NewOrganizationAsync("PU8");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Agent);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var actor = PlatformAdmin(world);
        await service.DeactivateUserAsync(actor, secondAdminId);
        await service.ReactivateUserAsync(actor, secondAdminId);

        var events = await world.Context.PlatformAuditEvents
            .Where(e => e.TargetUserId == secondAdminId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(PlatformEventType.UserDeactivated, events[0].EventType);
        Assert.Equal(PlatformEventType.UserReactivated, events[1].EventType);
    }

    [Fact] // SoleAdminGuard fix (ADR-0023 §5 remark): an already-deactivated Admin's membership
           // row must never count as "another Admin" — otherwise deactivating the last genuinely
           // active Admin of an organization would silently succeed and leave zero usable Admins.
    public async Task DeactivateAsync_LastActiveAdmin_IsBlockedEvenWhenAnotherAdminMembershipRowIsAlreadyInactive()
    {
        var world = await NewOrganizationAsync("PU10");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Admin);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var actor = PlatformAdmin(world);

        // Deactivate the second Admin first — their OrganizationMembership row (Role=Admin) is
        // deliberately never removed by platform deactivation (ADR-0023), so it still exists.
        var firstResult = await service.DeactivateUserAsync(actor, secondAdminId);
        Assert.True(firstResult.Succeeded);

        // The original Admin is now the only one who can actually sign in — deactivating them too
        // must be blocked, even though two Admin membership rows still exist for this organization.
        var secondResult = await service.DeactivateUserAsync(actor, world.AdminId);

        Assert.False(secondResult.Succeeded);
        var originalAdmin = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.True(originalAdmin!.IsActive);
    }

    [Fact] // Historical attribution (requester/assignee/comments) must remain intact.
    public async Task DeactivateAsync_DoesNotAffectHistoricalTicketAttribution()
    {
        var world = await NewOrganizationAsync("PU9");
        var teamService = new FlowOps.Application.Directory.TeamService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var catalogService = new FlowOps.Application.Catalog.CatalogService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var admin = new CurrentUser(world.AdminId, world.OrganizationId, UserRole.Admin, new HashSet<int>(), new HashSet<int>());
        var teamResult = await teamService.CreateAsync(admin, "Support");
        var categoryResult = await catalogService.CreateCategoryAsync(admin, teamResult.TeamId!.Value, "Incidents", WorkType.Incident);
        var ticketService = new FlowOps.Application.Tickets.TicketService(world.Context, new TicketTestData.FixedTimeProvider(Now));
        var (ticketId, _) = await ticketService.CreateAsync(
            new FlowOps.Application.Tickets.CreateTicketRequest("Printer jam", "The printer is jammed.", WorkType.Incident, Priority.Low, teamResult.TeamId.Value, categoryResult.CategoryId!.Value, null),
            admin);

        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        // A second Admin exists so deactivating the requester is not blocked by the sole-admin rule.
        await AddMemberAsync(world, "Second Admin", UserRole.Admin);
        await service.DeactivateUserAsync(PlatformAdmin(world), world.AdminId);

        var ticket = await world.Context.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(world.AdminId, ticket.RequesterId);
    }

    [Fact]
    public async Task GetRecentAuditEventsAsync_ReturnsMostRecentFirst()
    {
        var world = await NewOrganizationAsync("PU11");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Agent);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var actor = PlatformAdmin(world);
        await service.DeactivateUserAsync(actor, secondAdminId);
        await service.ReactivateUserAsync(actor, secondAdminId);

        var events = await service.GetRecentAuditEventsAsync(secondAdminId);

        Assert.Equal(2, events.Count);
        Assert.Equal(PlatformEventType.UserReactivated, events[0].EventType);
        Assert.Equal(PlatformEventType.UserDeactivated, events[1].EventType);
    }

    // ---------------------------------------------------------------------------------------
    // Phase 24A (ADR-0024): account approval
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ApproveUserAsync_Pending_BecomesActive_AndClearsLockout()
    {
        var world = await NewPendingOrganizationAsync("PA1");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.ApproveUserAsync(actor, world.AdminId);

        Assert.True(result.Succeeded);
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Active, detail!.Status);
        var user = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.False(user!.LockoutEnabled);
        Assert.Null(user.LockoutEnd);
    }

    [Fact]
    public async Task ApproveUserAsync_PendingUserCanThenSignIn()
    {
        var world = await NewPendingOrganizationAsync("PA2");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var user = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.True(await world.UserManager.IsLockedOutAsync(user!)); // sanity: genuinely blocked before approval

        await service.ApproveUserAsync(actor, world.AdminId);

        var reloaded = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.False(await world.UserManager.IsLockedOutAsync(reloaded!));
    }

    [Fact]
    public async Task ApproveUserAsync_RecordsAuditEvent()
    {
        var world = await NewPendingOrganizationAsync("PA3");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await service.ApproveUserAsync(actor, world.AdminId);

        var events = await world.Context.PlatformAuditEvents.Where(e => e.TargetUserId == world.AdminId).ToListAsync();
        var approvalEvent = Assert.Single(events);
        Assert.Equal(PlatformEventType.UserApproved, approvalEvent.EventType);
        Assert.Equal(actor.UserId, approvalEvent.ActorUserId);
    }

    [Fact] // spec §13: "Active → Approve again" is an idempotent success, not a failure.
    public async Task ApproveUserAsync_AlreadyActive_IsIdempotent()
    {
        var world = await NewOrganizationAsync("PA4"); // already approved by the fixture
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.ApproveUserAsync(actor, world.AdminId);

        Assert.True(result.Succeeded);
    }

    [Fact] // spec §14: Approve must never be a backdoor around Reactivate's own gate.
    public async Task ApproveUserAsync_Inactive_IsRejected_PointsToReactivate()
    {
        var world = await NewOrganizationAsync("PA5");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Admin);
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateUserAsync(actor, secondAdminId);

        var result = await service.ApproveUserAsync(actor, secondAdminId);

        Assert.False(result.Succeeded);
        Assert.Contains("Reactivate", result.Error);
    }

    [Fact]
    public async Task ApproveUserAsync_UnknownUser_Throws()
    {
        var world = await NewOrganizationAsync("PA6");
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<PlatformAccessDeniedException>(() => service.ApproveUserAsync(actor, Guid.NewGuid()));
    }

    [Fact] // spec §14: Reactivate must never approve a still-Pending account.
    public async Task ReactivateUserAsync_Pending_IsRejected_PointsToApprove()
    {
        var world = await NewPendingOrganizationAsync("PA7");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.ReactivateUserAsync(actor, world.AdminId);

        Assert.False(result.Succeeded);
        Assert.Contains("Approve", result.Error);
    }

    [Fact] // spec §14: Deactivate must never apply to a still-Pending account.
    public async Task DeactivateUserAsync_Pending_IsRejected()
    {
        var world = await NewPendingOrganizationAsync("PA8");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.DeactivateUserAsync(actor, world.AdminId);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ListUsersAsync_FilteredByPendingStatus_ReturnsOnlyPendingAccounts()
    {
        var pendingWorld = await NewPendingOrganizationAsync("PA9Pending");
        var activeWorld = await NewOrganizationAsync("PA9Active");
        var service = new PlatformUserService(pendingWorld.Context, pendingWorld.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.ListUsersAsync(pageNumber: 1, status: FlowOps.Domain.Accounts.AccountStatus.Pending);

        Assert.Contains(result.Items, u => u.UserId == pendingWorld.AdminId);
        Assert.DoesNotContain(result.Items, u => u.UserId == activeWorld.AdminId);
    }

    [Fact]
    public async Task GetUserStatusSummaryAsync_CountsEachStatusSeparately()
    {
        var pendingWorld = await NewPendingOrganizationAsync("PA10Pending");
        var service = new PlatformUserService(pendingWorld.Context, pendingWorld.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var summary = await service.GetUserStatusSummaryAsync();

        Assert.True(summary.Pending >= 1);
        Assert.Equal(summary.Pending + summary.Active + summary.Inactive + summary.Rejected, summary.Total);
    }

    [Fact]
    public async Task GetUserStatusSummaryAsync_CountsRejectedSeparately()
    {
        var pendingWorld = await NewPendingOrganizationAsync("PA10bRejected");
        var actor = await PlatformOperatorAsync(pendingWorld);
        var service = new PlatformUserService(pendingWorld.Context, pendingWorld.UserManager, new TicketTestData.FixedTimeProvider(Now));
        var before = await service.GetUserStatusSummaryAsync();

        await service.RejectUserAsync(actor, pendingWorld.AdminId);
        var after = await service.GetUserStatusSummaryAsync();

        Assert.Equal(before.Pending - 1, after.Pending);
        Assert.Equal(before.Rejected + 1, after.Rejected);
        Assert.Equal(before.Total, after.Total); // moved between buckets, nothing created or destroyed
    }

    // ---------------------------------------------------------------------------------------
    // Phase 24A-Extension (ADR-0025): account rejection
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RejectUserAsync_Pending_BecomesRejected_AndStaysLockedOut()
    {
        var world = await NewPendingOrganizationAsync("PR1");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RejectUserAsync(actor, world.AdminId);

        Assert.True(result.Succeeded);
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Rejected, detail!.Status);
        var user = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.True(await world.UserManager.IsLockedOutAsync(user!)); // never loosened by rejection
    }

    [Fact]
    public async Task RejectUserAsync_DoesNotDeleteAccountOrganizationOrMembership()
    {
        var world = await NewPendingOrganizationAsync("PR2");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await service.RejectUserAsync(actor, world.AdminId);

        var user = await world.UserManager.FindByIdAsync(world.AdminId.ToString());
        Assert.NotNull(user); // row preserved — never a hard delete (spec §5/§27)
        var organizationExists = await world.Context.Organizations.AnyAsync(o => o.Id == world.OrganizationId);
        Assert.True(organizationExists);
        var membershipExists = await world.Context.OrganizationMemberships.AnyAsync(m => m.UserId == world.AdminId && m.OrganizationId == world.OrganizationId);
        Assert.True(membershipExists);
    }

    [Fact]
    public async Task RejectUserAsync_RecordsAuditEvent()
    {
        var world = await NewPendingOrganizationAsync("PR3");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await service.RejectUserAsync(actor, world.AdminId);

        var events = await world.Context.PlatformAuditEvents.Where(e => e.TargetUserId == world.AdminId).ToListAsync();
        var rejectionEvent = Assert.Single(events);
        Assert.Equal(PlatformEventType.UserRejected, rejectionEvent.EventType);
        Assert.Equal(actor.UserId, rejectionEvent.ActorUserId);
    }

    [Fact] // spec §18: an already-rejected account is an idempotent no-op, not a failure.
    public async Task RejectUserAsync_AlreadyRejected_IsIdempotent()
    {
        var world = await NewPendingOrganizationAsync("PR4");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        await service.RejectUserAsync(actor, world.AdminId);

        var result = await service.RejectUserAsync(actor, world.AdminId);

        Assert.True(result.Succeeded);
        var events = await world.Context.PlatformAuditEvents.Where(e => e.TargetUserId == world.AdminId).ToListAsync();
        Assert.Single(events); // no duplicate audit row from the idempotent second call
    }

    [Fact] // spec §18: an Active account cannot be "rejected" — Reject is initial review only.
    public async Task RejectUserAsync_Active_IsRejected()
    {
        var world = await NewOrganizationAsync("PR5"); // already approved by the fixture
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.RejectUserAsync(actor, world.AdminId);

        Assert.False(result.Succeeded);
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Active, detail!.Status); // unchanged
    }

    [Fact] // spec §18: an Inactive account cannot be "rejected" either.
    public async Task RejectUserAsync_Inactive_IsRejected()
    {
        var world = await NewOrganizationAsync("PR6");
        var secondAdminId = await AddMemberAsync(world, "Second Admin", UserRole.Admin);
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        await service.DeactivateUserAsync(actor, secondAdminId);

        var result = await service.RejectUserAsync(actor, secondAdminId);

        Assert.False(result.Succeeded);
        var detail = await service.GetUserDetailAsync(secondAdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Inactive, detail!.Status); // unchanged
    }

    [Fact]
    public async Task RejectUserAsync_UnknownUser_Throws()
    {
        var world = await NewOrganizationAsync("PR7");
        var actor = PlatformAdmin(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<PlatformAccessDeniedException>(() => service.RejectUserAsync(actor, Guid.NewGuid()));
    }

    [Fact] // spec §14/§18: Rejected must never be conflated with Inactive — neither Reactivate nor
           // Deactivate may act on a Rejected account.
    public async Task RejectedAccount_CannotBeDeactivatedOrReactivated()
    {
        var world = await NewPendingOrganizationAsync("PR8");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));
        await service.RejectUserAsync(actor, world.AdminId);

        var deactivateResult = await service.DeactivateUserAsync(actor, world.AdminId);
        var reactivateResult = await service.ReactivateUserAsync(actor, world.AdminId);

        Assert.False(deactivateResult.Succeeded);
        Assert.False(reactivateResult.Succeeded);
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Rejected, detail!.Status); // unchanged throughout
    }

    // ---------------------------------------------------------------------------------------
    // Phase 24A-Extension (ADR-0025) §19: approval/rejection concurrency
    // ---------------------------------------------------------------------------------------

    [Fact] // Two Platform Admins racing Approve then Reject on the same Pending account: whichever
           // commits first wins outright; the second call must never silently corrupt that outcome.
    public async Task ApproveThenReject_OnSameAccount_SecondCallFailsSafely_FirstOutcomeStands()
    {
        var world = await NewPendingOrganizationAsync("PC1");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var approveResult = await service.ApproveUserAsync(actor, world.AdminId);
        var rejectResult = await service.RejectUserAsync(actor, world.AdminId);

        Assert.True(approveResult.Succeeded);
        Assert.False(rejectResult.Succeeded); // Active cannot be rejected — the account ends in exactly one valid state.
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Active, detail!.Status);
    }

    [Fact] // The reverse order: Reject commits first, Approve must not be able to override it.
    public async Task RejectThenApprove_OnSameAccount_SecondCallFailsSafely_FirstOutcomeStands()
    {
        var world = await NewPendingOrganizationAsync("PC2");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var rejectResult = await service.RejectUserAsync(actor, world.AdminId);
        var approveResult = await service.ApproveUserAsync(actor, world.AdminId);

        Assert.True(rejectResult.Succeeded);
        Assert.False(approveResult.Succeeded);
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Rejected, detail!.Status);
    }

    [Fact] // A genuine race: both admins read the same Pending row before either commits — Identity's
           // own ConcurrencyStamp must stop the second UpdateAsync from silently overwriting the first.
    public async Task ConcurrentApproveAndReject_OnStaleReads_SecondUpdateFailsConcurrencyCheck()
    {
        var world = await NewPendingOrganizationAsync("PC3");
        var actor = await PlatformOperatorAsync(world);
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(Now));

        // Two independent DbContexts against the same database, simulating two Platform Admins who
        // both loaded the same Pending user before either mutation committed.
        var contextA = _fixture.CreateContext();
        var servicesA = new ServiceCollection();
        servicesA.AddSingleton(contextA);
        servicesA.AddLogging();
        servicesA.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManagerA = servicesA.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
        var serviceA = new PlatformUserService(contextA, userManagerA, new TicketTestData.FixedTimeProvider(Now));

        var contextB = _fixture.CreateContext();
        var servicesB = new ServiceCollection();
        servicesB.AddSingleton(contextB);
        servicesB.AddLogging();
        servicesB.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManagerB = servicesB.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
        var serviceB = new PlatformUserService(contextB, userManagerB, new TicketTestData.FixedTimeProvider(Now));

        var approveResult = await serviceA.ApproveUserAsync(actor, world.AdminId);
        var rejectResult = await serviceB.RejectUserAsync(actor, world.AdminId);

        Assert.True(approveResult.Succeeded);
        Assert.False(rejectResult.Succeeded); // ConcurrencyStamp mismatch — never a silent overwrite.
        var detail = await service.GetUserDetailAsync(world.AdminId);
        Assert.Equal(FlowOps.Domain.Accounts.AccountStatus.Active, detail!.Status); // exactly one valid final state
    }

    // ---------------------------------------------------------------------------------------
    // Phase 24B: the Platform homepage's own queries
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingAccountsAsync_ReturnsPendingAccountsWithOrganizationContext()
    {
        var pendingWorld = await NewPendingOrganizationAsync("HomePendingList");
        var service = new PlatformUserService(pendingWorld.Context, pendingWorld.UserManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetPendingAccountsAsync(take: 1000);

        var item = Assert.Single(result, x => x.UserId == pendingWorld.AdminId);
        Assert.Equal($"HomePendingList Org", item.OrganizationName);
        Assert.Equal(Now, item.RegisteredAt);
    }

    [Fact]
    public async Task GetPendingAccountsAsync_NeverExceedsTake()
    {
        await NewPendingOrganizationAsync("HomePendingBoundA");
        await NewPendingOrganizationAsync("HomePendingBoundB");
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
        var service = new PlatformUserService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetPendingAccountsAsync(take: 1);

        Assert.True(result.Count <= 1);
    }

    [Fact]
    public async Task GetPendingAccountsAsync_ApprovedAccountNoLongerAppears()
    {
        var pendingWorld = await NewPendingOrganizationAsync("HomePendingApprove");
        var operatorIdentity = await PlatformOperatorAsync(pendingWorld);
        var service = new PlatformUserService(pendingWorld.Context, pendingWorld.UserManager, new TicketTestData.FixedTimeProvider(Now));

        await service.ApproveUserAsync(operatorIdentity, pendingWorld.AdminId);
        var result = await service.GetPendingAccountsAsync(take: 1000);

        Assert.DoesNotContain(result, x => x.UserId == pendingWorld.AdminId);
    }

    [Fact]
    public async Task GetRecentUsersAsync_OrdersByMostRecentMembership_AndNeverExceedsTake()
    {
        var world = await NewOrganizationAsync("HomeRecentUsers");
        var later = Now.AddMinutes(5); // strictly after the founder's own registration-time membership
        var laterMember = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@platformuserservice.test.local",
            Email = $"{Guid.NewGuid():N}@platformuserservice.test.local",
            EmailConfirmed = true,
            DisplayName = "Later Member",
            IsActive = true,
            RegistrationApprovedAt = later,
        };
        await world.UserManager.CreateAsync(laterMember, Password);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, laterMember.Id, UserRole.Agent, later));
        await world.Context.SaveChangesAsync();
        var service = new PlatformUserService(world.Context, world.UserManager, new TicketTestData.FixedTimeProvider(later));

        // A large take rather than take:1 — the shared Postgres test database may hold other,
        // real-clock-timestamped memberships from unrelated test classes, so this asserts the
        // relative ordering between these two specific users, never a global "top of everyone" claim.
        var result = await service.GetRecentUsersAsync(take: 1000);

        var items = result.ToList();
        var laterIndex = items.FindIndex(u => u.UserId == laterMember.Id);
        var founderIndex = items.FindIndex(u => u.UserId == world.AdminId);
        Assert.True(laterIndex >= 0, "The later-joined member was not returned.");
        Assert.True(founderIndex >= 0, "The founder was not returned.");
        Assert.True(laterIndex < founderIndex, "A more recently joined member must rank ahead of an earlier one.");
    }

    [Fact]
    public async Task GetRecentUsersAsync_NeverExceedsTake()
    {
        await NewOrganizationAsync("HomeRecentUsersBound");
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
        var service = new PlatformUserService(context, userManager, new TicketTestData.FixedTimeProvider(Now));

        var result = await service.GetRecentUsersAsync(take: 2);

        Assert.True(result.Count <= 2);
    }

    private static PlatformAdminIdentity PlatformAdmin(World world) => new(world.AdminId, "Platform Operator");

    /// <summary>A real, already-approved <c>ApplicationUser</c> row to satisfy
    /// <c>platform_audit_events.actor_user_id</c>'s real FK — <see cref="PlatformAdmin"/>'s
    /// synthetic id only ever worked for tests that never actually wrote an audit row.</summary>
    private async Task<PlatformAdminIdentity> PlatformOperatorAsync(World world)
    {
        var operatorId = await TicketTestData.AddUserAsync(world.Context, "Platform Operator");
        return new PlatformAdminIdentity(operatorId, "Platform Operator");
    }

    private async Task<Guid> AddMemberAsync(World world, string displayName, UserRole role)
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@platformuserservice.test.local",
            Email = $"{Guid.NewGuid():N}@platformuserservice.test.local",
            EmailConfirmed = true,
            DisplayName = displayName,
            IsActive = true,
            RegistrationApprovedAt = Now,
        };
        await world.UserManager.CreateAsync(user, Password);
        world.Context.OrganizationMemberships.Add(new OrganizationMembership(0, world.OrganizationId, user.Id, role, Now));
        await world.Context.SaveChangesAsync();
        return user.Id;
    }

    private async Task<World> NewOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@platformuserservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        // Phase 24A (ADR-0024): self-registration alone leaves the founder Pending. This fixture
        // exists to exercise platform user *lifecycle* (approve/deactivate/reactivate) as an
        // already-usable "another Admin" backdrop, never the registration-approval gate itself
        // (which has its own dedicated tests) — approved immediately, the same shortcut
        // TicketTestData.AddUserAsync already takes.
        var founder = await context.Users.SingleAsync(u => u.Id == registration.UserId!.Value);
        founder.RegistrationApprovedAt = Now;
        await context.SaveChangesAsync();

        return new World(context, userManager, registration.UserId!.Value, registration.OrganizationId!.Value);
    }

    /// <summary>The genuine, unmodified result of self-registration (ADR-0024): Pending, locked
    /// out, RegistrationApprovedAt still null — for tests of the approval gate itself.</summary>
    private async Task<World> NewPendingOrganizationAsync(string label)
    {
        var context = _fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        var userManager = services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest($"{label} Admin", $"{Guid.NewGuid():N}@platformuserservice.test.local", Password, $"{label} Org"));
        Assert.True(registration.Succeeded);

        return new World(context, userManager, registration.UserId!.Value, registration.OrganizationId!.Value);
    }
}
