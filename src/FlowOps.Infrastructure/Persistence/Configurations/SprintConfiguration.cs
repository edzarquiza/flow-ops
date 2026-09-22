using FlowOps.Domain.Catalog;
using FlowOps.Domain.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>ADR-0029. The two invariants that must hold on every code path are constraints, not
/// just application checks: a valid date range, and at most one Active sprint per project.</summary>
public sealed class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> builder)
    {
        builder.ToTable("sprints", t =>
        {
            t.HasCheckConstraint("ck_sprints_status", $"status IN ('{string.Join("', '", Enum.GetNames<SprintStatus>())}')");
            t.HasCheckConstraint("ck_sprints_date_range", "start_date <= end_date");
            t.HasCheckConstraint("ck_sprints_completed_at", "status <> 'Completed' OR completed_at IS NOT NULL");
            t.HasCheckConstraint("ck_sprints_cancelled_at", "status <> 'Cancelled' OR cancelled_at IS NOT NULL");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProjectId).IsRequired();
        builder.Property(s => s.Name).IsRequired().HasMaxLength(80);
        builder.Property(s => s.StartDate).IsRequired();
        builder.Property(s => s.EndDate).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // Two admins starting different sprints at once: the second INSERT/UPDATE fails here, never
        // silently producing two current sprints.
        builder.HasIndex(s => s.ProjectId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_sprints_one_active_per_project");

        builder.HasIndex(s => new { s.ProjectId, s.StartDate })
            .HasDatabaseName("ix_sprints_project_start");

        // ADR-0011: two users completing/starting the same sprint — the loser gets a concurrency
        // conflict, not a silent overwrite.
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
