using FlowOps.Application.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// First-run workspace setup (ADR-0020, revised by ADR-0026): both the read side (derived
/// checklist state) and the two skip mutations live here rather than on
/// <see cref="AnalyticsQueryService"/> — that class is read-only by its own doc comment, and skip
/// is a write. Deliberately narrow (two methods plus the status query, no generic onboarding
/// abstraction) — the same restraint ADR-0020 itself insisted on.
/// </summary>
public sealed class WorkspaceSetupService
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public WorkspaceSetupService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// ADR-0020/ADR-0026: bounded existence checks scoped to <paramref name="user"/>'s own
    /// organization, never a loaded collection counted in memory, plus that organization's two
    /// persisted skip flags. Entirely reflects current data on every call — nothing here is
    /// cached, so this is correct immediately after switching organizations, sending an
    /// invitation, or skipping a step. Callers should skip calling this at all for a non-Admin or
    /// the demo organization (see Index.cshtml.cs) rather than pay for the queries nobody will see.
    /// </summary>
    public async Task<WorkspaceSetupStatus> GetWorkspaceSetupStatusAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var hasTeam = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.OrganizationId == user.OrganizationId, cancellationToken);

        // Ticket has no OrganizationId column of its own (it inherits organization transitively
        // through Team, like Category) — the same join-based boundary ApplyAnalyticsScope/
        // TicketQueryService.ApplyViewScope already use, not a second definition of it.
        var hasTicket = await _dbContext.Tickets
            .AsNoTracking()
            .Join(_dbContext.Teams, t => t.TeamId, team => team.Id, (t, team) => team.OrganizationId)
            .AnyAsync(organizationId => organizationId == user.OrganizationId, cancellationToken);

        var hasProject = await _dbContext.Projects
            .AsNoTracking()
            .AnyAsync(p => p.OrganizationId == user.OrganizationId, cancellationToken);

        // "Active" matches the same definition the Members page itself shows (ApplicationUser.IsActive)
        // — capped with Take(2) before CountAsync, so the database only ever has to find at most two
        // matching rows regardless of how large the organization is; the caller only needs to know
        // whether the count exceeds one, never the true count.
        var activeMemberCount = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == user.OrganizationId)
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (m, u) => u.IsActive)
            .Where(isActive => isActive)
            .Take(2)
            .CountAsync(cancellationToken);

        // ADR-0026: "has sent an invitation" counts regardless of its current status (pending,
        // accepted, or expired) — the checklist rewards the Admin's own action of inviting, not an
        // outcome outside their control. See WorkspaceSetupStatus's own doc comment.
        var hasSentInvitation = await _dbContext.Invitations
            .AsNoTracking()
            .AnyAsync(i => i.OrganizationId == user.OrganizationId, cancellationToken);

        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .SingleAsync(o => o.Id == user.OrganizationId, cancellationToken);

        return new WorkspaceSetupStatus(
            hasTeam,
            hasSentInvitation || activeMemberCount > 1,
            organization.InviteStepSkippedAt is not null,
            hasProject,
            organization.ProjectStepSkippedAt is not null,
            hasTicket);
    }

    public async Task SkipInviteStepAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var organization = await LoadForAdminAsync(user, cancellationToken);
        organization.SkipInviteStep(_timeProvider.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SkipProjectStepAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var organization = await LoadForAdminAsync(user, cancellationToken);
        organization.SkipProjectStep(_timeProvider.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>ADR-0020: only an Admin can act on any setup item — enforced here too (not only by
    /// the calling PageModel) so this service is safe to call directly regardless of caller.</summary>
    private async Task<Domain.Organizations.Organization> LoadForAdminAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Role != UserRole.Admin)
        {
            throw new OrganizationAccessDeniedException("This role may not change workspace setup state.");
        }

        return await _dbContext.Organizations.SingleAsync(o => o.Id == user.OrganizationId, cancellationToken);
    }
}
