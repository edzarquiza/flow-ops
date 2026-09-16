using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from a <see cref="UserRole"/> to the product-facing role name used
/// throughout FlowOps's own copy (CLAUDE.md §6.3's role-semantics list; the same four names
/// <see cref="FlowOps.Application.Demo.DemoPersonas"/> already uses for three of its four seeded
/// personas). Decides no authorization — <see cref="UserRole"/> itself remains the only value any
/// policy consults; this only renders it.
/// </summary>
public static class RoleDisplay
{
    public static string ToDisplayName(UserRole role) => role switch
    {
        UserRole.Admin => "Admin",
        UserRole.Manager => "Service Desk Manager",
        UserRole.Agent => "IT Support Agent",
        UserRole.Viewer => "Executive Viewer",
        _ => role.ToString(),
    };
}
