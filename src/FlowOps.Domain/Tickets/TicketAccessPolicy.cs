namespace FlowOps.Domain.Tickets;

/// <summary>
/// Rule AUTH-RULE-04: the single authoritative resource-authorization decision surface for
/// tickets. Every application service must call this before mutating or returning a ticket;
/// Razor Pages and the API must call the exact same methods — never a second, "simpler" check
/// (see the flowops-authorization skill). Implements the capability matrix at AUTH-RULE-02.
/// </summary>
public static class TicketAccessPolicy
{
    /// <summary>
    /// AUTH-RULE-02 "Create ticket" row: Admin, Manager, and Agent may create; Viewer may not.
    /// Unlike every other method here there is no <see cref="TicketAuthorizationSnapshot"/> to
    /// judge against — the ticket does not exist yet — so the decision is role-only. Team scoping
    /// does not apply either: the matrix conditions this row purely on role.
    /// </summary>
    public static bool CanCreate(CurrentUser user) =>
        user.Role is UserRole.Admin or UserRole.Manager or UserRole.Agent;

    /// <summary>AUTH-RULE-02 "View tickets" row.</summary>
    public static bool CanView(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role == UserRole.Admin || user.MemberTeamIds.Contains(ticket.TeamId);

    /// <summary>AUTH-RULE-02 "Comment (public)" row.</summary>
    public static bool CanComment(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role != UserRole.Viewer && CanView(ticket, user);

    /// <summary>
    /// TICKET-ENT-05 / AUTH-RULE-02 "See internal comments" row. Independent of team membership —
    /// the rule text conditions this purely on role.
    /// </summary>
    public static bool CanSeeInternalComments(CurrentUser user) =>
        user.Role != UserRole.Viewer;

    /// <summary>AUTH-RULE-02 "Assign / reassign" row.</summary>
    public static bool CanAssign(TicketAuthorizationSnapshot ticket, CurrentUser user, Guid targetAssigneeId) =>
        user.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => user.ManagedTeamIds.Contains(ticket.TeamId),
            UserRole.Agent => user.MemberTeamIds.Contains(ticket.TeamId) && targetAssigneeId == user.UserId,
            _ => false,
        };

    /// <summary>
    /// AUTH-RULE-02 "Transition status" row. Also gates "Change priority" — the matrix gives
    /// that row an identical Admin/Manager-own-teams/Agent-own-assigned-tickets/Viewer-none
    /// footprint, and docs/domain-model.md names only six policy methods (AUTH-RULE-04), so no
    /// separate CanChangePriority method is introduced.
    /// </summary>
    public static bool CanTransition(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => user.ManagedTeamIds.Contains(ticket.TeamId),
            UserRole.Agent => ticket.AssigneeId == user.UserId,
            _ => false,
        };

    /// <summary>
    /// TICKET-WF-08, whose rule text names this policy as the owner of "who may close": a
    /// Manager or Admin, or the requester confirming their own ticket is done.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="CanTransition"/>. Closing is the one transition
    /// whose permitted actors differ from the generic "Transition status" matrix row: that row
    /// gives an Agent their <em>assigned</em> tickets, while TICKET-WF-08 gives the
    /// <em>requester</em> the close — and <see cref="Ticket.Close"/> enforces exactly that, so a
    /// generic transition check here would authorize an assignee the aggregate then rejects.
    /// It currently evaluates identically to <see cref="CanReopen"/>; they are kept separate
    /// because they implement two different documented rules that merely agree today, and
    /// collapsing them would let a change to one silently redefine the other (CLAUDE.md §2 rule 7).
    /// </remarks>
    /// <summary>Planning a ticket into/out of a sprint (ADR-0029): Admin, or a Manager of the
    /// ticket's team — the same authority as reassigning it. Agents and Viewers may not.</summary>
    public static bool CanPlan(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => user.ManagedTeamIds.Contains(ticket.TeamId),
            _ => false,
        };

    public static bool CanClose(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => user.ManagedTeamIds.Contains(ticket.TeamId),
            UserRole.Agent => ticket.RequesterId == user.UserId,
            _ => false,
        };

    /// <summary>AUTH-RULE-02 "Reopen" row.</summary>
    public static bool CanReopen(TicketAuthorizationSnapshot ticket, CurrentUser user) =>
        user.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => user.ManagedTeamIds.Contains(ticket.TeamId),
            UserRole.Agent => ticket.RequesterId == user.UserId,
            _ => false,
        };

    /// <summary>
    /// AUTH-RULE-02 "Team analytics" row (Phase 10). Deliberately not <see cref="CanView"/>'s
    /// scope: that row gives every non-Admin role the same "member of team" breadth, but this row
    /// gives Agent a narrower one — "own workload only" — because an aggregate count computed
    /// over a whole team would disclose team-wide data to an Agent the capability matrix does not
    /// grant them. Manager's "own teams" here follows <see cref="CurrentUser.ManagedTeamIds"/>'s
    /// own documented purpose ("drives the 'Manager, own teams' scoping in the AUTH-RULE-02
    /// capability matrix"), matching every other Manager-scoped row in this class rather than
    /// <see cref="CanView"/>'s <c>MemberTeamIds</c>.
    /// </summary>
    public static AnalyticsScope GetAnalyticsScope(CurrentUser user) =>
        user.Role switch
        {
            UserRole.Admin => new AnalyticsScope(AnalyticsScopeKind.AllTeams, new HashSet<int>(), null),
            UserRole.Manager => new AnalyticsScope(AnalyticsScopeKind.ManagedTeams, user.ManagedTeamIds, null),
            UserRole.Agent => new AnalyticsScope(AnalyticsScopeKind.OwnAssignedTicketsOnly, new HashSet<int>(), user.UserId),
            UserRole.Viewer => new AnalyticsScope(AnalyticsScopeKind.MemberTeams, user.MemberTeamIds, null),
            _ => throw new ArgumentOutOfRangeException(nameof(user), user.Role, "Unrecognized role."),
        };

    /// <summary>
    /// Team Workload's own authorization question (ADR-0036): does <paramref name="user"/>'s
    /// existing Team Analytics scope (<see cref="GetAnalyticsScope"/>, the same "Team analytics"
    /// AUTH-RULE-02 row every other analytics view already uses) include <paramref name="teamId"/>?
    /// Deliberately reuses that scope rather than <see cref="DirectoryAccessPolicy.CanManageTeams"/>
    /// (the Admin-only gate <c>TeamService.GetTeamDetailAsync</c> uses) — read-only workload
    /// visibility is not team-management authority, and Team Workload must never grant broader
    /// access than the analytics scope it otherwise already follows. Agent's scope has no team
    /// concept at all (their analytics scope is "own assigned tickets only"), so no team id ever
    /// qualifies for Agent.
    /// </summary>
    public static bool CanViewTeamWorkload(CurrentUser user, int teamId)
    {
        var scope = GetAnalyticsScope(user);
        return scope.Kind switch
        {
            AnalyticsScopeKind.AllTeams => true,
            AnalyticsScopeKind.ManagedTeams or AnalyticsScopeKind.MemberTeams => scope.TeamIds.Contains(teamId),
            AnalyticsScopeKind.OwnAssignedTicketsOnly => false,
            _ => false,
        };
    }
}
