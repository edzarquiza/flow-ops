using System.Security.Claims;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace FlowOps.Application.Platform;

/// <summary>
/// Phase 24 (ADR-0023): the ONE authoritative source for "is this caller a Platform Admin" — every
/// <c>/Platform</c> page calls <see cref="GetCurrentPlatformAdminAsync"/> exactly once and
/// <c>Forbid()</c>s on <see langword="null"/>, the same shape <c>CurrentUserAccessor</c> already
/// established for <c>CurrentUser</c>. Deliberately independent of <c>CurrentUserAccessor</c> and
/// of any <c>OrganizationMembership</c> — platform authority is a fact about the
/// <see cref="ApplicationUser"/> row alone (<see cref="ApplicationUser.IsPlatformAdmin"/>), re-read
/// from the database on every call rather than trusted from a claim (mutable state, same staleness
/// reasoning ADR-0008 already established for organization role).
/// </summary>
public sealed class PlatformUserAccessor
{
    private readonly UserManager<ApplicationUser> _userManager;

    public PlatformUserAccessor(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<PlatformAdminIdentity?> GetCurrentPlatformAdminAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim, out var userId))
        {
            return null;
        }

        return await GetCurrentPlatformAdminAsync(userId, cancellationToken);
    }

    /// <summary>Returns <see langword="null"/> if the user does not exist, is inactive, or does not
    /// hold platform authority — callers must treat null as "not authorized for anything."</summary>
    public async Task<PlatformAdminIdentity?> GetCurrentPlatformAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || !user.IsPlatformAdmin)
        {
            return null;
        }

        return new PlatformAdminIdentity(user.Id, user.DisplayName);
    }
}
