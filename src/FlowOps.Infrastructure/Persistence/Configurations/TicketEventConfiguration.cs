using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §9. The FK/cascade relationship to Ticket is configured on the
/// Ticket side (<see cref="TicketConfiguration"/>) since Events is Ticket's navigation.</summary>
public sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        builder.ToTable("ticket_events", t => t.HasCheckConstraint(
            "ck_ticket_events_event_type",
            $"event_type IN ('{string.Join("', '", Enum.GetNames<TicketEventType>())}')"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasConversion<string>().IsRequired();
        builder.Property(e => e.ActorUserId).IsRequired();
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.Field);
        builder.Property(e => e.OldValue);
        builder.Property(e => e.NewValue);
        builder.Property(e => e.Note);

        builder.HasIndex(e => new { e.TicketId, e.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ticket_events_ticket");

        // Added in Phase 4 now that Identity exists.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
