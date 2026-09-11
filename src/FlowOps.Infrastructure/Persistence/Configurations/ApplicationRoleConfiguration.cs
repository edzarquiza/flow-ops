using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Seeds AUTH-RULE-01's four fixed roles via migration <c>HasData</c> — deterministic, not
/// environment-dependent "demo data" (CLAUDE.md §14 is a separate, later concept).
/// </summary>
public sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        // ConcurrencyStamp is set explicitly and deterministically — HasData requires every
        // property to have a fixed value; leaving it to IdentityRole's constructor default would
        // generate a new random stamp every time this migration is regenerated, producing a
        // spurious UpdateData diff against no real change.
        builder.HasData(
            new ApplicationRole(WellKnownRoles.Admin) { Id = WellKnownRoles.AdminId, NormalizedName = WellKnownRoles.Admin.ToUpperInvariant(), ConcurrencyStamp = "00000000-0000-0000-0000-1000000000a1" },
            new ApplicationRole(WellKnownRoles.Manager) { Id = WellKnownRoles.ManagerId, NormalizedName = WellKnownRoles.Manager.ToUpperInvariant(), ConcurrencyStamp = "00000000-0000-0000-0000-1000000000a2" },
            new ApplicationRole(WellKnownRoles.Agent) { Id = WellKnownRoles.AgentId, NormalizedName = WellKnownRoles.Agent.ToUpperInvariant(), ConcurrencyStamp = "00000000-0000-0000-0000-1000000000a3" },
            new ApplicationRole(WellKnownRoles.Viewer) { Id = WellKnownRoles.ViewerId, NormalizedName = WellKnownRoles.Viewer.ToUpperInvariant(), ConcurrencyStamp = "00000000-0000-0000-0000-1000000000a4" });
    }
}
