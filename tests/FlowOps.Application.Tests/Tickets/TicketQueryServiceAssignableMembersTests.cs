using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// ADR-0027: <see cref="TicketQueryService.GetAssignableTeamMembersAsync"/> — the "assign to
/// someone else" picker's data source. Authorization here mirrors
/// <see cref="TicketAccessPolicy.CanAssign"/>'s own Admin/Manager branches directly, so these
/// tests exist to prove the two never drift apart.
/// </summary>
[Collection("Postgres")]
public sealed class TicketQueryServiceAssignableMembersTests
{
    private readonly PostgresFixture _fixture;

    public TicketQueryServiceAssignableMembersTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Admin_SeesAllActiveMembersOfTheTeam_OrderedByName()
    {
        await using var context = _fixture.CreateContext();
        var teamId = await TicketTestData.AddTeamAsync(context);
        var zed = await TicketTestData.AddUserAsync(context, "Zed Agent");
        var alice = await TicketTestData.AddUserAsync(context, "Alice Agent");
        await TicketTestData.AddTeamMembershipAsync(context, teamId, zed);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, alice);
        var admin = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Admin);
        var service = new TicketQueryService(context, TimeProvider.System);

        var members = await service.GetAssignableTeamMembersAsync(admin, teamId);

        Assert.Equal(["Alice Agent", "Zed Agent"], members.Select(m => m.DisplayName));
    }

    [Fact]
    public async Task Admin_ExcludesInactiveMembers()
    {
        await using var context = _fixture.CreateContext();
        var teamId = await TicketTestData.AddTeamAsync(context);
        var inactiveId = await TicketTestData.AddUserAsync(context, "Inactive Agent");
        await TicketTestData.AddTeamMembershipAsync(context, teamId, inactiveId);
        var inactiveUser = await context.Users.SingleAsync(u => u.Id == inactiveId);
        inactiveUser.IsActive = false;
        await context.SaveChangesAsync();
        var admin = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Admin);
        var service = new TicketQueryService(context, TimeProvider.System);

        var members = await service.GetAssignableTeamMembersAsync(admin, teamId);

        Assert.Empty(members);
    }

    [Fact]
    public async Task Manager_OfThisTeam_SeesItsMembers()
    {
        await using var context = _fixture.CreateContext();
        var teamId = await TicketTestData.AddTeamAsync(context);
        var agentId = await TicketTestData.AddUserAsync(context, "An Agent");
        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        var managerId = await TicketTestData.AddUserAsync(context);
        var manager = TicketTestData.Manager(managerId, teamId);
        var service = new TicketQueryService(context, TimeProvider.System);

        var members = await service.GetAssignableTeamMembersAsync(manager, teamId);

        Assert.Contains(members, m => m.UserId == agentId);
    }

    [Fact]
    public async Task Manager_OfADifferentTeam_IsDenied()
    {
        await using var context = _fixture.CreateContext();
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var manager = TicketTestData.Manager(await TicketTestData.AddUserAsync(context), otherTeamId);
        var service = new TicketQueryService(context, TimeProvider.System);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => service.GetAssignableTeamMembersAsync(manager, teamId));
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public async Task AgentOrViewer_IsAlwaysDenied(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var teamId = await TicketTestData.AddTeamAsync(context);
        var actor = TicketTestData.User(await TicketTestData.AddUserAsync(context), role, teamId);
        var service = new TicketQueryService(context, TimeProvider.System);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => service.GetAssignableTeamMembersAsync(actor, teamId));
    }

    [Fact] // Never a client-trusted id — a team from a different organization must be refused
           // even for an Admin.
    public async Task Admin_TeamFromAnotherOrganization_IsDenied()
    {
        await using var context = _fixture.CreateContext();
        var (_, otherOrgTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var admin = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Admin);
        var service = new TicketQueryService(context, TimeProvider.System);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() => service.GetAssignableTeamMembersAsync(admin, otherOrgTeamId));
    }
}
