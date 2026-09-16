using FlowOps.Application.Accounts;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Accounts;
using FlowOps.Domain.Platform;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Platform;

/// <summary>
/// Phase 24 (ADR-0023): platform-level user administration — lists/inspects every user on the
/// platform (never scoped to one organization) and approves/deactivates/reactivates an identity
/// using the exact same three-mechanism recipe <see cref="FlowOps.Application.Accounts.AccountService.DeleteAccountAsync"/>
/// already established (<c>IsActive</c>/lockout, a permanent Identity lockout, and a fresh security
/// stamp), with one deliberate difference: unlike self-account deletion, platform deactivation never
/// removes any <c>OrganizationMembership</c> row — see this module's own ADR for why.
/// </summary>
/// <remarks>
/// Phase 24A (ADR-0024): every legal <see cref="AccountStatus"/> transition (Approve/Deactivate/
/// Reactivate) is decided by <see cref="AccountLifecyclePolicy"/>, never by an inline
/// <c>if (user.IsActive)</c> check — that policy is what keeps "never approved" (Pending) and "was
/// approved, later deactivated" (Inactive) from ever being confused with each other, even though
/// both currently leave the account locked out.
/// </remarks>
public sealed class PlatformUserService
{
    public const int PageSize = 25;

    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TimeProvider _timeProvider;

    public PlatformUserService(FlowOpsDbContext dbContext, UserManager<ApplicationUser> userManager, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<PlatformUserListItem>> ListUsersAsync(
        int pageNumber,
        string? search = null,
        AccountStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        var query = _dbContext.Users.AsNoTracking();

        var normalizedSearch = SearchTermNormalizer.Normalize(search);
        if (normalizedSearch is not null)
        {
            var pattern = SearchTermNormalizer.ToLikePattern(normalizedSearch);
            query = query.Where(u =>
                EF.Functions.ILike(u.Email!, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(u.DisplayName, pattern, SearchTermNormalizer.LikeEscapeCharacter));
        }

        // Filtered server-side (never a full-table scan filtered in memory — spec §36): each
        // branch is a plain, translatable predicate over the three raw columns. Pending explicitly
        // excludes a Rejected account (registration_rejected_at set) — both share a null
        // registration_approved_at, so without this a Rejected account would wrongly still show
        // up as Pending everywhere Pending is queried.
        query = status switch
        {
            AccountStatus.Pending => query.Where(u => u.RegistrationApprovedAt == null && u.RegistrationRejectedAt == null),
            AccountStatus.Active => query.Where(u => u.RegistrationApprovedAt != null && u.IsActive),
            AccountStatus.Inactive => query.Where(u => u.RegistrationApprovedAt != null && !u.IsActive),
            AccountStatus.Rejected => query.Where(u => u.RegistrationRejectedAt != null),
            _ => query,
        };

        var totalCount = await query.CountAsync(cancellationToken);

        var page = await query
            .OrderBy(u => u.DisplayName)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(u => new { u.Id, Email = u.Email!, u.DisplayName, u.IsActive, u.RegistrationApprovedAt, u.RegistrationRejectedAt })
            .ToListAsync(cancellationToken);

        var userIds = page.Select(u => u.Id).ToArray();

        var membershipCounts = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => userIds.Contains(m.UserId))
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(u => new PlatformUserListItem(
                u.Id,
                u.Email,
                u.DisplayName,
                AccountStatusResolver.Resolve(u.IsActive, u.RegistrationApprovedAt, u.RegistrationRejectedAt),
                membershipCounts.FirstOrDefault(m => m.UserId == u.Id)?.Count ?? 0,
                null)) // ApplicationUser carries no CreatedAt column (docs/database.md §1) — never fabricated here.
            .ToList();

        return new PagedResult<PlatformUserListItem>(items, pageNumber, PageSize, totalCount);
    }

    /// <summary>The platform-wide status breakdown for the <c>/Platform/Users</c> summary strip —
    /// one bounded <c>GROUP BY</c>, never a full in-memory scan (spec §36).</summary>
    public async Task<PlatformUserStatusSummary> GetUserStatusSummaryAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _dbContext.Users
            .AsNoTracking()
            .GroupBy(u => new { IsRejected = u.RegistrationRejectedAt != null, IsPending = u.RegistrationApprovedAt == null, u.IsActive })
            .Select(g => new { g.Key.IsRejected, g.Key.IsPending, g.Key.IsActive, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var rejected = counts.Where(c => c.IsRejected).Sum(c => c.Count);
        var pending = counts.Where(c => !c.IsRejected && c.IsPending).Sum(c => c.Count);
        var active = counts.Where(c => !c.IsRejected && !c.IsPending && c.IsActive).Sum(c => c.Count);
        var inactive = counts.Where(c => !c.IsRejected && !c.IsPending && !c.IsActive).Sum(c => c.Count);
        return new PlatformUserStatusSummary(pending, active, inactive, rejected);
    }

    /// <summary>Phase 24B: the homepage's own bounded, prioritized Pending list — never the full
    /// paginated set <c>/Platform/Users/Pending</c> shows. Each row's organization/registered-date
    /// context comes from that account's earliest <c>OrganizationMembership</c> — the organization
    /// created atomically alongside it at self-registration (ADR-0016) — never a second,
    /// independent "organization approval" record (spec §3: approval stays identity-level).</summary>
    public async Task<IReadOnlyList<PlatformPendingAccountListItem>> GetPendingAccountsAsync(int take, CancellationToken cancellationToken = default)
    {
        var pendingUserIds = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.RegistrationApprovedAt == null && u.RegistrationRejectedAt == null)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (pendingUserIds.Count == 0)
        {
            return [];
        }

        // Bounded to just the (typically few) Pending accounts' own memberships — never the whole
        // membership table — then the earliest membership per user is resolved client-side, which
        // is safe precisely because this set is already small.
        var memberships = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => pendingUserIds.Contains(m.UserId))
            .Join(_dbContext.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { m.UserId, OrganizationName = o.Name, m.JoinedAt })
            .OrderBy(x => x.JoinedAt)
            .ToListAsync(cancellationToken);

        var earliestByUser = memberships
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.First());

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => pendingUserIds.Contains(u.Id))
            .Select(u => new { u.Id, Email = u.Email!, u.DisplayName })
            .ToListAsync(cancellationToken);

        return users
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                Earliest = earliestByUser.GetValueOrDefault(u.Id),
            })
            .OrderByDescending(u => u.Earliest?.JoinedAt ?? DateTimeOffset.MinValue)
            .Take(take)
            .Select(u => new PlatformPendingAccountListItem(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Earliest?.OrganizationName,
                u.Earliest?.JoinedAt ?? DateTimeOffset.MinValue))
            .ToList();
    }

    /// <summary>Phase 24B: the homepage's own bounded "recently joined" list. Ordered by each
    /// user's most recent <c>OrganizationMembership.JoinedAt</c> — a real, stored timestamp, unlike
    /// <c>ApplicationUser</c> itself (docs/database.md §1) — never a fabricated "created" date.</summary>
    public async Task<IReadOnlyList<PlatformUserListItem>> GetRecentUsersAsync(int take, CancellationToken cancellationToken = default)
    {
        var recentMembership = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, JoinedAt = g.Max(m => m.JoinedAt) })
            .OrderByDescending(x => x.JoinedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var userIds = recentMembership.Select(x => x.UserId).ToArray();

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, Email = u.Email!, u.DisplayName, u.IsActive, u.RegistrationApprovedAt, u.RegistrationRejectedAt })
            .ToListAsync(cancellationToken);

        var membershipCounts = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => userIds.Contains(m.UserId))
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return recentMembership
            .Select(rm => new { rm.JoinedAt, User = users.SingleOrDefault(u => u.Id == rm.UserId) })
            .Where(x => x.User is not null)
            .Select(x => new PlatformUserListItem(
                x.User!.Id,
                x.User.Email,
                x.User.DisplayName,
                AccountStatusResolver.Resolve(x.User.IsActive, x.User.RegistrationApprovedAt, x.User.RegistrationRejectedAt),
                membershipCounts.FirstOrDefault(m => m.UserId == x.User.Id)?.Count ?? 0,
                x.JoinedAt))
            .ToList();
    }

    public async Task<PlatformUserDetail?> GetUserDetailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var memberships = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(_dbContext.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Name, m.Role })
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return new PlatformUserDetail(
            user.Id,
            user.Email!,
            user.DisplayName,
            AccountStatusResolver.Resolve(user),
            memberships.Select(m => new PlatformUserMembership(m.Id, m.Name, m.Role.ToString())).ToList());
    }

    /// <summary>The most recent platform-administration actions taken against this user — the
    /// same "recent activity" visibility gap closed on the organization detail page, mirrored here
    /// (<see cref="PlatformOrganizationService.GetRecentAuditEventsAsync"/>).</summary>
    public async Task<IReadOnlyList<PlatformAuditEventListItem>> GetRecentAuditEventsAsync(Guid userId, int take = 10, CancellationToken cancellationToken = default)
    {
        return await _dbContext.PlatformAuditEvents
            .AsNoTracking()
            .Where(e => e.TargetUserId == userId)
            .Join(_dbContext.Users, e => e.ActorUserId, u => u.Id, (e, u) => new { e.Id, e.EventType, ActorDisplayName = u.DisplayName, e.OccurredAt })
            // Id as a secondary key: two events recorded within the same clock tick (a real
            // possibility, not just a fixed-clock test artifact) would otherwise have no
            // deterministic tiebreak — Id strictly increases in insertion order.
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new PlatformAuditEventListItem(x.EventType, x.ActorDisplayName, x.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Phase 24A (ADR-0024): the one and only way a Pending account ever becomes able to
    /// authenticate. Legal only from <see cref="AccountStatus.Pending"/> — an already-Active account
    /// is treated as an idempotent no-op success (spec §13: "Active → Approve again" must not
    /// duplicate any side effect), while an Inactive account (previously approved, later
    /// deactivated) is rejected with a message pointing at Reactivate instead, never silently
    /// treated as approvable (spec §14: Approve must never be a backdoor around Reactivate's own
    /// gate). Clears the exact lockout <see cref="FlowOps.Application.Accounts.AccountService.RegisterAsync"/>
    /// set at registration — the same clear-lockout mechanism <see cref="ReactivateUserAsync"/>
    /// already uses.
    /// </summary>
    public async Task<PlatformMutationResult> ApproveUserAsync(PlatformAdminIdentity actor, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new PlatformAccessDeniedException("This user is not available.");
        }

        var status = AccountStatusResolver.Resolve(user);
        if (status == AccountStatus.Active)
        {
            return PlatformMutationResult.Success(); // idempotent: already approved, nothing to do.
        }

        if (!AccountLifecyclePolicy.CanApprove(status))
        {
            return PlatformMutationResult.Failed(status switch
            {
                AccountStatus.Rejected => "This account was already rejected and cannot be approved.",
                AccountStatus.Inactive => "This account has already been deactivated. Use Reactivate instead.",
                _ => "This account cannot be approved.",
            });
        }

        var now = _timeProvider.GetUtcNow();
        user.RegistrationApprovedAt = now;
        user.IsActive = true;
        user.LockoutEnabled = false;
        user.LockoutEnd = null;

        // Phase 24A-Extension (ADR-0025) §19: two Platform Admins could race Approve/Reject on the
        // same Pending account. UserManager.UpdateAsync already carries Identity's own optimistic
        // concurrency check (ConcurrencyStamp) — if another admin's mutation committed first, this
        // fails here rather than silently overwriting it, and the caller is told to re-check state
        // instead of assuming success.
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return PlatformMutationResult.Failed("This account was just changed by another administrator. Please refresh and try again.");
        }

        await _userManager.UpdateSecurityStampAsync(user);

        _dbContext.PlatformAuditEvents.Add(new PlatformAuditEvent(0, PlatformEventType.UserApproved, actor.UserId, null, userId, now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>
    /// Phase 24A-Extension (ADR-0025): the one and only way a Pending account becomes permanently
    /// unable to authenticate through initial review. Legal only from <see cref="AccountStatus.Pending"/> —
    /// an already-Rejected account is an idempotent no-op success (mirrors <see cref="ApproveUserAsync"/>'s
    /// own "Active → Approve again" idiom); Active or Inactive accounts cannot be rejected at all
    /// (Reject is initial review, not a general "revoke access" action — <see cref="DeactivateUserAsync"/>
    /// already covers that for an Active account). Never deletes the <c>ApplicationUser</c>,
    /// <c>Organization</c>, or any <c>OrganizationMembership</c> row (spec §5/§27) — the account
    /// simply never gains its first-ever ability to authenticate; the lockout registration itself
    /// already set is never cleared.
    /// </summary>
    public async Task<PlatformMutationResult> RejectUserAsync(PlatformAdminIdentity actor, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new PlatformAccessDeniedException("This user is not available.");
        }

        var status = AccountStatusResolver.Resolve(user);
        if (status == AccountStatus.Rejected)
        {
            return PlatformMutationResult.Success(); // idempotent: already rejected, nothing to do.
        }

        if (!AccountLifecyclePolicy.CanReject(status))
        {
            return PlatformMutationResult.Failed(status switch
            {
                AccountStatus.Active => "This account is already active and cannot be rejected. Deactivate it instead if you need to revoke access.",
                AccountStatus.Inactive => "This account is inactive and cannot be rejected.",
                _ => "This account cannot be rejected.",
            });
        }

        var now = _timeProvider.GetUtcNow();
        user.RegistrationRejectedAt = now;
        // Deliberately no lockout change: the account is already fully locked out from
        // registration (AccountService.RegisterAsync), and rejection must never accidentally
        // loosen it — it stays exactly as unable to authenticate as it already was.

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return PlatformMutationResult.Failed("This account was just changed by another administrator. Please refresh and try again.");
        }

        _dbContext.PlatformAuditEvents.Add(new PlatformAuditEvent(0, PlatformEventType.UserRejected, actor.UserId, null, userId, now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>
    /// Deactivates a user's ability to authenticate. Reuses
    /// <see cref="FlowOps.Application.Accounts.AccountService.DeleteAccountAsync"/>'s exact lockout recipe, but —
    /// unlike that self-service path — never removes any <c>OrganizationMembership</c> row
    /// (ADR-0023: platform deactivation must leave membership history coherent, since a platform
    /// operator did not choose to leave any organization). Blocked by the same sole-admin rule
    /// account deletion already enforces: deactivating a user who is the only Admin of an
    /// organization would leave it with none. Legal only from <see cref="AccountStatus.Active"/> —
    /// never from Pending (ADR-0024): there is nothing to "deactivate" on an account that was never
    /// approved in the first place.
    /// </summary>
    public async Task<PlatformMutationResult> DeactivateUserAsync(PlatformAdminIdentity actor, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new PlatformAccessDeniedException("This user is not available.");
        }

        var status = AccountStatusResolver.Resolve(user);
        if (!AccountLifecyclePolicy.CanDeactivate(status))
        {
            return PlatformMutationResult.Failed(status switch
            {
                AccountStatus.Pending => "This account is still pending approval, not active.",
                AccountStatus.Rejected => "This account was rejected and was never active.",
                _ => "This user is already inactive.",
            });
        }

        var blockedOrganizationNames = await SoleAdminGuard.OrganizationsThatWouldLoseTheirLastAdminAsync(_dbContext, userId, cancellationToken);
        if (blockedOrganizationNames.Count > 0)
        {
            return PlatformMutationResult.Failed(
                $"This user is the only Admin of: {string.Join(", ", blockedOrganizationNames)}. Assign another Admin there first.");
        }

        user.IsActive = false;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user);

        _dbContext.PlatformAuditEvents.Add(new PlatformAuditEvent(0, PlatformEventType.UserDeactivated, actor.UserId, null, userId, _timeProvider.GetUtcNow()));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>Legal only from <see cref="AccountStatus.Inactive"/> — never from Pending
    /// (ADR-0024's critical guard: Reactivate must never be usable as a backdoor around Approve).</summary>
    public async Task<PlatformMutationResult> ReactivateUserAsync(PlatformAdminIdentity actor, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new PlatformAccessDeniedException("This user is not available.");
        }

        var status = AccountStatusResolver.Resolve(user);
        if (!AccountLifecyclePolicy.CanReactivate(status))
        {
            return PlatformMutationResult.Failed(status switch
            {
                AccountStatus.Pending => "This account has not yet been approved. Use Approve instead.",
                AccountStatus.Rejected => "This account was rejected and cannot be reactivated.",
                _ => "This user is already active.",
            });
        }

        user.IsActive = true;
        user.LockoutEnabled = false;
        user.LockoutEnd = null;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user);

        _dbContext.PlatformAuditEvents.Add(new PlatformAuditEvent(0, PlatformEventType.UserReactivated, actor.UserId, null, userId, _timeProvider.GetUtcNow()));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }
}
