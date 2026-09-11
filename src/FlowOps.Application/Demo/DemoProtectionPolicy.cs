using FlowOps.Infrastructure.Identity;

namespace FlowOps.Application.Demo;

/// <summary>
/// CLAUDE.md §14's demo-mode guard: "when Demo:Enabled, seeded persona accounts cannot be
/// deleted, renamed, role-changed, or password-changed, by anyone including Admin." This is a
/// single, centralized decision (the same shape as <c>TicketAccessPolicy</c> for tickets) rather
/// than a check repeated wherever a future user-management feature eventually mutates an account.
/// </summary>
/// <remarks>
/// No page or service in the repository can mutate a user account yet — <c>/Admin</c> is still a
/// Phase 4 placeholder (CLAUDE.md §23) — so this policy currently has no caller. It exists now,
/// ahead of that feature, for the same reason <c>TicketAccessPolicy</c> existed before every one
/// of its Web-layer callers did: the rule is part of the demo contract regardless of which phase
/// happens to build the first mutation path, and a future PageModel/service need only call this
/// once to be compliant, rather than re-deriving the rule.
/// </remarks>
public static class DemoProtectionPolicy
{
    /// <exception cref="DemoProtectedAccountException">
    /// <paramref name="user"/> is a protected demo persona.
    /// </exception>
    public static void EnsureMutable(ApplicationUser user)
    {
        if (user.IsDemoProtected)
        {
            throw new DemoProtectedAccountException(
                "This account is a protected demo persona and cannot be deleted, renamed, " +
                "role-changed, or have its password changed.");
        }
    }
}
