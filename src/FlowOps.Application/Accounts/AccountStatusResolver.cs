using FlowOps.Domain.Accounts;
using FlowOps.Infrastructure.Identity;

namespace FlowOps.Application.Accounts;

/// <summary>
/// Phase 24A-Extension (ADR-0025): the ONE place the raw (<c>IsActive</c>,
/// <c>RegistrationApprovedAt</c>, <c>RegistrationRejectedAt</c>) columns on <see cref="ApplicationUser"/>
/// become the <see cref="AccountStatus"/> every caller reasons about — <see cref="Application.Platform.PlatformUserService"/>
/// and the Web layer's own <c>LoginModel</c> both need this exact derivation, so it lives here once
/// rather than as two independently-maintained private copies (the "two sources of truth" pattern
/// this codebase has repeatedly found and removed elsewhere).
/// </summary>
public static class AccountStatusResolver
{
    public static AccountStatus Resolve(bool isActive, DateTimeOffset? registrationApprovedAt, DateTimeOffset? registrationRejectedAt) =>
        registrationRejectedAt is not null ? AccountStatus.Rejected
        : registrationApprovedAt is null ? AccountStatus.Pending
        : isActive ? AccountStatus.Active
        : AccountStatus.Inactive;

    public static AccountStatus Resolve(ApplicationUser user) =>
        Resolve(user.IsActive, user.RegistrationApprovedAt, user.RegistrationRejectedAt);
}
