using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// docs/database.md §6 (SLA-RULE-01, PERSIST-RULE-05). The "(work_type, priority) unique with
/// work_type nullable for the default row" constraint is implemented as two partial unique
/// indexes rather than a COALESCE expression index — docs/database.md itself offers this as an
/// equivalent alternative ("...or a partial index for the NULL case").
/// </summary>
public sealed class SlaConfigurationConfiguration : IEntityTypeConfiguration<SlaConfiguration>
{
    public void Configure(EntityTypeBuilder<SlaConfiguration> builder)
    {
        var workTypeCheck = $"work_type IS NULL OR work_type IN ('{string.Join("', '", Enum.GetNames<WorkType>())}')";
        var priorityCheck = $"priority IN ('{string.Join("', '", Enum.GetNames<Priority>())}')";

        builder.ToTable("sla_configurations", t =>
        {
            t.HasCheckConstraint("ck_sla_configurations_work_type", workTypeCheck);
            t.HasCheckConstraint("ck_sla_configurations_priority", priorityCheck);
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.WorkType).HasConversion<string>();
        builder.Property(c => c.Priority).HasConversion<string>().IsRequired();
        builder.Property(c => c.TargetMinutes).IsRequired();
        builder.Property(c => c.RiskThresholdPercent).IsRequired();

        builder.HasIndex(c => c.Priority)
            .IsUnique()
            .HasFilter("work_type IS NULL")
            .HasDatabaseName("ix_sla_configurations_default_per_priority");

        builder.HasIndex(c => new { c.WorkType, c.Priority })
            .IsUnique()
            .HasFilter("work_type IS NOT NULL")
            .HasDatabaseName("ix_sla_configurations_work_type_priority");

        // SLA-RULE-02's exact seeded defaults, as the (null, Priority) default rows SLA-RULE-01
        // falls back to. Seeded the same way as AUTH-RULE-01's four roles: fixed ids, no
        // randomness, no environment dependence — this is the closed set of reference rows the
        // SLA engine requires to exist at all, not CLAUDE.md §14 demo data. Without these,
        // SlaPolicy.ResolveTargetMinutes throws and no ticket can be created.
        //
        // Anonymous objects rather than SlaConfiguration instances: the entity exposes only
        // getters with a constructor, and HasData needs to set each mapped property directly.
        builder.HasData(
            new { Id = 1, WorkType = (WorkType?)null, Priority = Priority.Critical, TargetMinutes = 240, RiskThresholdPercent = 80 },
            new { Id = 2, WorkType = (WorkType?)null, Priority = Priority.High, TargetMinutes = 480, RiskThresholdPercent = 80 },
            new { Id = 3, WorkType = (WorkType?)null, Priority = Priority.Medium, TargetMinutes = 1440, RiskThresholdPercent = 80 },
            new { Id = 4, WorkType = (WorkType?)null, Priority = Priority.Low, TargetMinutes = 4320, RiskThresholdPercent = 80 });
    }
}
