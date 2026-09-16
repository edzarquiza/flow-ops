using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §2.</summary>
public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.Name).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.IsActive).IsRequired().HasDefaultValue(true);

        // Phase 16: uniqueness is now per-organization, not global — two organizations may each
        // have a "Service Desk" team without colliding. Phase 22 (ADR-0022): filtered to active
        // rows only, so deactivating a team frees its name for reuse without touching that row.
        builder.HasIndex(t => new { t.OrganizationId, t.Name })
            .IsUnique()
            .HasFilter("is_active = true");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(t => t.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
