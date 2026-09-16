namespace FlowOps.Application.Platform;

/// <summary>
/// Raised when the caller is not a Platform Admin, or when a platform mutation's target
/// organization/user does not exist — the same non-disclosure shape every other
/// <c>*AccessDeniedException</c> in this codebase uses ("not authorized" and "does not exist" are
/// deliberately indistinguishable).
/// </summary>
public sealed class PlatformAccessDeniedException : Exception
{
    public PlatformAccessDeniedException(string message)
        : base(message)
    {
    }
}
