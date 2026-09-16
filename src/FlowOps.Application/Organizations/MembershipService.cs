using FlowOps.Application.Demo;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Organizations;

/// <summary>
/// Phase 18: the current organization's member list, role changes, and removal. Every method takes
/// the acting <see cref="CurrentUser"/> — already resolved server-side, already carrying the
/// caller's real <c>OrganizationId</c> — and re-verifies the target membership belongs to that same
/// organization before doing anything to it; there is no path through which a caller-supplied
/// organization id could reach this class at all.
/// </summary>
public sealed class MembershipService
{
    private readonly FlowOpsDbContext _dbContext;

    public MembershipService(FlowOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>`ORG-RULE-11`: only Admin/Manager may view the member list at all.</summary>
    /// <param name="search">
    /// An optional free-text term matched against name and email, scoped to
    /// <paramref name="currentUser"/>'s own organization exactly like the rest of this query —
    /// never a second, broader lookup. <see langword="null"/>/whitespace is treated as no search.
    /// </param>
    public async Task<IReadOnlyList<MemberListItem>> GetMembersAsync(
        CurrentUser currentUser,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!OrganizationAccessPolicy.CanManageMembers(currentUser))
        {
            throw new OrganizationAccessDeniedException("This role may not view organization members.");
        }

        var normalizedSearch = SearchTermNormalizer.Normalize(search);

        // One projecting join — no N+1, no full user/membership graph loaded (Step 30). Ordering
        // is applied to the joined anonymous shape, not the already-constructed MemberListItem —
        // EF Core cannot translate an OrderBy against a member of a record built by an earlier
        // Select/Join (the same rule AnalyticsQueryService's own workload query documents), so the
        // record is projected last, immediately before materialisation.
        var query = _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == currentUser.OrganizationId)
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName, u.Email, m.Role, u.IsActive, u.IsDemoProtected });

        if (normalizedSearch is not null)
        {
            var pattern = SearchTermNormalizer.ToLikePattern(normalizedSearch);
            query = query.Where(x =>
                EF.Functions.ILike(x.DisplayName, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(x.Email!, pattern, SearchTermNormalizer.LikeEscapeCharacter));
        }

        return await query
            .OrderBy(x => x.DisplayName)
            .Select(x => new MemberListItem(x.Id, x.DisplayName, x.Email!, x.Role, x.IsActive, x.IsDemoProtected))
            .ToListAsync(cancellationToken);
    }

    /// <summary>`ORG-RULE-11`/`12`: role-assignment authorization, then the sole-admin invariant,
    /// checked in that order — an unauthorized caller never learns whether the sole-admin rule
    /// would even have applied.</summary>
    public async Task<MembershipActionResult> ChangeRoleAsync(CurrentUser actor, Guid targetUserId, UserRole newRole, CancellationToken cancellationToken = default)
    {
        var target = await LoadTargetAsync(actor.OrganizationId, targetUserId, cancellationToken);
        if (target is null)
        {
            return MembershipActionResult.Failed("That member is not part of this organization.");
        }

        var (membership, user) = target.Value;

        if (!OrganizationAccessPolicy.CanChangeRole(actor, membership.Role, newRole))
        {
            throw new OrganizationAccessDeniedException("This role may not make that change.");
        }

        DemoProtectionPolicy.EnsureMutable(user);

        if (membership.Role == UserRole.Admin && newRole != UserRole.Admin)
        {
            var isSoleAdmin = await SoleAdminGuard.IsSoleAdminAsync(_dbContext, actor.OrganizationId, targetUserId, cancellationToken);
            if (isSoleAdmin)
            {
                return MembershipActionResult.Failed("This is the only Admin in this organization. Assign another Admin first.");
            }
        }

        membership.ChangeRole(newRole);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MembershipActionResult.Success();
    }

    /// <summary>`ORG-RULE-11`/`12`/`13`: removes only the membership row — the
    /// <c>ApplicationUser</c>, every other organization's membership, and all historical
    /// ticket/comment/audit data stay completely untouched (the same deactivation-not-deletion
    /// principle ADR-0016 established for account deletion, applied here to one organization's
    /// access grant rather than the whole account).</summary>
    public async Task<MembershipActionResult> RemoveMemberAsync(CurrentUser actor, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        var target = await LoadTargetAsync(actor.OrganizationId, targetUserId, cancellationToken);
        if (target is null)
        {
            return MembershipActionResult.Failed("That member is not part of this organization.");
        }

        var (membership, user) = target.Value;

        if (!OrganizationAccessPolicy.CanRemoveMember(actor, membership.Role))
        {
            throw new OrganizationAccessDeniedException("This role may not remove that member.");
        }

        DemoProtectionPolicy.EnsureMutable(user);

        if (membership.Role == UserRole.Admin)
        {
            var isSoleAdmin = await SoleAdminGuard.IsSoleAdminAsync(_dbContext, actor.OrganizationId, targetUserId, cancellationToken);
            if (isSoleAdmin)
            {
                return MembershipActionResult.Failed("This is the only Admin in this organization. Assign another Admin first.");
            }
        }

        _dbContext.OrganizationMemberships.Remove(membership);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MembershipActionResult.Success();
    }

    private async Task<(OrganizationMembership Membership, ApplicationUser User)?> LoadTargetAsync(int organizationId, Guid targetUserId, CancellationToken cancellationToken)
    {
        var membership = await _dbContext.OrganizationMemberships
            .SingleOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == targetUserId, cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == targetUserId, cancellationToken);
        return (membership, user);
    }
}
