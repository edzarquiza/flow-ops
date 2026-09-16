using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Directory;

/// <summary>
/// AUTH-RULE-01 / CLAUDE.md §6.1's coarse gate ("Manage users/teams/categories/SLA is Admin-only"),
/// applied to team management specifically — the same shape as
/// <see cref="FlowOps.Domain.Organizations.OrganizationAccessPolicy"/> for invitations. Every
/// application service that creates or changes a Team must call this, never restate the check
/// inline.
/// </summary>
public static class DirectoryAccessPolicy
{
    public static bool CanManageTeams(CurrentUser user) => user.Role == UserRole.Admin;
}
