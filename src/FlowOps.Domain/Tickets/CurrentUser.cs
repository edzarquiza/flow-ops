namespace FlowOps.Domain.Tickets;

/// <summary>
/// The calling user's identity, role, and team relationships, as needed by
/// <see cref="TicketAccessPolicy"/> (AUTH-RULE-04). <paramref name="ManagedTeamIds"/> is the
/// subset of <paramref name="MemberTeamIds"/> for which the user is flagged as team manager
/// (see docs/database.md §3, <c>team_members.is_team_manager</c>) — it drives the "Manager, own
/// teams" scoping in the AUTH-RULE-02 capability matrix.
/// </summary>
public sealed record CurrentUser(
    Guid UserId,
    UserRole Role,
    IReadOnlySet<int> MemberTeamIds,
    IReadOnlySet<int> ManagedTeamIds);
