using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Organizations;

/// <summary>
/// `ORG-RULE-12`: no action may leave an organization with zero Admin memberships. Introduced in
/// Phase 17 (<see cref="FlowOps.Application.Accounts.AccountService.DeleteAccountAsync"/>) as an
/// inline check; extracted here so account deletion, membership role changes, and member removal
/// all share the exact same rule rather than three independently-maintained copies of it (Step
/// 19's explicit instruction). Two queries, no full organization/membership graph ever loaded.
/// </summary>
/// <remarks>
/// Phase 24 (ADR-0023): "another Admin" means another Admin membership belonging to an *active*
/// account. Discovered live while wiring platform user deactivation to this guard — an Admin whose
/// account has already been deactivated (platform deactivation deliberately never removes the
/// membership row, ADR-0023 §5) still held a real `OrganizationMembership` row with
/// `Role == Admin`, so the original row-count-only check could not tell "another Admin exists" from
/// "another Admin *membership record* exists for an account that can no longer sign in" — a
/// dormant gap since Phase 17 (an already-deactivated Admin's membership row was never excluded
/// there either), only surfaced once platform deactivation gave a second, independent way for an
/// Admin account to become inactive without its membership being removed. Both callers below now
/// join to a genuinely usable account for the "another Admin" half of the check.
///
/// Phase 24A (ADR-0024): "genuinely usable" now also excludes a still-Pending account —
/// `IsActive` alone is `true` for a Pending account (it is not "deactivated," only "not yet
/// approved" — see `AccountLifecyclePolicy`), but a Pending Admin cannot sign in and so cannot
/// actually act as the organization's other Admin either. Both joins below require
/// `RegistrationApprovedAt != null` in addition to `IsActive`, i.e. `AccountStatus.Active`.
/// </remarks>
public static class SoleAdminGuard
{
    /// <summary>True if <paramref name="userId"/> is currently the only Admin of
    /// <paramref name="organizationId"/> — i.e. removing or changing away from their Admin role in
    /// this one organization would leave it with none.</summary>
    public static async Task<bool> IsSoleAdminAsync(FlowOpsDbContext dbContext, int organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var isAdminHere = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.Role == UserRole.Admin, cancellationToken);
        if (!isAdminHere)
        {
            return false;
        }

        var anotherAdminExists = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.Role == UserRole.Admin && m.UserId != userId)
            .Join(dbContext.Users.Where(u => u.IsActive && u.RegistrationApprovedAt != null), m => m.UserId, u => u.Id, (m, u) => m.UserId)
            .AnyAsync(cancellationToken);

        return !anotherAdminExists;
    }

    /// <summary>
    /// The multi-organization form of the same check (Phase 17's account-deletion scenario): given
    /// every organization <paramref name="userId"/> is an Admin of, returns the names of those that
    /// would be left with zero Admins if <paramref name="userId"/>'s Admin status were removed from
    /// all of them at once.
    /// </summary>
    public static async Task<IReadOnlyList<string>> OrganizationsThatWouldLoseTheirLastAdminAsync(FlowOpsDbContext dbContext, Guid userId, CancellationToken cancellationToken = default)
    {
        var adminOrganizationIds = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Role == UserRole.Admin)
            .Select(m => m.OrganizationId)
            .ToListAsync(cancellationToken);

        if (adminOrganizationIds.Count == 0)
        {
            return [];
        }

        var organizationsWithAnotherAdmin = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => adminOrganizationIds.Contains(m.OrganizationId) && m.Role == UserRole.Admin && m.UserId != userId)
            .Join(dbContext.Users.Where(u => u.IsActive && u.RegistrationApprovedAt != null), m => m.UserId, u => u.Id, (m, u) => new { m.OrganizationId })
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var blockedOrganizationIds = adminOrganizationIds.Except(organizationsWithAnotherAdmin).ToList();
        if (blockedOrganizationIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Organizations
            .AsNoTracking()
            .Where(o => blockedOrganizationIds.Contains(o.Id))
            .Select(o => o.Name)
            .ToListAsync(cancellationToken);
    }
}
