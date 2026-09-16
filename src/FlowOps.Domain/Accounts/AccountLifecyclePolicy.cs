namespace FlowOps.Domain.Accounts;

/// <summary>
/// Phase 24A / ADR-0024 (extended by ADR-0025 for Reject): the ONE authoritative place the legal
/// <see cref="AccountStatus"/> transitions are decided — every state-changing Application-layer
/// method (<c>PlatformUserService.ApproveUserAsync</c>/<c>RejectUserAsync</c>/
/// <c>DeactivateUserAsync</c>/<c>ReactivateUserAsync</c>) asks this policy before mutating anything,
/// never re-implements the rule inline. Deliberately a pure, static, no-dependency policy — the
/// same shape as <see cref="Organizations.OrganizationAccessPolicy"/> — so it is unit-testable with
/// zero database/HTTP setup.
/// </summary>
/// <remarks>
/// The legal graph is intentionally small and one-directional except for the Active/Inactive pair:
/// <code>
/// Pending --Approve--&gt;  Active
/// Pending --Reject--&gt;   Rejected
/// Active  --Deactivate--&gt; Inactive
/// Inactive --Reactivate--&gt; Active
/// </code>
/// <see cref="AccountStatus.Pending"/> is a dead end for every transition except Approve/Reject —
/// <b>Reactivate must never be a way to approve a Pending account</b>, <b>Approve must never be a
/// way to reactivate an Inactive one</b>, and — the addition this policy makes explicit —
/// <see cref="AccountStatus.Rejected"/> has no outgoing transition at all: it is terminal by
/// design (ADR-0025). Conflating any of these would let a Platform Admin action on one lifecycle
/// silently bypass another's own gate (the exact ambiguity this product's own spec calls out by name).
/// </remarks>
public static class AccountLifecyclePolicy
{
    /// <summary>Only a truly <see cref="AccountStatus.Pending"/> account may be approved.</summary>
    public static bool CanApprove(AccountStatus current) => current == AccountStatus.Pending;

    /// <summary>Only a truly <see cref="AccountStatus.Pending"/> account may be rejected — never
    /// Active, Inactive, or an already-Rejected account (ADR-0025).</summary>
    public static bool CanReject(AccountStatus current) => current == AccountStatus.Pending;

    /// <summary>Only a currently <see cref="AccountStatus.Active"/> account may be deactivated.</summary>
    public static bool CanDeactivate(AccountStatus current) => current == AccountStatus.Active;

    /// <summary>Only a currently <see cref="AccountStatus.Inactive"/> account may be reactivated —
    /// never a Pending one (that would bypass approval entirely), and never a Rejected one
    /// (Rejected is terminal — see this type's own remarks).</summary>
    public static bool CanReactivate(AccountStatus current) => current == AccountStatus.Inactive;
}
