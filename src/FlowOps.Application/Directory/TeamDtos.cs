using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Directory;

/// <summary>One team already visible in the caller's organization, for the /Admin team list.</summary>
public sealed record TeamListItem(int TeamId, string TeamName, bool IsActive);

/// <summary>The outcome of creating a team — <see cref="Error"/> is a plain, user-facing validation
/// message (name required/too long/already taken), never a <see cref="TeamAccessDeniedException"/>
/// (authorization failures are exceptions, not results — see that type's own doc comment).</summary>
public sealed record CreateTeamResult(bool Succeeded, int? TeamId, string? Error)
{
    public static CreateTeamResult Success(int teamId) => new(true, teamId, null);

    public static CreateTeamResult Failed(string error) => new(false, null, error);
}

/// <summary>The outcome of renaming or deactivating a team — <see cref="Error"/> is a plain,
/// user-facing validation message, never a <see cref="TeamAccessDeniedException"/>
/// (authorization/organization-boundary failures are exceptions, not results).</summary>
public sealed record TeamMutationResult(bool Succeeded, string? Error)
{
    public static TeamMutationResult Success() => new(true, null);

    public static TeamMutationResult Failed(string error) => new(false, error);
}

/// <summary>One member of a team, for the team-detail page — the org role and the
/// team-specific manager flag are two different facts (§6 of the team-membership spec: never
/// confuse <c>OrganizationMembership.Role</c> with <c>TeamMember.IsTeamManager</c>).</summary>
public sealed record TeamMemberListItem(Guid UserId, string DisplayName, string Email, UserRole OrganizationRole, bool IsTeamManager);

/// <summary>A team plus its current membership, for the team-detail page. Returned as
/// <see langword="null"/> when the team does not exist <em>or</em> belongs to a different
/// organization — the same "indistinguishable" non-disclosure shape used everywhere else in this
/// app (e.g. <see cref="FlowOps.Application.Tickets.TicketQueryService.GetDetailAsync"/>).</summary>
public sealed record TeamDetail(int TeamId, string TeamName, bool IsActive, IReadOnlyList<TeamMemberListItem> Members);

/// <summary>One active organization member not yet on the team being managed — the "add member"
/// dropdown's only data source, so a user from another organization or a still-pending invitation
/// can never appear there in the first place.</summary>
public sealed record EligibleMemberOption(Guid UserId, string DisplayName);

/// <summary>The outcome of a team-membership mutation (add/remove/set-manager) —
/// <see cref="Error"/> is a plain, user-facing message for an ordinary, non-tampering failure
/// (duplicate add, already-removed target); an authorization or organization-boundary failure is
/// always a <see cref="TeamAccessDeniedException"/> instead, never a result.</summary>
public sealed record TeamMembershipResult(bool Succeeded, string? Error)
{
    public static TeamMembershipResult Success() => new(true, null);

    public static TeamMembershipResult Failed(string error) => new(false, error);
}
