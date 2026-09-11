using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Tickets;

/// <summary>
/// Rule AUTH-RULE-02 / AUTH-RULE-04: the full role × capability truth table, per CLAUDE.md §15's
/// instruction that TicketAccessPolicy be "unit-tested exhaustively as a truth table."
/// </summary>
public class TicketAccessPolicyTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Assignee = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();
    private const int Team = 1;
    private const int OtherTeam = 2;

    private static TicketAuthorizationSnapshot Snapshot(Guid? assignee = null) =>
        new(TicketId: 1, TeamId: Team, RequesterId: Requester, AssigneeId: assignee ?? Assignee, Status: Status.Assigned);

    private static CurrentUser User(UserRole role, bool memberOfTeam = true, bool managesTeam = false, Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            role,
            memberOfTeam ? new HashSet<int> { Team } : new HashSet<int>(),
            managesTeam ? new HashSet<int> { Team } : new HashSet<int>());

    // ---- CanCreate ----

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    public void CanCreate_AdminManagerAgent_True(UserRole role) =>
        Assert.True(TicketAccessPolicy.CanCreate(User(role)));

    [Fact]
    public void CanCreate_Viewer_False() =>
        Assert.False(TicketAccessPolicy.CanCreate(User(UserRole.Viewer)));

    [Theory] // role-only per the matrix: team membership does not gate creation
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    public void CanCreate_OutsideAnyTeam_StillTrue(UserRole role) =>
        Assert.True(TicketAccessPolicy.CanCreate(User(role, memberOfTeam: false)));

    // ---- CanClose (TICKET-WF-08) ----

    [Fact]
    public void CanClose_Admin_True() =>
        Assert.True(TicketAccessPolicy.CanClose(Snapshot(), User(UserRole.Admin, memberOfTeam: false)));

    [Fact]
    public void CanClose_ManagerOfTheTeam_True() =>
        Assert.True(TicketAccessPolicy.CanClose(Snapshot(), User(UserRole.Manager, managesTeam: true)));

    [Fact]
    public void CanClose_ManagerOfAnotherTeam_False() =>
        Assert.False(TicketAccessPolicy.CanClose(Snapshot(), User(UserRole.Manager, managesTeam: false)));

    [Fact] // "or by the requester confirming"
    public void CanClose_AgentWhoIsTheRequester_True() =>
        Assert.True(TicketAccessPolicy.CanClose(Snapshot(), User(UserRole.Agent, id: Requester)));

    /// <summary>
    /// The case that separates CanClose from CanTransition: an Agent who owns the ticket as
    /// assignee may drive it through the workflow, but TICKET-WF-08 reserves the close itself for
    /// a Manager/Admin or the requester — and <c>Ticket.Close</c> enforces exactly that, so the
    /// policy must agree or it would authorize a call the aggregate then rejects.
    /// </summary>
    [Fact]
    public void CanClose_AgentWhoIsOnlyTheAssignee_False()
    {
        var user = User(UserRole.Agent, id: Assignee);

        Assert.True(TicketAccessPolicy.CanTransition(Snapshot(), user));
        Assert.False(TicketAccessPolicy.CanClose(Snapshot(), user));
    }

    [Fact]
    public void CanClose_Viewer_False() =>
        Assert.False(TicketAccessPolicy.CanClose(Snapshot(), User(UserRole.Viewer)));

    // ---- CanView ----

    [Fact]
    public void CanView_Admin_AlwaysTrue_EvenOutsideTeam() =>
        Assert.True(TicketAccessPolicy.CanView(Snapshot(), User(UserRole.Admin, memberOfTeam: false)));

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public void CanView_NonAdminMemberOfTeam_True(UserRole role) =>
        Assert.True(TicketAccessPolicy.CanView(Snapshot(), User(role, memberOfTeam: true)));

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Viewer)]
    public void CanView_NonAdminOutsideTeam_False(UserRole role) =>
        Assert.False(TicketAccessPolicy.CanView(Snapshot(), User(role, memberOfTeam: false)));

    // ---- CanComment ----

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Agent, true)]
    [InlineData(UserRole.Viewer, false)]
    public void CanComment_ByRole_MemberOfTeam(UserRole role, bool expected) =>
        Assert.Equal(expected, TicketAccessPolicy.CanComment(Snapshot(), User(role, memberOfTeam: true)));

    [Fact]
    public void CanComment_AgentOutsideTeam_False() =>
        Assert.False(TicketAccessPolicy.CanComment(Snapshot(), User(UserRole.Agent, memberOfTeam: false)));

    // ---- CanSeeInternalComments ----

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Agent, true)]
    [InlineData(UserRole.Viewer, false)]
    public void CanSeeInternalComments_ByRole(UserRole role, bool expected) =>
        Assert.Equal(expected, TicketAccessPolicy.CanSeeInternalComments(User(role)));

    // ---- CanAssign ----

    [Fact]
    public void CanAssign_Admin_AlwaysTrue() =>
        Assert.True(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Admin), Stranger));

    [Fact]
    public void CanAssign_ManagerOfTeam_True() =>
        Assert.True(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Manager, managesTeam: true), Stranger));

    [Fact]
    public void CanAssign_ManagerNotManagingTeam_False() =>
        Assert.False(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Manager, managesTeam: false), Stranger));

    [Fact]
    public void CanAssign_AgentSelfAssigning_True()
    {
        var agentId = Guid.NewGuid();
        Assert.True(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Agent, id: agentId), agentId));
    }

    [Fact]
    public void CanAssign_AgentAssigningSomeoneElse_False()
    {
        var agentId = Guid.NewGuid();
        Assert.False(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Agent, id: agentId), Stranger));
    }

    [Fact]
    public void CanAssign_Viewer_AlwaysFalse() =>
        Assert.False(TicketAccessPolicy.CanAssign(Snapshot(), User(UserRole.Viewer), Stranger));

    // ---- CanTransition (also gates "Change priority" — see policy doc comment) ----

    [Fact]
    public void CanTransition_Admin_AlwaysTrue() =>
        Assert.True(TicketAccessPolicy.CanTransition(Snapshot(), User(UserRole.Admin)));

    [Fact]
    public void CanTransition_ManagerOfTeam_True() =>
        Assert.True(TicketAccessPolicy.CanTransition(Snapshot(), User(UserRole.Manager, managesTeam: true)));

    [Fact]
    public void CanTransition_ManagerNotManagingTeam_False() =>
        Assert.False(TicketAccessPolicy.CanTransition(Snapshot(), User(UserRole.Manager, managesTeam: false)));

    [Fact]
    public void CanTransition_AssignedAgent_True()
    {
        var snapshot = Snapshot(Assignee);
        Assert.True(TicketAccessPolicy.CanTransition(snapshot, User(UserRole.Agent, id: Assignee)));
    }

    [Fact]
    public void CanTransition_UnassignedAgent_False()
    {
        var snapshot = Snapshot(Assignee);
        Assert.False(TicketAccessPolicy.CanTransition(snapshot, User(UserRole.Agent, id: Stranger)));
    }

    [Fact]
    public void CanTransition_Viewer_AlwaysFalse() =>
        Assert.False(TicketAccessPolicy.CanTransition(Snapshot(), User(UserRole.Viewer)));

    // ---- CanReopen ----

    [Fact]
    public void CanReopen_Admin_AlwaysTrue() =>
        Assert.True(TicketAccessPolicy.CanReopen(Snapshot(), User(UserRole.Admin)));

    [Fact]
    public void CanReopen_ManagerOfTeam_True() =>
        Assert.True(TicketAccessPolicy.CanReopen(Snapshot(), User(UserRole.Manager, managesTeam: true)));

    [Fact]
    public void CanReopen_RequesterAgent_True()
    {
        var snapshot = new TicketAuthorizationSnapshot(1, Team, Requester, Assignee, Status.Resolved);
        Assert.True(TicketAccessPolicy.CanReopen(snapshot, User(UserRole.Agent, id: Requester)));
    }

    [Fact]
    public void CanReopen_NonRequesterAgent_False()
    {
        var snapshot = new TicketAuthorizationSnapshot(1, Team, Requester, Assignee, Status.Resolved);
        Assert.False(TicketAccessPolicy.CanReopen(snapshot, User(UserRole.Agent, id: Stranger)));
    }

    [Fact]
    public void CanReopen_Viewer_AlwaysFalse() =>
        Assert.False(TicketAccessPolicy.CanReopen(Snapshot(), User(UserRole.Viewer)));

    // ---- GetAnalyticsScope (Phase 10) ----

    [Fact]
    public void GetAnalyticsScope_Admin_IsAllTeams() =>
        Assert.Equal(AnalyticsScopeKind.AllTeams, TicketAccessPolicy.GetAnalyticsScope(User(UserRole.Admin)).Kind);

    [Fact] // Manager's "own teams" here is ManagedTeamIds, matching every other Manager-scoped row.
    public void GetAnalyticsScope_Manager_IsManagedTeams()
    {
        var user = User(UserRole.Manager, memberOfTeam: true, managesTeam: true);

        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        Assert.Equal(AnalyticsScopeKind.ManagedTeams, scope.Kind);
        Assert.Equal(user.ManagedTeamIds, scope.TeamIds);
    }

    [Fact] // A Manager who merely belongs to a team, without managing it, gets no scope from it.
    public void GetAnalyticsScope_ManagerOfNoTeam_HasEmptyScope()
    {
        var user = User(UserRole.Manager, memberOfTeam: true, managesTeam: false);

        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        Assert.Equal(AnalyticsScopeKind.ManagedTeams, scope.Kind);
        Assert.Empty(scope.TeamIds);
    }

    [Fact] // The single rule this whole capability exists to enforce: Agent gets their own
           // assignments only, never CanView's team-wide "member of team" breadth.
    public void GetAnalyticsScope_Agent_IsOwnAssignedTicketsOnly_NotTeamWide()
    {
        var agentId = Guid.NewGuid();
        var user = User(UserRole.Agent, memberOfTeam: true, id: agentId);

        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        Assert.Equal(AnalyticsScopeKind.OwnAssignedTicketsOnly, scope.Kind);
        Assert.Equal(agentId, scope.AssigneeId);
        Assert.Empty(scope.TeamIds);
    }

    [Fact]
    public void GetAnalyticsScope_Viewer_IsMemberTeams()
    {
        var user = User(UserRole.Viewer, memberOfTeam: true);

        var scope = TicketAccessPolicy.GetAnalyticsScope(user);

        Assert.Equal(AnalyticsScopeKind.MemberTeams, scope.Kind);
        Assert.Equal(user.MemberTeamIds, scope.TeamIds);
    }
}
