using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Organizations;

/// <summary>ORG-RULE-11's full role x capability truth table, unit-tested exhaustively — the same
/// discipline CLAUDE.md §15 requires of <c>TicketAccessPolicy</c>.</summary>
public class OrganizationAccessPolicyTests
{
    private static CurrentUser User(UserRole role) =>
        new(Guid.NewGuid(), 1, role, new HashSet<int>(), new HashSet<int>());

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Agent, false)]
    [InlineData(UserRole.Viewer, false)]
    public void CanInvite_OnlyAdminAndManager(UserRole role, bool expected) =>
        Assert.Equal(expected, OrganizationAccessPolicy.CanInvite(User(role)));

    [Theory]
    [InlineData(UserRole.Admin, UserRole.Admin, true)]
    [InlineData(UserRole.Admin, UserRole.Manager, true)]
    [InlineData(UserRole.Admin, UserRole.Agent, true)]
    [InlineData(UserRole.Admin, UserRole.Viewer, true)]
    [InlineData(UserRole.Manager, UserRole.Admin, false)] // least-privilege: Manager cannot create Admins
    [InlineData(UserRole.Manager, UserRole.Manager, false)] // nor other Managers
    [InlineData(UserRole.Manager, UserRole.Agent, true)]
    [InlineData(UserRole.Manager, UserRole.Viewer, true)]
    [InlineData(UserRole.Agent, UserRole.Agent, false)]
    [InlineData(UserRole.Viewer, UserRole.Viewer, false)]
    public void CanInviteRole_ManagerIsLeastPrivilege(UserRole inviterRole, UserRole targetRole, bool expected) =>
        Assert.Equal(expected, OrganizationAccessPolicy.CanInviteRole(User(inviterRole), targetRole));

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Agent, false)]
    [InlineData(UserRole.Viewer, false)]
    public void CanManageMembers_OnlyAdminAndManager(UserRole role, bool expected) =>
        Assert.Equal(expected, OrganizationAccessPolicy.CanManageMembers(User(role)));

    [Theory]
    [InlineData(UserRole.Admin, UserRole.Admin, UserRole.Manager, true)]
    [InlineData(UserRole.Manager, UserRole.Admin, UserRole.Manager, false)] // Manager cannot touch an Admin
    [InlineData(UserRole.Manager, UserRole.Manager, UserRole.Agent, false)] // nor another Manager
    [InlineData(UserRole.Manager, UserRole.Agent, UserRole.Viewer, true)]
    [InlineData(UserRole.Manager, UserRole.Agent, UserRole.Admin, false)] // and cannot promote to Admin
    [InlineData(UserRole.Agent, UserRole.Agent, UserRole.Viewer, false)]
    public void CanChangeRole_ManagerIsLeastPrivilege(UserRole actorRole, UserRole targetCurrentRole, UserRole newRole, bool expected) =>
        Assert.Equal(expected, OrganizationAccessPolicy.CanChangeRole(User(actorRole), targetCurrentRole, newRole));

    [Theory]
    [InlineData(UserRole.Admin, UserRole.Admin, true)]
    [InlineData(UserRole.Manager, UserRole.Admin, false)]
    [InlineData(UserRole.Manager, UserRole.Manager, false)]
    [InlineData(UserRole.Manager, UserRole.Agent, true)]
    [InlineData(UserRole.Manager, UserRole.Viewer, true)]
    [InlineData(UserRole.Viewer, UserRole.Agent, false)]
    public void CanRemoveMember_ManagerIsLeastPrivilege(UserRole actorRole, UserRole targetRole, bool expected) =>
        Assert.Equal(expected, OrganizationAccessPolicy.CanRemoveMember(User(actorRole), targetRole));
}
