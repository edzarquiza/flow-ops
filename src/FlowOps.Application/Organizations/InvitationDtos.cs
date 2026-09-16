using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Organizations;

public sealed record CreateInvitationRequest(string Email, UserRole Role);

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
/// Web layer builds the actual invitation link from this value; Application stays free of URL/HTTP
/// concerns.
/// </summary>
public sealed record CreateInvitationResult(CreateInvitationOutcome Outcome, string? RawToken, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Outcome == CreateInvitationOutcome.Success;

    public static CreateInvitationResult Success(string rawToken) => new(CreateInvitationOutcome.Success, rawToken, []);

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
