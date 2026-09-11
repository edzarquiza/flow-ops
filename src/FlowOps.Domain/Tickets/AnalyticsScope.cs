namespace FlowOps.Domain.Tickets;

/// <summary>
/// Which shape of scope <see cref="TicketAccessPolicy.GetAnalyticsScope"/> resolved for a caller.
/// A discriminator rather than a single predicate because the AUTH-RULE-02 "Team analytics" row
/// gives each role a genuinely different shape of scope, not just a different team set — see
/// <see cref="AnalyticsScope"/>.
/// </summary>
public enum AnalyticsScopeKind
{
    /// <summary>Admin: every ticket, no filter.</summary>
    AllTeams,

    /// <summary>Manager: tickets on teams the caller manages (<c>ManagedTeamIds</c>).</summary>
    ManagedTeams,

    /// <summary>Viewer: tickets on teams the caller belongs to (<c>MemberTeamIds</c>).</summary>
    MemberTeams,

    /// <summary>
    /// Agent: only tickets currently assigned to the caller — deliberately narrower than
    /// <see cref="TicketAccessPolicy.CanView"/>'s team-wide scope for the same role, because an
    /// aggregate count must not disclose team-wide data an Agent cannot otherwise see (AUTH-RULE-02's
    /// "Team analytics: Agent → own workload only", distinct from its "View tickets: Agent → own
    /// teams" row).
    /// </summary>
    OwnAssignedTicketsOnly,
}

/// <summary>
/// Rule AUTH-RULE-02, "Team analytics" row (Phase 10): the population of tickets a caller's
/// dashboard/analytics queries may aggregate over. Resolved once by
/// <see cref="TicketAccessPolicy.GetAnalyticsScope"/> and translated to SQL by the Application
/// layer — this type carries no query logic itself, only the decision of which shape applies.
/// </summary>
/// <param name="Kind">Which of the four shapes above applies.</param>
/// <param name="TeamIds">Populated only for <see cref="AnalyticsScopeKind.ManagedTeams"/> and
/// <see cref="AnalyticsScopeKind.MemberTeams"/>; empty otherwise.</param>
/// <param name="AssigneeId">Populated only for <see cref="AnalyticsScopeKind.OwnAssignedTicketsOnly"/>;
/// null otherwise.</param>
public sealed record AnalyticsScope(AnalyticsScopeKind Kind, IReadOnlySet<int> TeamIds, Guid? AssigneeId);
