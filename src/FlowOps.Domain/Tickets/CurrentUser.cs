namespace FlowOps.Domain.Tickets;

/// <summary>
/// The calling user's identity, current organization, role, and team relationships, as needed by
/// <see cref="TicketAccessPolicy"/> (AUTH-RULE-04). <paramref name="ManagedTeamIds"/> is the
/// subset of <paramref name="MemberTeamIds"/> for which the user is flagged as team manager
/// (see docs/database.md §3, <c>team_members.is_team_manager</c>) — it drives the "Manager, own
/// teams" scoping in the AUTH-RULE-02 capability matrix.
/// </summary>
/// <remarks>
/// Phase 16: <paramref name="OrganizationId"/> is the caller's current organization — the outer
/// authorization boundary <see cref="TicketAccessPolicy"/> itself does not need to know about,
/// because <paramref name="Role"/>, <paramref name="MemberTeamIds"/>, and
/// <paramref name="ManagedTeamIds"/> are already resolved (by <c>CurrentUserAccessor</c>) to only
/// ever contain this organization's role and teams. Query services that read directly from
/// <c>FlowOpsDbContext</c> (bypassing team-membership scoping entirely for Admin) still need
/// <paramref name="OrganizationId"/> explicitly — see <c>TicketQueryService.ApplyViewScope</c> and
/// its siblings.
/// </remarks>
public sealed record CurrentUser(
    Guid UserId,
    int OrganizationId,
    UserRole Role,
    IReadOnlySet<int> MemberTeamIds,
    IReadOnlySet<int> ManagedTeamIds);
