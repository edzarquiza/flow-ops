using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>docs/database.md §8. The FK/cascade relationship to Ticket is configured on the
/// Ticket side (<see cref="TicketConfiguration"/>) since Comments is Ticket's navigation.</summary>
public sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("ticket_comments");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.AuthorId).IsRequired();
        builder.Property(c => c.Body).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.IsInternal).IsRequired().HasDefaultValue(false);

        builder.HasIndex(c => new { c.TicketId, c.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ticket_comments_ticket");

        // Added in Phase 4 now that Identity exists.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
    }
}
