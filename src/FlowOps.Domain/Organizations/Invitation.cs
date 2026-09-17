using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Organizations;

/// <summary>
/// Phase 18, `ORG-RULE-07`/`08`/`09`/`10`: an outstanding offer to join exactly one
/// <see cref="Organization"/> with a specific <see cref="UserRole"/> — never a membership itself
/// (nothing here grants access; only <see cref="Accept"/> followed by creating a real
/// <see cref="OrganizationMembership"/> does). Single-use and time-boxed: <see cref="Accept"/>
/// refuses an already-accepted or expired invitation, and <see cref="AcceptedAt"/> is the only
/// state <see cref="Accept"/> ever changes — nothing else about an invitation is ever mutated.
/// </summary>
/// <remarks>
/// Concurrency (Step 24/ADR-0017): this entity is mapped with the same PostgreSQL <c>xmin</c>
/// optimistic-concurrency token <c>TicketConfiguration</c> already uses (ADR-0011). Two concurrent
/// acceptance attempts both load the same row, but only the first <c>SaveChangesAsync</c> to
/// commit succeeds; the second's <c>xmin</c> no longer matches and EF Core throws
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> — the same reaction
/// path already established and tested for concurrent ticket transitions, applied here instead of
/// a second, different concurrency mechanism.
/// </remarks>
public sealed class Invitation
{
    /// <summary>Step 4's default lifetime — no existing configuration convention for a duration
    /// like this exists yet, so this is a plain constant, documented here and in ADR-0017.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    public int Id { get; private set; }
    public int OrganizationId { get; private set; }
    public string InvitedEmail { get; private set; } = string.Empty;
    public string NormalizedInvitedEmail { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }

    /// <summary>ADR-0027: the team, if any, this invitation should also add the accepting member
    /// to (alongside the <see cref="OrganizationMembership"/> acceptance always creates). Optional
    /// — an Admin/Viewer invitation reasonably has no team.</summary>
    public int? TeamId { get; private set; }

    public Guid InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool IsAccepted => AcceptedAt.HasValue;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    private Invitation()
    {
    }

    /// <summary>
    /// <paramref name="tokenHash"/> is the only token material this entity ever holds — the raw
    /// token itself is generated and discarded by the caller (<c>InvitationService</c>) immediately
    /// after hashing, per Step 3: the raw token is never persisted.
    /// </summary>
    public static Invitation Create(
        int organizationId,
        string invitedEmail,
        string normalizedInvitedEmail,
        string tokenHash,
        UserRole role,
        Guid invitedByUserId,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        int? teamId = null)
    {
        if (string.IsNullOrWhiteSpace(invitedEmail))
        {
            throw new DomainRuleException("ORG-RULE-09", "An invited email address is required.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new DomainRuleException("ORG-RULE-08", "An invitation token hash is required.");
        }

        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new DomainRuleException("ORG-RULE-10", "Invitation lifetime must be positive.");
        }

        return new Invitation
        {
            OrganizationId = organizationId,
            InvitedEmail = invitedEmail,
            NormalizedInvitedEmail = normalizedInvitedEmail,
            TokenHash = tokenHash,
            Role = role,
            TeamId = teamId,
            InvitedByUserId = invitedByUserId,
            CreatedAt = now,
            ExpiresAt = now + effectiveLifetime,
        };
    }

    /// <summary>`ORG-RULE-08`: single-use. Throws rather than silently no-op-ing, so a caller can
    /// never mistake a rejected re-acceptance for a successful one.</summary>
    public void Accept(DateTimeOffset now)
    {
        if (IsAccepted)
        {
            throw new DomainRuleException("ORG-RULE-08", "This invitation has already been used.");
        }

        if (IsExpired(now))
        {
            throw new DomainRuleException("ORG-RULE-10", "This invitation has expired.");
        }

        AcceptedAt = now;
    }
}
