using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Organizations;

/// <summary>
/// `ORG-RULE-11`: the single authoritative authorization surface for invitations and member
/// management — the same shape as <see cref="TicketAccessPolicy"/> for tickets. Every application
/// service and Razor Page must call this before creating an invitation or changing/removing a
/// membership; never a second, "simpler" check.
/// </summary>
/// <remarks>
/// Least-privilege policy for Manager (Step 8/17, deliberately chosen — no existing product
/// requirement pinned an exact rule): a Service Desk Manager may invite, role-change, or remove
/// only Agent/Viewer members — never Admin or another Manager. Nothing here grants a Manager the
/// ability to create or manage an Admin, which would let a non-Admin role quietly accumulate
/// Admin-equivalent influence over the organization. Admin has no such restriction.
/// </remarks>
public static class OrganizationAccessPolicy
{
    /// <summary>Who may create an invitation at all, independent of the target role.</summary>
    public static bool CanInvite(CurrentUser user) =>
        user.Role is UserRole.Admin or UserRole.Manager;

    /// <summary>Whether <paramref name="inviter"/> may issue an invitation for
    /// <paramref name="targetRole"/> specifically.</summary>
    public static bool CanInviteRole(CurrentUser inviter, UserRole targetRole) =>
        inviter.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => targetRole is UserRole.Agent or UserRole.Viewer,
            _ => false,
        };

    /// <summary>Who may view/manage the current organization's member list at all.</summary>
    public static bool CanManageMembers(CurrentUser user) =>
        user.Role is UserRole.Admin or UserRole.Manager;

    /// <summary>Whether <paramref name="actor"/> may change a member currently holding
    /// <paramref name="targetCurrentRole"/> to <paramref name="newRole"/>.</summary>
    public static bool CanChangeRole(CurrentUser actor, UserRole targetCurrentRole, UserRole newRole) =>
        actor.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => targetCurrentRole is UserRole.Agent or UserRole.Viewer && newRole is UserRole.Agent or UserRole.Viewer,
            _ => false,
        };

    /// <summary>Whether <paramref name="actor"/> may remove a member currently holding
    /// <paramref name="targetRole"/>.</summary>
    public static bool CanRemoveMember(CurrentUser actor, UserRole targetRole) =>
        actor.Role switch
        {
            UserRole.Admin => true,
            UserRole.Manager => targetRole is UserRole.Agent or UserRole.Viewer,
            _ => false,
        };
}
