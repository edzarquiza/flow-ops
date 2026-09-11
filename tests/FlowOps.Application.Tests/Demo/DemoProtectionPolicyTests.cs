using FlowOps.Application.Demo;
using FlowOps.Infrastructure.Identity;
using Xunit;

namespace FlowOps.Application.Tests.Demo;

/// <summary>
/// CLAUDE.md §14's demo-mode guard, tested directly against the policy: no page or service in the
/// repository can mutate a user account yet (see <see cref="DemoProtectionPolicy"/>'s own doc
/// comment), so this proves the rule itself rather than a caller that does not exist.
/// </summary>
public sealed class DemoProtectionPolicyTests
{
    [Fact]
    public void EnsureMutable_ProtectedPersona_Throws()
    {
        var user = new ApplicationUser { IsDemoProtected = true };

        var ex = Assert.Throws<DemoProtectedAccountException>(() => DemoProtectionPolicy.EnsureMutable(user));
        Assert.Contains("protected demo persona", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureMutable_OrdinaryAccount_DoesNotThrow()
    {
        var user = new ApplicationUser { IsDemoProtected = false };

        var exception = Record.Exception(() => DemoProtectionPolicy.EnsureMutable(user));

        Assert.Null(exception);
    }
}
