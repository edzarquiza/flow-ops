using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Organizations;

public sealed record CreateInvitationRequest(string Email, UserRole Role, int? TeamId = null);

/// <summary>Verification pass: one outstanding, not-yet-accepted invitation, for the Members page's
/// own "Pending invitations" list — before this existed, an invitation the inviter didn't
/// immediately copy the link for effectively vanished from the UI the moment they navigated away,
/// with no way to find it again short of re-inviting the same address.</summary>
public sealed record PendingInvitationView(
    string Email,
    UserRole Role,
    string? TeamName,
    string InvitedByDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired);

public enum CreateInvitationOutcome
{
    Success,
    AlreadyMember,
    ActiveInvitationExists,
    ValidationFailed,
}

/// <summary>
/// <paramref name="RawToken"/> is the one and only time the raw token is ever available outside a
/// URL a recipient already holds — this service never persists it (Step 3/9), only its hash. The
/// Web layer builds the actual invitation link from this value (still shown on-screen as the
/// existing copy-link fallback); Application stays free of URL/HTTP concerns for the *page* link,
/// even though it now also builds the same link internally for the invitation email itself
/// (ADR-0035, from the trusted configured <c>FlowOps:Email:BaseUrl</c> — never from a request).
/// <paramref name="EmailDeliverySucceeded"/> is <see langword="true"/> whenever an outcome other
/// than <see cref="CreateInvitationOutcome.Success"/> makes it irrelevant — the invitation itself
/// still always succeeds or fails independently of email delivery (ADR-0035 Decision "email
/// delivery is never allowed to roll back the underlying business operation").
/// <paramref name="EmailProviderConfigured"/> (verification pass): distinguishes an environment
/// with no live provider configured (<c>FlowOps:Email:Provider</c> unset or <c>"Log"</c> —
/// <see cref="FlowOps.Infrastructure.Email.LogEmailSender"/> always reports success, since nothing
/// was even attempted) from one where a real send genuinely succeeded or failed. Without this, the
/// Web layer had no way to tell "nothing was ever going to be sent" apart from "it was sent" — both
/// reported <see cref="EmailDeliverySucceeded"/> = <see langword="true"/>, so the UI told every
/// local/CI user their invitation had been emailed when it never left the process.
/// </summary>
public sealed record CreateInvitationResult(CreateInvitationOutcome Outcome, string? RawToken, IReadOnlyList<string> Errors, bool EmailDeliverySucceeded = true, bool EmailProviderConfigured = false)
{
    public bool Succeeded => Outcome == CreateInvitationOutcome.Success;

    public static CreateInvitationResult Success(string rawToken, bool emailDeliverySucceeded, bool emailProviderConfigured) =>
        new(CreateInvitationOutcome.Success, rawToken, [], emailDeliverySucceeded, emailProviderConfigured);

    public static CreateInvitationResult Failed(CreateInvitationOutcome outcome, string error) => new(outcome, null, [error]);
}

public enum InvitationState
{
    NotFound,
    Expired,
    Accepted,
    Valid,
}

/// <summary>The invitation-acceptance page's entire view model — deliberately carries no database
/// id, only what Step 11/27 asks the page to show.</summary>
public sealed record InvitationDetails(InvitationState State, string? OrganizationName, string? InvitedEmail, UserRole? Role)
{
    public static InvitationDetails NotFound() => new(InvitationState.NotFound, null, null, null);
}

public enum AcceptInvitationOutcome
{
    Success,
    NotFound,
    Expired,
    AlreadyUsed,
    EmailMismatch,
    AlreadyMember,
    ValidationFailed,
}

public sealed record AcceptInvitationResult(AcceptInvitationOutcome Outcome, Guid? UserId, int? OrganizationId, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Outcome is AcceptInvitationOutcome.Success or AcceptInvitationOutcome.AlreadyMember;

    public static AcceptInvitationResult Success(Guid userId, int organizationId) => new(AcceptInvitationOutcome.Success, userId, organizationId, []);

    public static AcceptInvitationResult AlreadyMember(Guid userId, int organizationId) => new(AcceptInvitationOutcome.AlreadyMember, userId, organizationId, []);

    public static AcceptInvitationResult Failed(AcceptInvitationOutcome outcome, IEnumerable<string>? errors = null) =>
        new(outcome, null, null, errors?.ToList() ?? []);
}
