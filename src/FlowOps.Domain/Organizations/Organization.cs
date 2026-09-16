namespace FlowOps.Domain.Organizations;

/// <summary>
/// Phase 16: the outer multi-tenancy boundary. Reference data with a straightforward lifecycle,
/// not an aggregate — the same shape as <see cref="Directory.Team"/> and
/// <see cref="Catalog.Project"/>. Every <see cref="Directory.Team"/> and <see cref="Catalog.Project"/>
/// belongs to exactly one Organization; a <see cref="Tickets.Ticket"/> belongs to one transitively,
/// through its Team.
/// </summary>
/// <remarks>
/// Phase 24 (ADR-0023): <see cref="IsActive"/> is added for platform-level tenant lifecycle
/// management. Unlike <see cref="Directory.Team"/>/<see cref="Catalog.Project"/>/
/// <see cref="Catalog.Category"/> deactivation (terminal, never reverses), an Organization can be
/// <see cref="Reactivate"/>d — a platform administrator may need to restore access after a billing
/// pause or a mistaken deactivation. Deactivation never touches any other row: no membership, team,
/// category, project, ticket, comment, or event is altered. It only removes the organization from
/// <see cref="FlowOps.Application.Tickets.CurrentUserAccessor"/>'s resolvable-membership set, which
/// is what makes every ordinary tenant page unreachable for that organization's members without
/// deleting or hiding any historical data.
/// </remarks>
public sealed class Organization
{
    public int Id { get; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive { get; private set; }

    public Organization(int id, string name, DateTimeOffset createdAt, bool isActive = true)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
        IsActive = isActive;
    }

    /// <summary>Platform-level correction (Phase 24 product-gap follow-up) — no uniqueness rule to
    /// enforce, since organization names are deliberately non-unique (docs/database.md §1a).</summary>
    public void Rename(string name) => Name = name;

    /// <summary>Makes the organization inaccessible for ordinary tenant operation. Never deletes or
    /// alters any other row — see this type's own remarks.</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Restores ordinary tenant operation. Reconstructs nothing — every membership, team,
    /// category, project, ticket, comment, and event that existed before deactivation is exactly as
    /// it was, since none of them were ever touched.</summary>
    public void Reactivate() => IsActive = true;
}
