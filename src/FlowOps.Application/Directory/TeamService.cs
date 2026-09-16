using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Directory;

/// <summary>
/// CLAUDE.md §3.3's `Directory` module public surface ("Users, teams, membership, roles" ->
/// `TeamService`), first populated here — team creation for the Workspace Setup checklist's "set
/// up your first team" step (ADR-0020/ADR-0021 made that step reachable; this makes it actually do
/// something). Orchestration only, backed entirely by <see cref="DirectoryAccessPolicy"/> for
/// authorization and the existing <c>teams.(organization_id, name)</c> unique index for naming —
/// no new invariant, no new authorization mechanism.
/// </summary>
public sealed class TeamService
{
    /// <summary>Matches the cap already used for comparable free-text names elsewhere (e.g. the
    /// Organization name at registration) — generous for a real name, short enough to stay a name.</summary>
    public const int MaxNameLength = 100;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public TeamService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>Every team in <paramref name="actor"/>'s own organization — Admin-only, the same
    /// gate as <see cref="CreateAsync"/>, since this is also "manage teams," not a public listing.</summary>
    public async Task<IReadOnlyList<TeamListItem>> GetTeamsAsync(CurrentUser actor, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not view team management.");
        }

        return await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.OrganizationId == actor.OrganizationId)
            .OrderBy(t => t.Name)
            .Select(t => new TeamListItem(t.Id, t.Name, t.IsActive))
            .ToListAsync(cancellationToken);
    }

    /// <summary>AUTH-RULE-01: Admin-only, scoped to the caller's own organization
    /// (<paramref name="actor"/>.OrganizationId is never client-supplied — resolved server-side by
    /// <c>CurrentUserAccessor</c>, exactly like every other organization-scoped write in this app).</summary>
    public async Task<CreateTeamResult> CreateAsync(CurrentUser actor, string name, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not create teams.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CreateTeamResult.Failed($"Team name must be between 1 and {MaxNameLength} characters.");
        }

        // Pre-checked rather than caught from the unique index (the same style
        // InvitationService.CreateInvitationAsync already uses for its own uniqueness checks) —
        // a friendly validation message instead of a raw constraint-violation exception. Scoped to
        // *active* teams only — the unique index itself is filtered the same way (ADR-0022), so a
        // name freed by deactivation is immediately reusable.
        var alreadyExists = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.OrganizationId == actor.OrganizationId && t.IsActive && t.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return CreateTeamResult.Failed("An active team with this name already exists in your organization.");
        }

        var team = new Team(0, actor.OrganizationId, trimmed, _timeProvider.GetUtcNow());
        _dbContext.Teams.Add(team);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent creates for the same (organization, name) pair race on the filtered
            // unique index.
            return CreateTeamResult.Failed("An active team with this name already exists in your organization.");
        }

        return CreateTeamResult.Success(team.Id);
    }

    /// <summary>The team plus its current members, or <see langword="null"/> if it does not exist
    /// or belongs to a different organization — both cases deliberately indistinguishable.</summary>
    public async Task<TeamDetail?> GetTeamDetailAsync(CurrentUser actor, int teamId, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not manage team membership.");
        }

        var team = await _dbContext.Teams
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (team is null)
        {
            return null;
        }

        // One projecting query — team members joined to their user row and their organization
        // role, never a lookup per row. Ordering is applied to the joined anonymous shape, not
        // the already-constructed TeamMemberListItem — EF Core cannot translate an OrderBy
        // against a member of a record built by an earlier Select/Join (the same rule
        // MembershipService.GetMembersAsync's own doc comment documents), so the record is
        // projected last, immediately before materialisation.
        var members = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .Join(
                _dbContext.OrganizationMemberships.Where(om => om.OrganizationId == actor.OrganizationId),
                x => x.u.Id,
                om => om.UserId,
                (x, om) => new { x.m, x.u, om.Role })
            .OrderBy(x => x.u.DisplayName)
            .Select(x => new TeamMemberListItem(x.u.Id, x.u.DisplayName, x.u.Email!, x.Role, x.m.IsTeamManager))
            .ToListAsync(cancellationToken);

        return new TeamDetail(team.Id, team.Name, team.IsActive, members);
    }

    /// <summary>Admin-only, scoped to the caller's own organization. Duplicate-name rejection is
    /// scoped to <em>active</em> teams only — the unique index itself is filtered the same way, so a
    /// name freed by deactivation is immediately reusable (ADR-0022).</summary>
    public async Task<TeamMutationResult> RenameAsync(CurrentUser actor, int teamId, string name, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not rename teams.");
        }

        var team = await _dbContext.Teams.SingleOrDefaultAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (team is null)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return TeamMutationResult.Failed($"Team name must be between 1 and {MaxNameLength} characters.");
        }

        var alreadyExists = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id != teamId && t.OrganizationId == actor.OrganizationId && t.IsActive && t.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return TeamMutationResult.Failed("An active team with this name already exists in your organization.");
        }

        team.Rename(trimmed);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TeamMutationResult.Failed("An active team with this name already exists in your organization.");
        }

        return TeamMutationResult.Success();
    }

    /// <summary>
    /// Deactivates a team — never deletes it, and never cascades to its categories, members, or
    /// tickets (ADR-0022). Existing tickets keep their <c>TeamId</c> and keep displaying this
    /// team's name; existing <c>TeamMember</c> rows and the team's categories are untouched. The
    /// team simply stops being offered for new ticket creation
    /// (<see cref="FlowOps.Application.Tickets.TicketQueryService.GetCreationOptionsAsync"/>'s
    /// active-only filter) and can no longer be selected for a new ticket
    /// (<see cref="FlowOps.Application.Tickets.TicketService.CreateAsync"/>'s own active check).
    /// </summary>
    public async Task<TeamMutationResult> DeactivateAsync(CurrentUser actor, int teamId, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not deactivate teams.");
        }

        var team = await _dbContext.Teams.SingleOrDefaultAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (team is null)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        if (!team.IsActive)
        {
            return TeamMutationResult.Failed("This team is already inactive.");
        }

        team.Deactivate();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return TeamMutationResult.Success();
    }

    /// <summary>Active members of the caller's own organization who are not already on
    /// <paramref name="teamId"/> — the "add member" control's only data source.</summary>
    public async Task<IReadOnlyList<EligibleMemberOption>> GetEligibleMembersAsync(CurrentUser actor, int teamId, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not manage team membership.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        var currentMemberIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        return await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == actor.OrganizationId)
            .Join(_dbContext.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName })
            .Where(x => !currentMemberIds.Contains(x.Id))
            .OrderBy(x => x.DisplayName)
            .Select(x => new EligibleMemberOption(x.Id, x.DisplayName))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Adds an existing, active member of the caller's own organization to a team.
    /// <paramref name="userId"/> must already have a real <c>OrganizationMembership</c> in that
    /// organization — a pending, unaccepted invitation has none, so it is excluded by construction
    /// rather than by a separate check (accepting an invitation is what creates the membership row
    /// in the first place; see <see cref="FlowOps.Application.Organizations.InvitationService"/>).
    /// </summary>
    public async Task<TeamMembershipResult> AddMemberAsync(CurrentUser actor, int teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not manage team membership.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        // A tampered/stale userId (not one of the "add member" control's own options) is refused
        // the same way a tampered CategoryId/ProjectId is in TicketService.CreateAsync — "wrong
        // organization" and "not a real member" are deliberately indistinguishable from "does not
        // exist," and this is what excludes both a cross-organization user and an unaccepted
        // invitation at once.
        var isEligible = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == actor.OrganizationId && m.UserId == userId)
            .Join(_dbContext.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => u.Id)
            .AnyAsync(cancellationToken);
        if (!isEligible)
        {
            throw new TeamAccessDeniedException("This user is not available to add to a team.");
        }

        var alreadyMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .AnyAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);
        if (alreadyMember)
        {
            return TeamMembershipResult.Failed("This person is already on the team.");
        }

        _dbContext.TeamMembers.Add(new TeamMember(teamId, userId, isTeamManager: false, _timeProvider.GetUtcNow()));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent adds for the same (team, user) pair race on the composite primary
            // key (team_id, user_id) — the loser lands here rather than a raw 500 or, worse, a
            // silently duplicated row (the PK makes a duplicate impossible at the database level).
            return TeamMembershipResult.Failed("This person is already on the team.");
        }

        return TeamMembershipResult.Success();
    }

    /// <summary>Removes only the <see cref="TeamMember"/> relationship — never the
    /// <c>ApplicationUser</c>, the <c>OrganizationMembership</c>, or any historical ticket, comment,
    /// or event. Team membership is current work-assignment metadata, not history.</summary>
    public async Task<TeamMembershipResult> RemoveMemberAsync(CurrentUser actor, int teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not manage team membership.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        var membership = await _dbContext.TeamMembers.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return TeamMembershipResult.Failed("That person is not on this team.");
        }

        _dbContext.TeamMembers.Remove(membership);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already removed by a concurrent request (EF's default affected-row-count check
            // catches this on the DELETE) — the same class of race
            // InvitationService.AcceptCoreAsync already handles for its own concurrent-acceptance
            // case, translated to a friendly result instead of an unhandled exception.
            return TeamMembershipResult.Failed("That person is not on this team.");
        }

        return TeamMembershipResult.Success();
    }

    /// <summary>Sets or unsets <see cref="TeamMember.IsTeamManager"/> — a fact about this
    /// particular team, never <see cref="FlowOps.Domain.Tickets.CurrentUser.Role"/> (the
    /// organization-wide role). Changing it changes <c>CurrentUser.ManagedTeamIds</c> the next time
    /// <c>CurrentUserAccessor</c> resolves the caller — <see cref="FlowOps.Domain.Tickets.TicketAccessPolicy"/>
    /// itself is never touched.</summary>
    public async Task<TeamMembershipResult> SetTeamManagerAsync(CurrentUser actor, int teamId, Guid userId, bool isTeamManager, CancellationToken cancellationToken = default)
    {
        if (!DirectoryAccessPolicy.CanManageTeams(actor))
        {
            throw new TeamAccessDeniedException("This role may not manage team membership.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new TeamAccessDeniedException("This team is not available to you.");
        }

        var membership = await _dbContext.TeamMembers.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return TeamMembershipResult.Failed("That person is not on this team.");
        }

        membership.SetManager(isTeamManager);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TeamMembershipResult.Failed("That person is not on this team.");
        }

        return TeamMembershipResult.Success();
    }
}
