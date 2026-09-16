using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Organizations;

/// <summary>
/// Phase 16: the join between a user and an Organization, carrying that user's role <em>within
/// that organization</em>. Deliberately not carried on <c>ApplicationUser</c> — a user may belong
/// to more than one Organization, potentially with a different <see cref="UserRole"/> in each, so
/// role is a fact about the membership, not the user. This is the single authoritative source for
/// <see cref="CurrentUser.Role"/>; ASP.NET Core Identity's own per-user role assignment is kept in
/// sync for the coarse, first-layer <c>[Authorize]</c> gate only (CLAUDE.md §6.2) and is never
/// read by <see cref="CurrentUserAccessor"/>.
/// </summary>
public sealed class OrganizationMembership
{
    public int Id { get; }
    public int OrganizationId { get; }
    public Guid UserId { get; }
    public UserRole Role { get; private set; }
    public DateTimeOffset JoinedAt { get; }

    public OrganizationMembership(int id, int organizationId, Guid userId, UserRole role, DateTimeOffset joinedAt)
    {
        Id = id;
        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    /// <summary>Phase 18: the only mutation this entity supports. Sole-admin protection and
    /// role-assignment authorization (`ORG-RULE-11`/`12`) are both decided by the caller
    /// (<c>MembershipService</c>) before this is ever invoked — this method only records the
    /// already-authorized new role, the same division of responsibility <c>Ticket</c>'s own
    /// mutation methods use throughout.</summary>
    public void ChangeRole(UserRole newRole) => Role = newRole;
}
