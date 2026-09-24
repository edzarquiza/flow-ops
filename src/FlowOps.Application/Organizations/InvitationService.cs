using System.Buffers.Text;
using System.Security.Cryptography;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Email;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOps.Application.Organizations;

/// <summary>
/// Phase 18: creates and accepts organization invitations. Mirrors
/// <see cref="FlowOps.Application.Tickets.TicketService"/> and
/// <see cref="FlowOps.Application.Accounts.AccountService"/>'s shape — orchestration only, backed
/// by <see cref="OrganizationAccessPolicy"/> for every authorization decision. See ADR-0017 for
/// the token-generation/hashing and concurrency strategy.
/// </summary>
/// <remarks>Phase 30 (ADR-0035): also sends the invitation email — see
/// <see cref="CreateInvitationAsync"/>'s own remarks for the trigger point and failure semantics.</remarks>
public sealed class InvitationService
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILookupNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<InvitationService> _logger;

    /// <summary>
    /// Phase 30 (ADR-0035): <paramref name="emailSender"/>/<paramref name="emailOptions"/> are
    /// required, deliberately — the same "no convenience default" decision <see cref="FlowOps.Application.Tickets.TicketService"/>
    /// makes for the same dependency. <paramref name="logger"/> keeps the pre-existing optional-
    /// with-no-op-default shape that class already established (Phase 5) — this class had no
    /// logger at all before this phase, so there is no existing behavior to preserve either way;
    /// matching that shape is simply consistency, not a weakened contract for a dependency this
    /// phase actually introduces.
    /// </summary>
    public InvitationService(
        FlowOpsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILookupNormalizer normalizer,
        TimeProvider timeProvider,
        IEmailSender emailSender,
        EmailOptions emailOptions,
        ILogger<InvitationService>? logger = null)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _normalizer = normalizer;
        _timeProvider = timeProvider;
        _emailSender = emailSender;
        _emailOptions = emailOptions;
        _logger = logger ?? NullLogger<InvitationService>.Instance;
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

        // Phase 30 (ADR-0035): strictly after the invitation is already committed — an email
        // delivery failure can never roll back a created invitation, and the existing copy-link
        // fallback (Members.cshtml) remains available regardless of whether this succeeds.
        var organizationName = await _dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == inviter.OrganizationId)
            .Select(o => o.Name)
            .SingleAsync(cancellationToken);
        var inviterName = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == inviter.UserId)
            .Select(u => u.DisplayName)
            .SingleAsync(cancellationToken);

        var emailDeliverySucceeded = await TrySendInvitationEmailAsync(
            email, organizationName, inviterName, request.Role, invitation.ExpiresAt, rawToken, inviter.OrganizationId, cancellationToken);

        // Verification pass: whether a live provider is even configured — see CreateInvitationResult's
        // own doc comment for why this is a separate fact from emailDeliverySucceeded above.
        var emailProviderConfigured = string.Equals(_emailOptions.Provider, "Resend", StringComparison.OrdinalIgnoreCase);

        return CreateInvitationResult.Success(rawToken, emailDeliverySucceeded, emailProviderConfigured);
    }

    /// <summary>
    /// Verification pass: every outstanding (not yet accepted) invitation in the caller's own
    /// organization, so an invitation someone didn't immediately copy the link for is still
    /// findable — same visibility gate as the member list itself (<c>ORG-RULE-11</c>: whoever may
    /// manage members may see who's still pending), never <see cref="OrganizationAccessPolicy.CanInvite"/>
    /// alone, since a Manager who can invite an Agent should also see an Admin's own pending
    /// invitations, not only the ones they created themselves. Expired invitations are shown, not
    /// hidden — <paramref name="actor"/> sees the same honest "this lapsed" state a query that
    /// silently dropped rows would have concealed.
    /// </summary>
    public async Task<IReadOnlyList<PendingInvitationView>> GetPendingInvitationsAsync(CurrentUser actor, CancellationToken cancellationToken = default)
    {
        if (!OrganizationAccessPolicy.CanManageMembers(actor))
        {
            throw new OrganizationAccessDeniedException("This role may not view invitations.");
        }

        var now = _timeProvider.GetUtcNow();

        return await _dbContext.Invitations
            .AsNoTracking()
            .Where(i => i.OrganizationId == actor.OrganizationId && i.AcceptedAt == null)
            .Join(_dbContext.Users, i => i.InvitedByUserId, u => u.Id, (i, u) => new { Invitation = i, InviterDisplayName = u.DisplayName })
            .GroupJoin(_dbContext.Teams, x => x.Invitation.TeamId, t => t.Id, (x, teams) => new { x.Invitation, x.InviterDisplayName, Teams = teams })
            .SelectMany(x => x.Teams.DefaultIfEmpty(), (x, team) => new { x.Invitation, x.InviterDisplayName, TeamName = team != null ? team.Name : null })
            .OrderByDescending(x => x.Invitation.CreatedAt)
            .Select(x => new PendingInvitationView(
                x.Invitation.InvitedEmail,
                x.Invitation.Role,
                x.TeamName,
                x.InviterDisplayName,
                x.Invitation.CreatedAt,
                x.Invitation.ExpiresAt,
                x.Invitation.ExpiresAt <= now))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The invitation link is built here, from the one trusted <see cref="EmailOptions.BaseUrl"/>
    /// plus a known, fixed application-relative path — never from a request's Host header or any
    /// other client-supplied input (ADR-0035 Decision 2), since this runs outside any HTTP request
    /// context. Matches <c>Url.Page("/Account/AcceptInvitation", values: new { token })</c>'s own
    /// output exactly (same page, same query parameter name) — Members.cshtml.cs still builds and
    /// shows that identical link on-screen regardless of whether this send succeeds.
    /// </summary>
    private async Task<bool> TrySendInvitationEmailAsync(
        string invitedEmail,
        string organizationName,
        string inviterName,
        UserRole role,
        DateTimeOffset expiresAt,
        string rawToken,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var acceptUrl = $"{_emailOptions.BaseUrl.TrimEnd('/')}/Account/AcceptInvitation?token={Uri.EscapeDataString(rawToken)}";
        var expiresText = expiresAt.ToString("MMMM d, yyyy");

        var textBody =
            $"""
            {inviterName} has invited you to join {organizationName} on FlowOps as a {role}.

            Accept your invitation: {acceptUrl}

            This invitation expires on {expiresText}.

            If you weren't expecting this invitation, you can safely ignore this email.

            — FlowOps
            """;

        var htmlBody =
            $"""
            <p>{System.Net.WebUtility.HtmlEncode(inviterName)} has invited you to join <strong>{System.Net.WebUtility.HtmlEncode(organizationName)}</strong> on FlowOps as a {System.Net.WebUtility.HtmlEncode(role.ToString())}.</p>
            <p><a href="{acceptUrl}">Accept your invitation</a></p>
            <p>This invitation expires on {expiresText}.</p>
            <p>If you weren't expecting this invitation, you can safely ignore this email.</p>
            <p>— FlowOps</p>
            """;

        var message = new EmailMessage(invitedEmail, null, $"You're invited to join {organizationName} on FlowOps", textBody, htmlBody);

        try
        {
            var result = await _emailSender.SendAsync(message, cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Invitation email delivery failed for organization {OrganizationId}: {Error}", organizationId, result.Error);
            }

            return result.Succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invitation email delivery threw an exception.");
            return false;
        }
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
