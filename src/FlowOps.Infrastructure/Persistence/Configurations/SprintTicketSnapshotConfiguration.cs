using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>ADR-0030. Append-only historical sprint membership: one row per (sprint, ticket),
/// written when the sprint is completed, never updated. Both foreign keys RESTRICT — neither a
/// sprint nor a ticket is ever hard-deleted.</summary>
public sealed class SprintTicketSnapshotConfiguration : IEntityTypeConfiguration<SprintTicketSnapshot>
{
    public void Configure(EntityTypeBuilder<SprintTicketSnapshot> builder)
    {
        builder.ToTable("sprint_ticket_snapshots", t =>
            t.HasCheckConstraint("ck_sprint_ticket_snapshots_status", $"status_at_completion IN ('{string.Join("', '", Enum.GetNames<Status>())}')"));

        builder.HasKey(s => new { s.SprintId, s.TicketId });

        builder.Property(s => s.StatusAtCompletion).HasConversion<string>().IsRequired();
        builder.Property(s => s.WasDone).IsRequired();

        builder.HasOne<Sprint>().WithMany().HasForeignKey(s => s.SprintId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Restrict);

        // "Which sprints did this ticket belong to" — the reverse lookup; the primary key already
        // serves "which tickets belonged to this sprint".
        builder.HasIndex(s => s.TicketId).HasDatabaseName("ix_sprint_ticket_snapshots_ticket");
    }
}
