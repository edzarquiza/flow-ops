namespace FlowOps.Application.Accounts;

/// <summary>Phase 17: the registration form's input, already trimmed/validated at the Web
/// boundary — this record carries exactly what <see cref="AccountService.RegisterAsync"/> needs,
/// nothing a client could use to select an existing organization or a role.</summary>
public sealed record RegisterRequest(string FullName, string Email, string Password, string OrganizationName);

/// <summary>
/// The outcome of a registration attempt. Mirrors <c>Microsoft.AspNetCore.Identity.IdentityResult</c>'s
/// succeeded/errors shape (rather than throwing) because a duplicate email or a rejected password is
/// an ordinary, expected outcome here — not an exceptional one.
/// </summary>
public sealed record RegistrationResult(bool Succeeded, Guid? UserId, int? OrganizationId, IReadOnlyList<string> Errors)
{
    public static RegistrationResult Success(Guid userId, int organizationId) => new(true, userId, organizationId, []);

    public static RegistrationResult Failed(IEnumerable<string> errors) => new(false, null, null, errors.ToList());

    public static RegistrationResult Failed(string error) => Failed([error]);
}

/// <summary>The outcome of an account-deletion attempt — distinguishes the one expected failure
/// mode (the sole-admin safety rule) from success, without throwing for it.</summary>
public sealed record DeleteAccountResult(bool Succeeded, IReadOnlyList<string> BlockedOrganizationNames)
{
    public static DeleteAccountResult Success() => new(true, []);

    public static DeleteAccountResult BlockedBySoleAdmin(IReadOnlyList<string> organizationNames) => new(false, organizationNames);
}
