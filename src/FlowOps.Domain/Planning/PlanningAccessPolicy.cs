using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Planning;

/// <summary>Who may create, start, and complete sprints (ADR-0029). Projects are organization-level
/// (no team owner), so this follows the same coarse Admin/Manager split as inviting members;
/// viewing a project's board/tickets needs no policy beyond ordinary ticket visibility.</summary>
public static class PlanningAccessPolicy
{
    public static bool CanManageSprints(CurrentUser user) => user.Role is UserRole.Admin or UserRole.Manager;
}
