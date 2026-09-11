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
}
