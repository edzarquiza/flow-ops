using Microsoft.AspNetCore.Identity;

namespace FlowOps.Infrastructure.Identity;

/// <summary>
/// Plain Identity role type — required by <c>IdentityDbContext&lt;ApplicationUser,
/// ApplicationRole, Guid&gt;</c> (CLAUDE.md §7.1). Carries no extra fields: the four roles
/// (AUTH-RULE-01) are a closed, fixed set with no per-role configuration data.
/// </summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName)
        : base(roleName)
    {
    }
}
