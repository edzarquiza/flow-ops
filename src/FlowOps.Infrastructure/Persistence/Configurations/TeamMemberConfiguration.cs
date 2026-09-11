using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §3. Composite PK — a user belongs to a team at most once.</summary>
public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_members");

        builder.HasKey(m => new { m.TeamId, m.UserId });

        builder.Property(m => m.IsTeamManager).IsRequired().HasDefaultValue(false);
        builder.Property(m => m.JoinedAt).IsRequired();

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        // Added in Phase 4 now that Identity exists.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
