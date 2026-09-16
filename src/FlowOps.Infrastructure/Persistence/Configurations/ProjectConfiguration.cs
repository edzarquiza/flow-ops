using FlowOps.Domain.Catalog;
using FlowOps.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §5.</summary>
public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Name).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.IsActive).IsRequired().HasDefaultValue(true);

        // Phase 16: uniqueness is now per-organization, not global. Project management phase:
        // filtered to active rows only, so deactivating a project frees its name for reuse (by a
        // rename or a new project) without ever needing to touch the deactivated row itself.
        builder.HasIndex(p => new { p.OrganizationId, p.Name })
            .IsUnique()
            .HasFilter("is_active = true");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
