using System.Security.Claims;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// Resolves the Domain's <see cref="CurrentUser"/> — the input <see cref="TicketAccessPolicy"/>
/// needs — from the authoritative persistence model, not from client-controlled cookie claims.
/// Role and team membership are both mutable (an Admin can change a user's role or team-manager
/// flag at any time per CLAUDE.md §6.2's privilege-escalation guards), so both are re-read from
/// the database on every call rather than trusted from the authentication cookie. The cookie's
/// own role claim is used only for the coarse <c>[Authorize(Roles=...)]</c> gate (CLAUDE.md
/// §6.2's first layer) — this class is the second, authoritative layer.
/// </summary>
public sealed class CurrentUserAccessor
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public CurrentUserAccessor(FlowOpsDbContext dbContext, UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    /// <summary>
    /// Resolves the caller from their authenticated principal. Only the identifier claim is read
    /// from the cookie — role and team membership still come from the database via the overload
    /// below. Kept here rather than in each PageModel so the "which claim may be trusted"
    /// decision lives in exactly one place.
    /// </summary>
    public async Task<CurrentUser?> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim, out var userId))
        {
            return null;
        }

        return await GetCurrentUserAsync(userId, cancellationToken);
    }

    /// <summary>Returns null if the user does not exist, is inactive, or has no recognized role —
    /// callers must treat null as "not authorized for anything," not as an error to retry.</summary>
    public async Task<CurrentUser?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var roleNames = await _userManager.GetRolesAsync(user);
        var roleName = roleNames.FirstOrDefault();
        if (roleName is null || !Enum.TryParse<UserRole>(roleName, out var role))
        {
            return null;
        }

        var memberships = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.TeamId, m.IsTeamManager })
            .ToListAsync(cancellationToken);

        var memberTeamIds = memberships.Select(m => m.TeamId).ToHashSet();
        var managedTeamIds = memberships.Where(m => m.IsTeamManager).Select(m => m.TeamId).ToHashSet();

        return new CurrentUser(userId, role, memberTeamIds, managedTeamIds);
    }
}
