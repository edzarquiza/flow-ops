namespace FlowOps.Domain.Accounts;

/// <summary>
/// Phase 24A/24A-Extension: the four states an account can be in, independent of any
/// <see cref="Organizations.OrganizationMembership.Role"/> — this is a platform-level fact about
/// the identity itself, the same relationship <c>ApplicationUser.IsPlatformAdmin</c> already has to
/// organization role (ADR-0023). Never persisted directly: the authoritative storage is three plain
/// columns on <c>ApplicationUser</c> (Infrastructure), and this enum is the derived read-model
/// <see cref="AccountLifecyclePolicy"/> and the Application layer reason about — see ADR-0024/ADR-0025.
/// </summary>
public enum AccountStatus
{
    /// <summary>Registered, never yet reviewed by a Platform Admin. Cannot authenticate.</summary>
    Pending,

    /// <summary>Approved and not deactivated. May authenticate and use FlowOps normally.</summary>
    Active,

    /// <summary>Was <see cref="Active"/>, then deactivated. Cannot authenticate. Distinct from
    /// <see cref="Pending"/> — an Inactive account was already approved once; reactivating it must
    /// never be confused with approving a Pending one (see <see cref="AccountLifecyclePolicy"/>).</summary>
    Inactive,

    /// <summary>Reviewed during initial approval and not accepted. Cannot authenticate — and never
    /// becomes <see cref="Active"/> through the ordinary Approve/Reactivate paths (ADR-0025).
    /// Deliberately distinct from <see cref="Inactive"/>: Rejected means "never let in in the first
    /// place"; Inactive means "was let in, then removed." The two must never be conflated.</summary>
    Rejected,
}
