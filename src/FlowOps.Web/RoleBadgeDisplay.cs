using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>Presentation-only tone/icon mapping from a <see cref="UserRole"/> to the shared badge
/// system — four distinct, restrained hues so a role reads at a glance in the Members table and
/// the account identity area, without inventing a second role taxonomy (the role itself is still
/// <see cref="UserRole"/>; this only picks a color and icon).</summary>
public static class RoleBadgeDisplay
{
    public static SemanticTone Tone(UserRole role) => role switch
    {
        UserRole.Admin => SemanticTone.Danger,
        UserRole.Manager => SemanticTone.Teal,
        UserRole.Agent => SemanticTone.Info,
        UserRole.Viewer => SemanticTone.Violet,
        _ => SemanticTone.Muted,
    };

    public static string Icon(UserRole role) => role switch
    {
        UserRole.Admin => Icons.RoleAdmin,
        UserRole.Manager => Icons.RoleManager,
        UserRole.Agent => Icons.RoleAgent,
        UserRole.Viewer => Icons.RoleViewer,
        _ => Icons.RoleAgent,
    };
}
