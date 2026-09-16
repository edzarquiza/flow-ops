using Microsoft.AspNetCore.Identity;

namespace FlowOps.Infrastructure.Identity;

/// <summary>
/// CLAUDE.md §4.2: "Users live in ASP.NET Core Identity (ApplicationUser : IdentityUser&lt;Guid&gt;)
/// in FlowOps.Infrastructure, carrying DisplayName, JobTitle, IsActive, PrimaryTeamId?." The
/// Domain never references this type — it only ever sees the plain <see cref="Guid"/> id
/// (TICKET-ENT-04).
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public string? JobTitle { get; set; }

    public bool IsActive { get; set; } = true;

    public int? PrimaryTeamId { get; set; }

    /// <summary>CLAUDE.md §14's demo-mode guard: true only for the accounts a demo seeding run
    /// created. Enforced by <see cref="FlowOps.Application.Demo.DemoProtectionPolicy"/>.</summary>
    public bool IsDemoProtected { get; set; }

    /// <summary>
    /// Phase 24 (ADR-0023): platform-wide administrative authority, completely independent of any
    /// <see cref="FlowOps.Domain.Organizations.OrganizationMembership.Role"/> — a user can hold
    /// this flag while belonging to zero, one, or many organizations with any ordinary role in
    /// each. Never read by <c>CurrentUserAccessor</c>, <c>TicketAccessPolicy</c>,
    /// <c>OrganizationAccessPolicy</c>, <c>DirectoryAccessPolicy</c>, or <c>CatalogAccessPolicy</c> —
    /// those remain exactly as before, resolving authority purely from
    /// <c>OrganizationMembership.Role</c>. Only ever set by the <c>grant-platform-admin</c>/
    /// <c>revoke-platform-admin</c> startup CLI commands (see <c>Program.cs</c>) — no tenant-facing
    /// UI, no self-service path, ever writes this flag.
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// Phase 24A (ADR-0024): <see langword="null"/> until a Platform Admin approves this account —
    /// together with <see cref="IsActive"/> this derives the three-state
    /// <see cref="FlowOps.Domain.Accounts.AccountStatus"/> a Platform Admin reasons about: null =
    /// Pending, non-null and <see cref="IsActive"/> = Active, non-null and not
    /// <see cref="IsActive"/> = Inactive. Set exactly once, by
    /// <c>PlatformUserService.ApproveUserAsync</c> — a Pending account created through public
    /// self-registration cannot authenticate until this is set (enforced the same way existing
    /// deactivation already blocks sign-in: a permanent Identity lockout, never a UI-only gate).
    /// An account created through organization invitation acceptance is set to the current time
    /// immediately, since accepting an invitation already implies vetting by that organization's own
    /// (already-approved) member — see ADR-0024 §"Invitations".
    /// </summary>
    public DateTimeOffset? RegistrationApprovedAt { get; set; }

    /// <summary>
    /// Phase 24A-Extension (ADR-0025): <see langword="null"/> unless a Platform Admin rejected this
    /// account during initial review. Together with <see cref="RegistrationApprovedAt"/> and
    /// <see cref="IsActive"/> this derives the four-state <see cref="FlowOps.Domain.Accounts.AccountStatus"/>:
    /// this field set = Rejected (regardless of the other two — mutually exclusive with
    /// <see cref="RegistrationApprovedAt"/> by construction, since only a Pending account, where
    /// both are still null, may ever be rejected — <c>AccountLifecyclePolicy.CanReject</c>). Set
    /// exactly once, by <c>PlatformUserService.RejectUserAsync</c>. A rejected account remains
    /// permanently locked out (the same lockout registration itself already set) — never becomes
    /// Active through the ordinary Approve/Reactivate paths.
    /// </summary>
    public DateTimeOffset? RegistrationRejectedAt { get; set; }
}
