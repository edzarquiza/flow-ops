using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Catalog;

/// <summary>
/// AUTH-RULE-01 / CLAUDE.md §6.1's coarse gate ("Manage users/teams/categories/SLA is Admin-only"),
/// applied to category management specifically — the same shape as
/// <see cref="FlowOps.Domain.Organizations.OrganizationAccessPolicy"/> for invitations. Every
/// application service that creates or changes a Category must call this, never restate the check
/// inline.
/// </summary>
public static class CatalogAccessPolicy
{
    public static bool CanManageCategories(CurrentUser user) => user.Role == UserRole.Admin;

    /// <summary>Same rule as <see cref="CanManageCategories"/> (CLAUDE.md §6.1 groups "categories"
    /// and "projects" under one Admin-only capability) — a separate method per resource, matching
    /// <see cref="FlowOps.Domain.Directory.DirectoryAccessPolicy"/>'s one-method-per-resource shape,
    /// not a shared name that would blur which resource is actually being checked.</summary>
    public static bool CanManageProjects(CurrentUser user) => user.Role == UserRole.Admin;
}
