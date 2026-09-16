using FlowOps.Domain.Organizations;
using FlowOps.Domain.Platform;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>Phase 24 (ADR-0023). Reference/history data, not an aggregate — the same shape as
/// <see cref="Domain.Tickets.TicketEvent"/>, scoped to platform actions instead of one ticket.</summary>
public sealed class PlatformAuditEventConfiguration : IEntityTypeConfiguration<PlatformAuditEvent>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEvent> builder)
    {
        builder.ToTable("platform_audit_events", t =>
        {
            t.HasCheckConstraint(
                "ck_platform_audit_events_event_type",
                $"event_type IN ('{string.Join("', '", Enum.GetNames<PlatformEventType>())}')");

            // Exactly one target per event — never both, never neither.
            t.HasCheckConstraint(
                "ck_platform_audit_events_exactly_one_target",
                "(target_organization_id IS NOT NULL)::int + (target_user_id IS NOT NULL)::int = 1");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasConversion<string>().IsRequired();
        builder.Property(e => e.ActorUserId).IsRequired();
        builder.Property(e => e.OccurredAt).IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.TargetOrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Read pattern: "recent platform activity for this organization/user" — never a full scan.
        builder.HasIndex(e => e.TargetOrganizationId);
        builder.HasIndex(e => e.TargetUserId);
    }
}
