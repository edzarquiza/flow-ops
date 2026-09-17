using System.Buffers.Text;
using System.Security.Cryptography;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Organizations;

/// <summary>
/// Phase 18: creates and accepts organization invitations. Mirrors
/// <see cref="FlowOps.Application.Tickets.TicketService"/> and
/// <see cref="FlowOps.Application.Accounts.AccountService"/>'s shape — orchestration only, backed
/// by <see cref="OrganizationAccessPolicy"/> for every authorization decision. See ADR-0017 for
/// the token-generation/hashing and concurrency strategy.
/// </summary>
public sealed class InvitationService
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILookupNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;

    public InvitationService(
        FlowOpsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILookupNormalizer normalizer,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _normalizer = normalizer;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// `ORG-RULE-11`: <paramref name="inviter"/> must already be resolved server-side (never a
    /// client-supplied organization id) — see <see cref="FlowOps.Domain.Tickets.CurrentUser"/>.
    /// </summary>
    public async Task<CreateInvitationResult> CreateInvitationAsync(CurrentUser inviter, CreateInvitationRequest request, CancellationToken cancellationToken = default)
    {
        if (!OrganizationAccessPolicy.CanInvite(inviter))
        {
            throw new OrganizationAccessDeniedException("This role may not invite members.");
        }

        if (!OrganizationAccessPolicy.CanInviteRole(inviter, request.Role))
        {
            throw new OrganizationAccessDeniedException($"This role may not invite a {request.Role}.");
        }

        var email = request.Email.Trim();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
        {
            return CreateInvitationResult.Failed(CreateInvitationOutcome.ValidationFailed, "A valid email address is required.");
        }

        // Step 6: the exact same normalization Identity itself uses for email/username lookups —
        // never a hand-rolled comparison rule.
        var normalizedEmail = _normalizer.NormalizeEmail(email) ?? email.ToUpperInvariant();
        var now = _timeProvider.GetUtcNow();

        var alreadyMember = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (m, u) => new { m.OrganizationId, u.NormalizedEmail })
            .AnyAsync(x => x.OrganizationId == inviter.OrganizationId && x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (alreadyMember)
        {
            return CreateInvitationResult.Failed(CreateInvitationOutcome.AlreadyMember, "This email already belongs to a member of this organization.");
        }

        // Step 10: an existing, still-valid (unaccepted, unexpired) invitation blocks a new one —
        // an expired one does not, so re-inviting after expiry always works without any explicit
        // "reissue" action.
        var hasActiveInvitation = await _dbContext.Invitations
            .AsNoTracking()
            .AnyAsync(i => i.OrganizationId == inviter.OrganizationId
                && i.NormalizedInvitedEmail == normalizedEmail
                && i.AcceptedAt == null
                && i.ExpiresAt > now, cancellationToken);
        if (hasActiveInvitation)
        {
            return CreateInvitationResult.Failed(CreateInvitationOutcome.ActiveInvitationExists, "An active invitation already exists for this email.");
        }

        // ADR-0027: an invited team, if named, must be a real, active team in the inviter's own
        // organization — the same "team in organization" check CatalogService's category/project
        // operations already use, never a client-trusted id passed straight through.
        if (request.TeamId is { } teamId)
        {
            var teamInOrganization = await _dbContext.Teams
                .AsNoTracking()
                .AnyAsync(t => t.Id == teamId && t.OrganizationId == inviter.OrganizationId && t.IsActive, cancellationToken);
            if (!teamInOrganization)
            {
                return CreateInvitationResult.Failed(CreateInvitationOutcome.ValidationFailed, "This team is not available to you.");
            }
        }

        // Step 3: 256 bits from the platform's CSPRNG, URL-safe encoded. The raw token is held in
        // a local variable only long enough to hash it and hand it back to the caller once — it is
        // never assigned to any property that could be persisted, logged, or serialized.
        var rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var invitation = Invitation.Create(inviter.OrganizationId, email, normalizedEmail, tokenHash, request.Role, inviter.UserId, now, teamId: request.TeamId);
        _dbContext.Invitations.Add(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreateInvitationResult.Success(rawToken);
    }

    /// <summary>Public, unauthenticated lookup for the accept-invitation page (Step 11) — never
    /// discloses a database id, and treats "does not exist," "expired," and "already used" as
    /// distinct, safe-to-show states rather than a generic failure, since none of them disclose
    /// anything about the organization or user that a legitimate recipient does not already know
    /// from the invitation itself.</summary>
    public async Task<InvitationDetails> GetInvitationDetailsAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var invitation = await FindByRawTokenAsync(rawToken, cancellationToken);
        if (invitation is null)
        {
            return InvitationDetails.NotFound();
        }

        var organizationName = await _dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == invitation.OrganizationId)
            .Select(o => o.Name)
            .SingleAsync(cancellationToken);

        var state = invitation.IsAccepted
            ? InvitationState.Accepted
            : invitation.IsExpired(_timeProvider.GetUtcNow())
                ? InvitationState.Expired
                : InvitationState.Valid;

        return new InvitationDetails(state, organizationName, invitation.InvitedEmail, invitation.Role);
    }

    /// <summary>Step 12: the signed-in caller's own account accepts the invitation. The email
    /// binding (Step 6) is enforced here — the caller's own normalized email must match the
    /// invitation's, regardless of which token they hold.</summary>
    public async Task<AcceptInvitationResult> AcceptForCurrentUserAsync(string rawToken, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var invitation = await FindByRawTokenAsync(rawToken, cancellationToken, forUpdate: true);
        if (invitation is null)
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.NotFound);
        }

        var user = await _userManager.FindByIdAsync(currentUserId.ToString());
        if (user is null)
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.NotFound);
        }

        if (!string.Equals(user.NormalizedEmail, invitation.NormalizedInvitedEmail, StringComparison.Ordinal))
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.EmailMismatch);
        }

        return await AcceptCoreAsync(invitation, user.Id, cancellationToken);
    }

    /// <summary>Step 13: a brand-new account, created for exactly the invited email — the email is
    /// never taken from request input, so there is no field through which a caller could redirect
    /// the invitation to a different address (Step 13's "cannot modify the invited email").</summary>
    public async Task<AcceptInvitationResult> AcceptForNewUserAsync(string rawToken, string fullName, string password, CancellationToken cancellationToken = default)
    {
        var invitation = await FindByRawTokenAsync(rawToken, cancellationToken, forUpdate: true);
        if (invitation is null)
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.NotFound);
        }

        if (invitation.IsAccepted)
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.AlreadyUsed);
        }

        if (invitation.IsExpired(_timeProvider.GetUtcNow()))
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.Expired);
        }

        var trimmedName = fullName.Trim();
        if (trimmedName.Length == 0)
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.ValidationFailed, ["Full name is required."]);
        }

        // ADR-0016's registration transaction technique, reused here for the identical reason:
        // UserManager.CreateAsync commits independently of FlowOpsDbContext's own SaveChangesAsync.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var user = new ApplicationUser
            {
                UserName = invitation.InvitedEmail,
                Email = invitation.InvitedEmail,
                EmailConfirmed = true,
                DisplayName = trimmedName,
                IsActive = true,
                // ADR-0024 "Invitations": an account created by accepting an organization invitation
                // is approved immediately, never Pending. Creating an invitation at all already
                // requires an active, previously-approved organization member with invite
                // permission (OrganizationAccessPolicy.CanInvite, itself only reachable through
                // CurrentUserAccessor, which already refuses a Pending/Inactive caller) — accepting
                // one is trusted organization onboarding, not a new, unvetted identity arriving from
                // the public internet the way self-registration is.
                RegistrationApprovedAt = _timeProvider.GetUtcNow(),
            };

            var createResult = await _userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AcceptInvitationResult.Failed(AcceptInvitationOutcome.ValidationFailed, createResult.Errors.Select(e => e.Description));
            }

            var result = await AcceptCoreAsync(invitation, user.Id, cancellationToken);
            if (!result.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    /// <summary>
    /// The shared accept path once the acting user is known and the email binding is already
    /// verified: consumes the invitation (`Invitation.Accept`, `ORG-RULE-08`) and creates the
    /// membership in one `SaveChangesAsync` — one atomic unit, so a concurrent second acceptance of
    /// the same token can never produce two memberships (see ADR-0017/Invitation's own remarks on
    /// the xmin concurrency token this relies on).
    /// </summary>
    private async Task<AcceptInvitationResult> AcceptCoreAsync(Invitation invitation, Guid userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var alreadyMember = await _dbContext.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == userId, cancellationToken);

        try
        {
            invitation.Accept(now);
        }
        catch (Domain.DomainRuleException ex) when (ex.RuleCode == "ORG-RULE-08")
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.AlreadyUsed);
        }
        catch (Domain.DomainRuleException ex) when (ex.RuleCode == "ORG-RULE-10")
        {
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.Expired);
        }

        // Step 10/14: already being a member (e.g. accepted a second, separate invitation to the
        // same organization) still consumes this invitation — its purpose is already fulfilled —
        // but never creates a second, constraint-violating membership row for the same org/user.
        if (!alreadyMember)
        {
            _dbContext.OrganizationMemberships.Add(new OrganizationMembership(0, invitation.OrganizationId, userId, invitation.Role, now));
        }

        // ADR-0027: root-cause fix for "an invited-and-accepted member can't see the tickets
        // their team already has" — TicketAccessPolicy.CanView requires actual TeamMembers
        // membership for every non-Admin role, and until now accepting an invitation only ever
        // created the OrganizationMembership above, never this. Best-effort, never a hard
        // acceptance failure: the named team may have been deactivated between invite and
        // accept, or the caller may already be on it (e.g. re-accepting a second invitation) —
        // both are silently skipped rather than blocking the membership itself.
        if (invitation.TeamId is { } teamId)
        {
            var teamStillActive = await _dbContext.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId && t.IsActive, cancellationToken);
            var alreadyTeamMember = await _dbContext.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);
            if (teamStillActive && !alreadyTeamMember)
            {
                _dbContext.TeamMembers.Add(new TeamMember(teamId, userId, isTeamManager: false, now));
            }
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Step 5/24: the other of two simultaneous acceptance attempts already committed first
            // — this one loses the race safely, with no membership created and no exception
            // escaping to the caller as a 500.
            return AcceptInvitationResult.Failed(AcceptInvitationOutcome.AlreadyUsed);
        }

        return alreadyMember
            ? AcceptInvitationResult.AlreadyMember(userId, invitation.OrganizationId)
            : AcceptInvitationResult.Success(userId, invitation.OrganizationId);
    }

    private async Task<Invitation?> FindByRawTokenAsync(string rawToken, CancellationToken cancellationToken, bool forUpdate = false)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var query = forUpdate ? _dbContext.Invitations : _dbContext.Invitations.AsNoTracking();
        return await query.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
    }
}
