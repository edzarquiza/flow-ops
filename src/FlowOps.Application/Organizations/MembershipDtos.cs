using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Organizations;

public sealed record MemberListItem(Guid UserId, string DisplayName, string Email, UserRole Role, bool IsActive, bool IsDemoProtected);

public sealed record MembershipActionResult(bool Succeeded, string? Error)
{
    public static MembershipActionResult Success() => new(true, null);

    public static MembershipActionResult Failed(string error) => new(false, error);
}
