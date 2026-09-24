namespace FlowOps.Domain.Directory;

/// <summary>
/// Rule TICKET-ENT-03: reference data with a straightforward lifecycle, not an aggregate. Fields
/// match docs/database.md §2 exactly — added in Phase 3A because Phase 2's scope was the
/// Ticket aggregate/SLA/Attention/Authorization only, so this type did not exist yet even though
/// it was already fully specified in docs/database.md and docs/architecture.md.
/// </summary>
/// <remarks>
/// Phase 16: <see cref="OrganizationId"/> is the outer multi-tenancy boundary — every Team belongs
/// to exactly one Organization, and every Ticket inherits its Organization from its Team (a Ticket
/// carries no OrganizationId column of its own — see the multi-tenant foundation ADR).
/// </remarks>
/// <remarks>
/// Phase 22 (ADR-0022): <see cref="IsActive"/> is added so a Team can be deactivated without ever
/// being deleted — existing tickets, categories, and <c>TeamMember</c> rows all keep referencing
/// this row unchanged. Deactivation only removes the team from *new* ticket creation and the
/// "eligible team" options; it never cascades to Category or TeamMember rows (see ADR-0022 for why).
/// Defaults to <see langword="true"/> so every existing call site keeps constructing active teams.
/// </remarks>
/// <remarks>
/// Phase 30B (ADR-0032): <see cref="Reactivate"/> restores <see cref="IsActive"/> — deactivation is
/// no longer terminal. Nothing else about ADR-0022 changes: reactivating still never cascades to
/// Category/TeamMember/Ticket rows, since none of them were ever touched by deactivation either.
/// </remarks>
public sealed class Team
{
    public int Id { get; }
    public int OrganizationId { get; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive { get; private set; }

    public Team(int id, int organizationId, string name, DateTimeOffset createdAt, bool isActive = true)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        CreatedAt = createdAt;
        IsActive = isActive;
    }

    public void Rename(string name) => Name = name;

    /// <summary>Makes the team unavailable for new ticket creation and new-category creation. Never
    /// deletes, never cascades — existing tickets, categories, and team memberships all remain
    /// exactly as they were (ADR-0022). Reversible via <see cref="Reactivate"/> (ADR-0032).</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Restores the team to normal use. The caller is responsible for the one invariant this
    /// method itself cannot see — that no other active team in the same organization already holds
    /// this name (the database's own filtered unique index is the final guard; the application layer
    /// turns a violation into a friendly message, per ADR-0032).</summary>
    public void Reactivate() => IsActive = true;
}
