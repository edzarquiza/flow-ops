using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// Resolves the Domain's <see cref="CurrentUser"/> — the input <see cref="TicketAccessPolicy"/>
/// needs — from the authoritative persistence model, not from client-controlled cookie claims.
/// Role, organization, and team membership are all mutable (an Admin can change a user's role, a
/// user's current organization, or a team-manager flag at any time per CLAUDE.md §6.2's
/// privilege-escalation guards), so all three are re-read from the database on every call rather
/// than trusted from the authentication cookie. The cookie's own role claim is used only for the
/// coarse <c>[Authorize(Roles=...)]</c> gate (CLAUDE.md §6.2's first layer) — this class is the
/// second, authoritative layer.
/// </summary>
/// <remarks>
/// Phase 16: role now comes from <see cref="Domain.Organizations.OrganizationMembership.Role"/>,
/// not from ASP.NET Core Identity's per-user role assignment — a user can belong to more than one
/// organization with a different role in each, so role can no longer live on the user. Identity's
/// own role table is left populated (by <c>DemoDataSeeder</c>/seeding) purely as a mirror for the
/// coarse gate; it is never read here.
///
/// Phase 19 (ADR-0018): "current organization" is now an explicit choice, not a permanent
/// deterministic pick. The choice is carried in a small, Data-Protection-protected cookie
/// (<see cref="CurrentOrganizationCookieName"/>) — a pure CONTEXT value, never an authorization
/// grant. Every read re-validates the cookie's organization id against a real, current
/// <c>OrganizationMembership</c> row before trusting it for anything; an invalid, stale, tampered,
/// or absent cookie falls back to a deterministic safe default (lowest <c>OrganizationId</c> among
/// the caller's real memberships) exactly as Phase 16 always did — the difference is that this
/// fallback is now only a fallback, persisted back into the cookie once chosen, rather than
/// recomputed on every single request once a real selection exists.
/// </remarks>
public sealed class CurrentUserAccessor
{
    /// <summary>Not a secret and not an authorization grant — see class remarks. Named
    /// distinctly from the Identity application cookie so the two can be cleared independently.</summary>
    private const string CurrentOrganizationCookieName = "FlowOps.CurrentOrganization";

    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDataProtectionProvider? _dataProtectionProvider;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// <paramref name="dataProtectionProvider"/>/<paramref name="httpContextAccessor"/> are both
    /// optional (default <c>null</c>) so every existing 2-argument construction — Application.Tests
    /// builds this class directly, with no HTTP request in play at all — keeps compiling and
    /// behaving exactly as before: with no <see cref="HttpContext"/> available, organization
    /// context is resolved by the same deterministic fallback alone, and nothing is ever written
    /// back (there is nowhere to write it to).
    /// </summary>
    public CurrentUserAccessor(
        FlowOpsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IDataProtectionProvider? dataProtectionProvider = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _dataProtectionProvider = dataProtectionProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Resolves the caller from their authenticated principal. Only the identifier claim is read
    /// from the cookie — role, organization, and team membership still come from the database via
    /// the overload below. Kept here rather than in each PageModel so the "which claim may be
    /// trusted" decision lives in exactly one place.
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

    /// <summary>Returns null if the user does not exist, is inactive, or has no organization
    /// membership — callers must treat null as "not authorized for anything," not as an error to
    /// retry.</summary>
    public async Task<CurrentUser?> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive)
        {
            return null;
        }

        // Phase 24 (ADR-0023): a membership in a deactivated organization is never resolvable here
        // — this is the one thing that actually makes organization deactivation "inaccessible for
        // active operation" rather than a cosmetic flag nothing reads. No row is ever deleted; the
        // organization's data is untouched and remains fully visible to Platform Admin's own
        // dedicated queries, which never go through this method.
        var memberships = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(_dbContext.Organizations.Where(o => o.IsActive), m => m.OrganizationId, o => o.Id, (m, o) => new { m.OrganizationId, m.Role })
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
        {
            return null;
        }

        // ORG-RULE-04/14: the cookie is a CONTEXT value, never an authorization shortcut — it is
        // only ever consulted to pick *which* of the caller's own real memberships applies; a
        // value that does not match any of them (tampered, stale, or simply absent) is discarded
        // exactly like "no selection at all", never treated as a hint toward any other organization.
        var selectedOrganizationId = ReadSelectedOrganizationId();
        var membership = selectedOrganizationId is { } selected
            ? memberships.SingleOrDefault(m => m.OrganizationId == selected)
            : null;

        if (membership is null)
        {
            // Deterministic safe default — lowest OrganizationId among real memberships — used
            // only as a fallback now, not the permanent strategy: persisting it below means the
            // *next* request reads this same choice back from the cookie instead of recomputing
            // it, so an explicit later switch is never silently reverted (Step 4).
            membership = memberships.OrderBy(m => m.OrganizationId).First();
            WriteSelectedOrganizationId(membership.OrganizationId);
        }

        // Team membership is scoped to the current organization's own teams — a membership row in
        // another organization's team must never leak into MemberTeamIds/ManagedTeamIds, since
        // TicketAccessPolicy trusts those sets as the caller's complete team footprint.
        var teamMemberships = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(
                _dbContext.Teams.Where(t => t.OrganizationId == membership.OrganizationId),
                m => m.TeamId,
                t => t.Id,
                (m, t) => new { m.TeamId, m.IsTeamManager })
            .ToListAsync(cancellationToken);

        var memberTeamIds = teamMemberships.Select(m => m.TeamId).ToHashSet();
        var managedTeamIds = teamMemberships.Where(m => m.IsTeamManager).Select(m => m.TeamId).ToHashSet();

        return new CurrentUser(userId, membership.OrganizationId, membership.Role, memberTeamIds, managedTeamIds);
    }

    /// <summary>
    /// Step 5: the one, explicit switch operation. Never accepts a role or membership from the
    /// caller — only an organization id, which is verified against a real
    /// <see cref="Domain.Organizations.OrganizationMembership"/> row before anything is persisted.
    /// Returns <c>false</c> (never an exception, never a different error shape) for both "no such
    /// organization" and "real organization the caller simply isn't a member of" — deliberately
    /// indistinguishable, so this can never be used to enumerate organizations (Step 6/29).
    /// </summary>
    public async Task<bool> TrySwitchOrganizationAsync(Guid userId, int organizationId, CancellationToken cancellationToken = default)
    {
        var hasMembership = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.OrganizationId == organizationId)
            .Join(_dbContext.Organizations.Where(o => o.IsActive), m => m.OrganizationId, o => o.Id, (m, o) => m)
            .AnyAsync(cancellationToken);

        if (!hasMembership)
        {
            return false;
        }

        WriteSelectedOrganizationId(organizationId);
        return true;
    }

    /// <summary>
    /// Step 17: the organization switcher's own data source — every organization the caller
    /// belongs to, and nothing else (no other users, no tickets, no teams). One indexed join, not
    /// an analytics query.
    /// </summary>
    public async Task<IReadOnlyList<OrganizationOption>> GetAvailableOrganizationsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(_dbContext.Organizations.Where(o => o.IsActive), m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Name })
            .OrderBy(x => x.Name)
            .Select(x => new OrganizationOption(x.Id, x.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Clears the stored selection — called on logout so a different account signing in
    /// on the same browser can never inherit it (Step 9).</summary>
    public void ClearSelectedOrganization()
    {
        var httpContext = _httpContextAccessor?.HttpContext;
        httpContext?.Response.Cookies.Delete(CurrentOrganizationCookieName, new CookieOptions { Path = "/" });
    }

    private int? ReadSelectedOrganizationId()
    {
        var httpContext = _httpContextAccessor?.HttpContext;
        var protector = _dataProtectionProvider?.CreateProtector(CurrentOrganizationCookieName);
        if (httpContext is null || protector is null)
        {
            return null;
        }

        var cookieValue = httpContext.Request.Cookies[CurrentOrganizationCookieName];
        if (string.IsNullOrEmpty(cookieValue))
        {
            return null;
        }

        try
        {
            var unprotected = protector.Unprotect(cookieValue);
            return int.TryParse(unprotected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var organizationId)
                ? organizationId
                : null;
        }
        catch (CryptographicException)
        {
            // Tampered, corrupted, or produced by a since-rotated key — treated as "no selection
            // at all" (never as a hint), which is exactly as safe as a cookie that was never set.
            return null;
        }
    }

    private void WriteSelectedOrganizationId(int organizationId)
    {
        var httpContext = _httpContextAccessor?.HttpContext;
        var protector = _dataProtectionProvider?.CreateProtector(CurrentOrganizationCookieName);
        if (httpContext is null || protector is null)
        {
            return;
        }

        var protectedValue = protector.Protect(organizationId.ToString(CultureInfo.InvariantCulture));
        httpContext.Response.Cookies.Append(CurrentOrganizationCookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            // Matches the application cookie's own SameAsRequest rationale (Program.cs): Secure
            // whenever the request itself was HTTPS, so this still works over plain HTTP in local
            // dev and every WebApplicationFactory-based test, none of which use TLS.
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(30),
        });
    }
}

/// <summary>One entry in the organization switcher — a name and an id, nothing else. Never
/// exposes a membership id or any other internal identifier.</summary>
public sealed record OrganizationOption(int Id, string Name);
