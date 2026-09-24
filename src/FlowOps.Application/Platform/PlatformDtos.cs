using FlowOps.Domain.Accounts;
using FlowOps.Domain.Platform;

namespace FlowOps.Application.Platform;

/// <summary>The authenticated caller's platform-admin identity — resolved fresh from the database
/// on every request by <see cref="PlatformUserAccessor"/>, never trusted from a claim (the same
/// staleness reasoning <c>CurrentUserAccessor</c> already applies to <c>CurrentUser</c>).</summary>
public sealed record PlatformAdminIdentity(Guid UserId, string DisplayName);

/// <summary>One organization, for the platform organizations list — no ticket/member rows loaded,
/// only the counts a platform operator actually needs at a glance.</summary>
public sealed record PlatformOrganizationListItem(
    int OrganizationId,
    string Name,
    bool IsActive,
    int MemberCount,
    int TicketCount,
    DateTimeOffset CreatedAt);

/// <summary>One organization's platform-level detail — deliberately not a full analytics product
/// (CLAUDE.md's own review checklist would flag that as an unjustified abstraction here); a handful
/// of counts plus the fields a platform operator needs to decide whether to deactivate/reactivate.</summary>
public sealed record PlatformOrganizationDetail(
    int OrganizationId,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt,
    int MemberCount,
    int TeamCount,
    int ProjectCount,
    int TicketCount);

/// <summary>One user, for the platform users list.</summary>
public sealed record PlatformUserListItem(
    Guid UserId,
    string Email,
    string DisplayName,
    AccountStatus Status,
    int OrganizationCount,
    DateTimeOffset? CreatedAt);

/// <summary>One membership row on the platform user-detail page — an organization name plus the
/// role the user holds there, never a raw <c>OrganizationMembership</c> id.</summary>
public sealed record PlatformUserMembership(int OrganizationId, string OrganizationName, string Role);

/// <summary>One user's platform-level detail. Never carries a password, password hash, security
/// stamp, or any other credential/token material — see this module's own ADR.</summary>
public sealed record PlatformUserDetail(
    Guid UserId,
    string Email,
    string DisplayName,
    AccountStatus Status,
    IReadOnlyList<PlatformUserMembership> Memberships);

/// <summary>Phase 24A (extended by 24A-Extension for Rejected): the platform-wide account-status
/// breakdown shown at the top of <c>/Platform/Users</c> — one bounded <c>GROUP BY</c>, never a full
/// in-memory scan.</summary>
public sealed record PlatformUserStatusSummary(int Pending, int Active, int Inactive, int Rejected)
{
    public int Total => Pending + Active + Inactive + Rejected;
}

/// <summary>Phase 24B: the platform-wide organization-status breakdown for the homepage KPI strip —
/// one bounded <c>GROUP BY</c>, mirroring <see cref="PlatformUserStatusSummary"/>.</summary>
public sealed record PlatformOrganizationStatusSummary(int Active, int Inactive)
{
    public int Total => Active + Inactive;
}

/// <summary>Phase 24B: a plain platform-wide ticket count for the homepage KPI strip — deliberately
/// not a second analytics engine (CLAUDE.md's own review checklist would flag that); two bounded
/// <c>COUNT</c> queries, nothing else.</summary>
public sealed record PlatformTicketSummary(int Total, int CreatedInLast30Days);

/// <summary>One row on the homepage's Pending Approvals section — an account together with the one
/// piece of organization context that makes it actionable at a glance. <see cref="OrganizationName"/>
/// is the organization created at self-registration (ADR-0016); <see cref="RegisteredAt"/> is that
/// same organization membership's own <c>JoinedAt</c> — the closest real proxy this data model has
/// for "when this account registered," since <c>ApplicationUser</c> itself carries no timestamp
/// (docs/database.md §1). Never a second, independent "organization approval" concept (spec §3):
/// approval remains entirely identity-level — this DTO only borrows organization context for display.</summary>
public sealed record PlatformPendingAccountListItem(
    Guid UserId,
    string Email,
    string DisplayName,
    string? OrganizationName,
    DateTimeOffset RegisteredAt);

/// <summary>The outcome of a platform lifecycle mutation (organization or user
/// deactivate/reactivate) — <see cref="Error"/> is a plain, user-facing message for an ordinary,
/// non-tampering failure (already in the target state, blocked by the sole-admin rule); an
/// authorization or not-found failure is always an exception instead, never a result.</summary>
public sealed record PlatformMutationResult(bool Succeeded, string? Error)
{
    public static PlatformMutationResult Success() => new(true, null);

    public static PlatformMutationResult Failed(string error) => new(false, error);
}

/// <summary>One pending (unaccepted, unexpired) invitation, for the organization detail page's
/// visibility gap identified after Phase 24's live use — a Platform Admin previously had no way to
/// see whether an organization had outstanding invites at all. Read-only: revocation remains
/// intentionally out of scope (ADR-0023's own Phase 24 decision, unchanged).</summary>
public sealed record PlatformPendingInvitation(
    string Email,
    string Role,
    string InvitedByDisplayName,
    DateTimeOffset ExpiresAt);

/// <summary>One platform-administration audit entry, for the "recent activity" section on an
/// organization/user detail page — never a general audit-log product (CLAUDE.md's own review
/// checklist), just the existing <c>PlatformAuditEvent</c> rows made visible, since Phase 24 wrote
/// them but never rendered them anywhere.</summary>
public sealed record PlatformAuditEventListItem(
    PlatformEventType EventType,
    string ActorDisplayName,
    DateTimeOffset OccurredAt);

/// <summary>Phase 30D (ADR-0034): one row of the SLA configuration screen. <see cref="WorkType"/>
/// null means this is the priority's own default row (SLA-RULE-01) — every priority always has
/// exactly one; a non-null <see cref="WorkType"/> is an override that only applies to that exact
/// (WorkType, Priority) pair, resolved first when one exists (see <c>SlaPolicy.ResolveConfiguration</c>).
/// <see cref="IsDefault"/> is the same fact as <c>WorkType is null</c>, surfaced directly so the
/// view never re-derives it — and it is exactly what gates whether a Delete action is offered
/// (the default row for a priority may never be removed).</summary>
public sealed record SlaConfigurationListItem(
    int Id,
    FlowOps.Domain.Tickets.WorkType? WorkType,
    FlowOps.Domain.Tickets.Priority Priority,
    int TargetMinutes,
    int RiskThresholdPercent)
{
    public bool IsDefault => WorkType is null;
}
