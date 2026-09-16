using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Platform;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Platform;

/// <summary>
/// Phase 24 (ADR-0023): platform-level organization administration — deliberately separate from
/// every tenant-scoped Application service (<c>TeamService</c>, <c>CatalogService</c>, etc.), which
/// all resolve their organization boundary from a caller's own <c>CurrentUser.OrganizationId</c>.
/// This service instead takes an explicit <c>organizationId</c>/<c>userId</c> because a platform
/// operator, by definition, is not scoped to any one organization — see this module's own ADR for
/// why that does not weaken tenant isolation elsewhere. Every method requires a
/// <see cref="PlatformAdminIdentity"/>, resolved by <see cref="PlatformUserAccessor"/>, never a raw
/// boolean or claim.
/// </summary>
public sealed class PlatformOrganizationService
{
    public const int PageSize = 25;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PlatformOrganizationService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>Every organization on the platform, active and inactive alike — the one place in
    /// the application that is allowed to see an inactive organization at all (ADR-0023).</summary>
    public async Task<PagedResult<PlatformOrganizationListItem>> ListOrganizationsAsync(
        int pageNumber,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        var query = _dbContext.Organizations.AsNoTracking();

        var normalizedSearch = SearchTermNormalizer.Normalize(search);
        if (normalizedSearch is not null)
        {
            var pattern = SearchTermNormalizer.ToLikePattern(normalizedSearch);
            query = query.Where(o => EF.Functions.ILike(o.Name, pattern, SearchTermNormalizer.LikeEscapeCharacter));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var page = await query
            .OrderBy(o => o.Name)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(o => new { o.Id, o.Name, o.IsActive, o.CreatedAt })
            .ToListAsync(cancellationToken);

        var organizationIds = page.Select(o => o.Id).ToArray();

        // Two bounded, grouped count queries for the whole page — never one query per row.
        var memberCounts = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => organizationIds.Contains(m.OrganizationId))
            .GroupBy(m => m.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ticketCounts = await _dbContext.Tickets
            .AsNoTracking()
            .Join(_dbContext.Teams, t => t.TeamId, team => team.Id, (t, team) => new { team.OrganizationId })
            .Where(x => organizationIds.Contains(x.OrganizationId))
            .GroupBy(x => x.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(o => new PlatformOrganizationListItem(
                o.Id,
                o.Name,
                o.IsActive,
                memberCounts.FirstOrDefault(m => m.OrganizationId == o.Id)?.Count ?? 0,
                ticketCounts.FirstOrDefault(t => t.OrganizationId == o.Id)?.Count ?? 0,
                o.CreatedAt))
            .ToList();

        return new PagedResult<PlatformOrganizationListItem>(items, pageNumber, PageSize, totalCount);
    }

    /// <summary>Phase 24B: the homepage KPI strip's organization breakdown — one bounded
    /// <c>GROUP BY</c>, never a full table scan.</summary>
    public async Task<PlatformOrganizationStatusSummary> GetOrganizationStatusSummaryAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _dbContext.Organizations
            .AsNoTracking()
            .GroupBy(o => o.IsActive)
            .Select(g => new { IsActive = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var active = counts.FirstOrDefault(c => c.IsActive)?.Count ?? 0;
        var inactive = counts.FirstOrDefault(c => !c.IsActive)?.Count ?? 0;
        return new PlatformOrganizationStatusSummary(active, inactive);
    }

    /// <summary>Phase 24B: the homepage KPI strip's platform-wide ticket count — two bounded
    /// <c>COUNT</c> queries, deliberately not a second analytics engine (spec §37: "not a chart-heavy
    /// dashboard").</summary>
    public async Task<PlatformTicketSummary> GetTicketSummaryAsync(CancellationToken cancellationToken = default)
    {
        var total = await _dbContext.Tickets.AsNoTracking().CountAsync(cancellationToken);
        var cutoff = _timeProvider.GetUtcNow().AddDays(-30);
        var recent = await _dbContext.Tickets.AsNoTracking().CountAsync(t => t.CreatedAt >= cutoff, cancellationToken);
        return new PlatformTicketSummary(total, recent);
    }

    /// <summary>Phase 24B: the homepage's own bounded organization list — never the full,
    /// unbounded set (spec §18/§30). Priority ordering: an organization with at least one Pending
    /// account first (spec §19 — the operational signal a Platform Admin needs to see without
    /// clicking through), then active, then inactive; newest first within each group. This never
    /// changes what <see cref="Organization.IsActive"/> means for any one row (spec §16) — it only
    /// decides which rows surface here, using the same read-only "does this org have a Pending
    /// member" fact the Pending Approvals section itself is built from.</summary>
    public async Task<IReadOnlyList<PlatformOrganizationListItem>> GetPriorityOrganizationsAsync(int take, CancellationToken cancellationToken = default)
    {
        var pendingOrganizationIds = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Join(_dbContext.Users.Where(u => u.RegistrationApprovedAt == null && u.RegistrationRejectedAt == null), m => m.UserId, u => u.Id, (m, u) => m.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var page = await _dbContext.Organizations
            .AsNoTracking()
            .Select(o => new { o.Id, o.Name, o.IsActive, o.CreatedAt, HasPendingAccount = pendingOrganizationIds.Contains(o.Id) })
            .OrderByDescending(o => o.HasPendingAccount)
            .ThenByDescending(o => o.IsActive)
            .ThenByDescending(o => o.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var organizationIds = page.Select(o => o.Id).ToArray();

        var memberCounts = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => organizationIds.Contains(m.OrganizationId))
            .GroupBy(m => m.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ticketCounts = await _dbContext.Tickets
            .AsNoTracking()
            .Join(_dbContext.Teams, t => t.TeamId, team => team.Id, (t, team) => new { team.OrganizationId })
            .Where(x => organizationIds.Contains(x.OrganizationId))
            .GroupBy(x => x.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return page
            .Select(o => new PlatformOrganizationListItem(
                o.Id,
                o.Name,
                o.IsActive,
                memberCounts.FirstOrDefault(m => m.OrganizationId == o.Id)?.Count ?? 0,
                ticketCounts.FirstOrDefault(t => t.OrganizationId == o.Id)?.Count ?? 0,
                o.CreatedAt))
            .ToList();
    }

    public async Task<PlatformOrganizationDetail?> GetOrganizationDetailAsync(int organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        var memberCount = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .CountAsync(m => m.OrganizationId == organizationId, cancellationToken);

        var teamCount = await _dbContext.Teams
            .AsNoTracking()
            .CountAsync(t => t.OrganizationId == organizationId, cancellationToken);

        var projectCount = await _dbContext.Projects
            .AsNoTracking()
            .CountAsync(p => p.OrganizationId == organizationId, cancellationToken);

        var ticketCount = await _dbContext.Tickets
            .AsNoTracking()
            .Join(_dbContext.Teams.Where(t => t.OrganizationId == organizationId), t => t.TeamId, team => team.Id, (t, team) => t.Id)
            .CountAsync(cancellationToken);

        return new PlatformOrganizationDetail(
            organization.Id,
            organization.Name,
            organization.IsActive,
            organization.CreatedAt,
            memberCount,
            teamCount,
            projectCount,
            ticketCount);
    }

    /// <summary>Platform-level rename — no uniqueness check, since organization names are
    /// deliberately non-unique (docs/database.md §1a).</summary>
    public async Task<PlatformMutationResult> RenameOrganizationAsync(PlatformAdminIdentity actor, int organizationId, string name, CancellationToken cancellationToken = default)
    {
        var organization = await _dbContext.Organizations.SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            throw new PlatformAccessDeniedException("This organization is not available.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 200)
        {
            return PlatformMutationResult.Failed("Organization name must be between 1 and 200 characters.");
        }

        organization.Rename(trimmed);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>Pending (unaccepted, unexpired) invitations for an organization — a visibility gap
    /// identified after Phase 24's own live use; read-only, revocation remains out of scope.</summary>
    public async Task<IReadOnlyList<PlatformPendingInvitation>> GetPendingInvitationsAsync(int organizationId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return await _dbContext.Invitations
            .AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.AcceptedAt == null && i.ExpiresAt > now)
            .Join(_dbContext.Users, i => i.InvitedByUserId, u => u.Id, (i, u) => new { i.InvitedEmail, i.Role, InvitedByDisplayName = u.DisplayName, i.ExpiresAt })
            .OrderBy(x => x.ExpiresAt)
            .Select(x => new PlatformPendingInvitation(x.InvitedEmail, x.Role.ToString(), x.InvitedByDisplayName, x.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The most recent platform-administration actions taken against this organization —
    /// makes <see cref="PlatformAuditEvent"/> rows (written since Phase 24, never previously
    /// rendered anywhere) actually visible.</summary>
    public async Task<IReadOnlyList<PlatformAuditEventListItem>> GetRecentAuditEventsAsync(int organizationId, int take = 10, CancellationToken cancellationToken = default)
    {
        return await _dbContext.PlatformAuditEvents
            .AsNoTracking()
            .Where(e => e.TargetOrganizationId == organizationId)
            .Join(_dbContext.Users, e => e.ActorUserId, u => u.Id, (e, u) => new { e.Id, e.EventType, ActorDisplayName = u.DisplayName, e.OccurredAt })
            // Id as a secondary key — see PlatformUserService.GetRecentAuditEventsAsync's own
            // comment: two events in the same clock tick otherwise have no deterministic tiebreak.
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new PlatformAuditEventListItem(x.EventType, x.ActorDisplayName, x.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deactivates an organization. Never deletes or alters any membership, team, category,
    /// project, ticket, comment, or event — only <see cref="Organization.IsActive"/> changes
    /// (ADR-0023). This is what makes the organization unreachable for ordinary tenant operation
    /// (<c>CurrentUserAccessor</c> stops resolving any membership into it) without touching a single
    /// historical row.
    /// </summary>
    public async Task<PlatformMutationResult> DeactivateOrganizationAsync(PlatformAdminIdentity actor, int organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _dbContext.Organizations.SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            throw new PlatformAccessDeniedException("This organization is not available.");
        }

        if (!organization.IsActive)
        {
            return PlatformMutationResult.Failed("This organization is already inactive.");
        }

        organization.Deactivate();
        RecordAuditEvent(actor.UserId, PlatformEventType.OrganizationDeactivated, targetOrganizationId: organizationId, targetUserId: null);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    public async Task<PlatformMutationResult> ReactivateOrganizationAsync(PlatformAdminIdentity actor, int organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _dbContext.Organizations.SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            throw new PlatformAccessDeniedException("This organization is not available.");
        }

        if (organization.IsActive)
        {
            return PlatformMutationResult.Failed("This organization is already active.");
        }

        organization.Reactivate();
        RecordAuditEvent(actor.UserId, PlatformEventType.OrganizationReactivated, targetOrganizationId: organizationId, targetUserId: null);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>Stages the audit row on the same tracked <see cref="FlowOpsDbContext"/> as the
    /// lifecycle mutation itself — both are written by the caller's one <c>SaveChangesAsync</c>, so
    /// the action and its audit record are atomic (never one without the other).</summary>
    private void RecordAuditEvent(Guid actorUserId, PlatformEventType eventType, int? targetOrganizationId, Guid? targetUserId)
    {
        _dbContext.PlatformAuditEvents.Add(new PlatformAuditEvent(0, eventType, actorUserId, targetOrganizationId, targetUserId, _timeProvider.GetUtcNow()));
    }
}
