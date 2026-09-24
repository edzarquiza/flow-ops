namespace FlowOps.Domain.Catalog;

/// <summary>
/// Rule TICKET-ENT-03. CLAUDE.md §4.2 defines <c>Project</c> only as an optional ticket
/// association with no further fields; docs/database.md §5 deliberately adds nothing beyond
/// that. Added in Phase 3A for the same reason as <see cref="Directory.Team"/>.
/// </summary>
/// <remarks>
/// Phase 16: <see cref="OrganizationId"/> is added because, unlike <c>Category</c> (which inherits
/// its organization transitively through <c>TeamId</c>), Project has no team relation and was
/// entirely global before this phase — a real cross-tenant leak if left unscoped.
/// </remarks>
/// <remarks>
/// Project management phase: <see cref="IsActive"/> is added because a Project can be referenced by
/// existing tickets (<c>Ticket.ProjectId</c>) and must never be hard-deleted — deactivation is the
/// only removal path, mirroring <c>ApplicationUser.IsActive</c>'s same "soft, reversible-by-design
/// state, never a delete" shape. Defaults to <see langword="true"/> so every existing call site
/// (chiefly <c>DemoDataSeeder</c>) keeps constructing active projects unchanged.
/// </remarks>
public sealed class Project
{
    public int Id { get; }
    public int OrganizationId { get; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive { get; private set; }

    public Project(int id, int organizationId, string name, DateTimeOffset createdAt, bool isActive = true)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        CreatedAt = createdAt;
        IsActive = isActive;
    }

    public void Rename(string name) => Name = name;

    /// <summary>Makes the project unavailable for new ticket selection. Never deletes — existing
    /// tickets keep their <c>ProjectId</c> and keep displaying this project's name, since the row
    /// itself is untouched. Reversible via <see cref="Reactivate"/> (ADR-0032).</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Restores the project to normal use. The caller is responsible for the one invariant
    /// this method itself cannot see — that no other active project in the same organization already
    /// holds this name (the database's own filtered unique index is the final guard; the application
    /// layer turns a violation into a friendly message, per ADR-0032).</summary>
    public void Reactivate() => IsActive = true;
}
