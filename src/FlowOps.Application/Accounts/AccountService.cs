using FlowOps.Application.Demo;
using FlowOps.Application.Organizations;
using FlowOps.Domain.Accounts;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Accounts;

/// <summary>
/// Phase 17: the individual account lifecycle — registration (which also creates the user's first
/// Organization and its Admin membership, per the product's "Create Account → Create Organization
/// → Creator becomes Admin" flow), profile/email/password changes, and account deletion. Mirrors
/// <see cref="FlowOps.Application.Tickets.TicketService"/>'s shape: orchestration only, no business
/// rule of its own beyond what Identity and the sole-admin safety rule already require.
/// </summary>
/// <remarks>
/// See ADR-0016 for why registration is wrapped in an explicit database transaction rather than a
/// new Unit-of-Work abstraction: <see cref="UserManager{TUser}"/> persists independently of the
/// application's own <see cref="FlowOpsDbContext"/> writes (each Identity store call commits via
/// its own internal <c>SaveChangesAsync</c>), so the smallest correct way to keep "user + organization
/// + membership" atomic is an explicit transaction spanning both, the same technique
/// <see cref="Demo.DemoDataSeeder"/> already uses for its own multi-step, multi-store seeding.
/// </remarks>
public sealed class AccountService
{
    private const int MaxOrganizationNameLength = 200;

    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TimeProvider _timeProvider;

    public AccountService(FlowOpsDbContext dbContext, UserManager<ApplicationUser> userManager, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates the account, its first Organization, and an Admin membership pointing to it, as one
    /// consistent operation (ADR-0016). The organization's role is never taken from the caller —
    /// it is always <see cref="UserRole.Admin"/>, assigned here, server-side, for the organization
    /// this same call just created (never an existing one, since <paramref name="request"/> carries
    /// no organization id at all — there is nothing for a caller to select).
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var organizationName = request.OrganizationName.Trim();
        if (organizationName.Length is 0 or > MaxOrganizationNameLength)
        {
            return RegistrationResult.Failed($"Organization name must be between 1 and {MaxOrganizationNameLength} characters.");
        }

        var fullName = request.FullName.Trim();
        if (fullName.Length is 0)
        {
            return RegistrationResult.Failed("Full name is required.");
        }

        // EnableRetryOnFailure (Program.cs) requires any user-initiated transaction to run inside
        // an execution strategy — same requirement, same technique, as DemoDataSeeder.SeedAsync.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                // No email-verification flow exists in Phase 17 (deliberately not built) — an
                // unconfirmed email would otherwise sit in permanent limbo with nothing to confirm it.
                EmailConfirmed = true,
                DisplayName = fullName,
                // Phase 24A (ADR-0024): a self-registered account starts Pending — RegistrationApprovedAt
                // stays null, and IsActive stays at its default (true), since Pending is not
                // "deactivated," only "not yet approved" (AccountLifecyclePolicy treats the two as
                // distinct states). The account must still genuinely be unable to authenticate before
                // approval, so this reuses the exact same mechanism platform user deactivation
                // already relies on for the identical guarantee (ADR-0023 §5): a permanent Identity
                // lockout, which makes SignInManager.PasswordSignInAsync refuse credentials outright
                // regardless of IsActive. ApproveUserAsync clears it, the same way Reactivate already does.
                LockoutEnabled = true,
                LockoutEnd = DateTimeOffset.MaxValue,
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return RegistrationResult.Failed(createResult.Errors.Select(e => e.Description));
            }

            var now = _timeProvider.GetUtcNow();
            var organization = new Organization(0, organizationName, now);
            _dbContext.Organizations.Add(organization);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _dbContext.OrganizationMemberships.Add(new OrganizationMembership(0, organization.Id, user.Id, UserRole.Admin, now));
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return RegistrationResult.Success(user.Id, organization.Id);
        });
    }

    /// <summary>
    /// Phase 29C: the user's saved appearance. One primary-key lookup, safe to call on every page
    /// render. An unknown user id yields the default (<see cref="AppearancePreference.Dark"/>); a stored
    /// value that is not one of the three names is a data-integrity error and surfaces as one (the
    /// column also has a database check, so it cannot be written through the application).
    /// </summary>
    public async Task<AppearancePreference> GetAppearanceAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var stored = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (AppearancePreference?)u.Appearance)
            .SingleOrDefaultAsync(cancellationToken);

        return stored ?? AppearancePreference.Dark;
    }

    /// <summary>
    /// Saves the caller's own appearance. Personal and harmless, so it is deliberately not gated by
    /// <see cref="DemoProtectionPolicy"/> (demo personas may try both themes) and touches nothing
    /// about authorization. Returns <see langword="false"/> for a value that is not a defined
    /// preference or for an unknown user, changing nothing.
    /// </summary>
    public async Task<bool> SetAppearanceAsync(Guid userId, AppearancePreference appearance, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(appearance))
        {
            return false;
        }

        var updated = await _dbContext.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Appearance, appearance), cancellationToken);

        return updated == 1;
    }

    /// <summary>
    /// Guide first-time discovery cue: atomically consumes the one-shot window on the very first
    /// call for this user (the conditional <c>WHERE ... IS NULL</c> plus <c>SET</c> is one SQL
    /// statement, so two concurrent requests can never both win). Returns <see langword="true"/>
    /// only for the caller that actually flipped it from null — the signal the Dashboard page uses
    /// to decide whether to render the cue at all. Every subsequent call, on any page, in any
    /// session, forever after, returns <see langword="false"/>.
    /// </summary>
    public async Task<bool> TryConsumeFirstGuideCueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var updated = await _dbContext.Users
            .Where(u => u.Id == userId && u.GuideIntroducedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GuideIntroducedAt, now), cancellationToken);

        return updated == 1;
    }

    /// <summary>Updates only the caller's own <see cref="ApplicationUser.DisplayName"/>. Never
    /// touches any historical Ticket/Comment/Event row — those resolve the actor's display name by
    /// joining to this same row at read time (already-established behavior, unchanged here), so a
    /// name change is naturally reflected going forward without rewriting history.</summary>
    public async Task<IdentityResult> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityResult.Failed(new IdentityError { Description = "Account not found." });
        }

        DemoProtectionPolicy.EnsureMutable(user);

        var trimmed = displayName.Trim();
        if (trimmed.Length is 0)
        {
            return IdentityResult.Failed(new IdentityError { Description = "Full name is required." });
        }

        user.DisplayName = trimmed;
        return await _userManager.UpdateAsync(user);
    }

    /// <summary>
    /// Changes both <see cref="ApplicationUser.Email"/> and <see cref="ApplicationUser.UserName"/>
    /// together — the two are kept identical everywhere else in this codebase (demo seeding,
    /// registration above), and login resolves the account via <c>UserManager.FindByNameAsync</c>
    /// under the hood, so leaving <c>UserName</c> behind would silently break sign-in with the new
    /// email. Uniqueness and normalization are both enforced by Identity's own validators inside
    /// <c>SetEmailAsync</c>/<c>SetUserNameAsync</c> — nothing is re-implemented here.
    /// </summary>
    public async Task<IdentityResult> ChangeEmailAsync(Guid userId, string newEmail, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityResult.Failed(new IdentityError { Description = "Account not found." });
        }

        DemoProtectionPolicy.EnsureMutable(user);

        var setEmailResult = await _userManager.SetEmailAsync(user, newEmail);
        if (!setEmailResult.Succeeded)
        {
            return setEmailResult;
        }

        var setUserNameResult = await _userManager.SetUserNameAsync(user, newEmail);
        if (!setUserNameResult.Succeeded)
        {
            return setUserNameResult;
        }

        // No verification flow exists in Phase 17 (by design) — leaving EmailConfirmed false after
        // SetEmailAsync's own reset would put the account in a permanent, meaningless "unconfirmed"
        // state that nothing in this codebase ever checks or resolves.
        user.EmailConfirmed = true;
        return await _userManager.UpdateAsync(user);
    }

    /// <summary>Delegates entirely to Identity's own password infrastructure — current-password
    /// verification, hashing, policy enforcement, and the security-stamp bump that follows a
    /// successful change all happen inside <see cref="UserManager{TUser}.ChangePasswordAsync"/>
    /// itself; nothing is reimplemented here.</summary>
    public async Task<IdentityResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityResult.Failed(new IdentityError { Description = "Account not found." });
        }

        DemoProtectionPolicy.EnsureMutable(user);

        return await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
    }

    /// <summary>
    /// The sole-admin safety rule: a user may not delete their account if doing so would leave any
    /// organization they administer with zero Admin memberships. Evaluated across every
    /// organization the user is an Admin of, not just their current one (a user may be Admin of
    /// several). On success: every <see cref="OrganizationMembership"/> row for this user is
    /// removed (access revoked; the organizations, their other members, and every ticket/comment/
    /// event/project/team/category they own are completely untouched), and the account itself is
    /// deactivated rather than deleted — <see cref="Ticket.RequesterId"/>/<c>AssigneeId</c>,
    /// <see cref="TicketComment.AuthorId"/>, and <see cref="TicketEvent.ActorUserId"/> all carry a
    /// <c>RESTRICT</c> foreign key to this same row (see docs/database.md), so a literal delete
    /// would fail at the database the moment the account has ever touched a single ticket — and
    /// even if it did not, doing so would destroy exactly the historical record this method exists
    /// to preserve. See ADR-0016.
    /// </summary>
    public async Task<DeleteAccountResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return DeleteAccountResult.Success(); // already gone — nothing to do, not an error.
        }

        DemoProtectionPolicy.EnsureMutable(user);

        // ORG-RULE-12, shared with Phase 18's membership role-change/removal — see SoleAdminGuard.
        var blockedOrganizationNames = await SoleAdminGuard.OrganizationsThatWouldLoseTheirLastAdminAsync(_dbContext, userId, cancellationToken);
        if (blockedOrganizationNames.Count > 0)
        {
            return DeleteAccountResult.BlockedBySoleAdmin(blockedOrganizationNames);
        }

        var memberships = await _dbContext.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .ToListAsync(cancellationToken);
        _dbContext.OrganizationMemberships.RemoveRange(memberships);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Deactivation, not deletion (see this method's doc comment). Three independent, existing
        // Identity mechanisms, not manual claim/hash manipulation: IsActive already makes
        // CurrentUserAccessor treat this user as unauthenticatable for every authorization
        // decision; a permanent lockout makes SignInManager.PasswordSignInAsync refuse credentials
        // outright even if correct; a fresh security stamp invalidates any other already-signed-in
        // session on its next validation. The Web layer additionally signs out the *current*
        // session immediately (SignInManager.SignOutAsync) rather than waiting on any of these.
        user.IsActive = false;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user);

        return DeleteAccountResult.Success();
    }
}
