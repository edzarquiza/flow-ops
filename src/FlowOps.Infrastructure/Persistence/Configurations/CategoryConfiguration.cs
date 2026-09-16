using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §4.</summary>
public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", t => t.HasCheckConstraint(
            "ck_categories_default_work_type",
            $"default_work_type IN ('{string.Join("', '", Enum.GetNames<WorkType>())}')"));

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired();
        builder.Property(c => c.DefaultWorkType).HasConversion<string>().IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.IsActive).IsRequired().HasDefaultValue(true);

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(c => c.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        // PERSIST-RULE-05. Phase 22 (ADR-0022): filtered to active rows only, so deactivating a
        // category frees its name for reuse on the same team without touching that row.
        builder.HasIndex(c => new { c.TeamId, c.Name })
            .IsUnique()
            .HasFilter("is_active = true");
    }
}
