using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// docs/database.md §7. Maps the Ticket aggregate without weakening it: no new public setters,
/// no public fields — <c>Id</c>/<c>Reference</c>/etc. keep their existing private setters (EF
/// Core sets them via reflection), and <c>Comments</c>/<c>Events</c>/<c>_statusBeforePending</c>
/// are mapped straight to their private backing fields.
/// </summary>
public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", t =>
        {
            t.HasCheckConstraint("ck_tickets_work_type", EnumCheck("work_type", Enum.GetNames<WorkType>(), allowNull: false));
            t.HasCheckConstraint("ck_tickets_priority", EnumCheck("priority", Enum.GetNames<Priority>(), allowNull: false));
            t.HasCheckConstraint("ck_tickets_status", EnumCheck("status", Enum.GetNames<Status>(), allowNull: false));
            t.HasCheckConstraint("ck_tickets_resolution_code", EnumCheck("resolution_code", Enum.GetNames<Resolution>(), allowNull: true));
            t.HasCheckConstraint("ck_tickets_status_before_pending", EnumCheck("status_before_pending", Enum.GetNames<Status>(), allowNull: true));

            // PERSIST-RULE-03.
            t.HasCheckConstraint("ck_tickets_closed_at", "status <> 'Closed' OR closed_at IS NOT NULL");
            t.HasCheckConstraint(
                "ck_tickets_resolution_present",
                "status NOT IN ('Resolved', 'Closed') OR (resolved_at IS NOT NULL AND resolution_code IS NOT NULL AND resolution_notes IS NOT NULL)");

            // ADR-0029: the backlog flag only means something inside a sprint.
            t.HasCheckConstraint("ck_tickets_sprint_backlog", "sprint_backlog = false OR sprint_id IS NOT NULL");

            // PERSIST-RULE-04.
            t.HasCheckConstraint("ck_tickets_sla_due_after_started", "sla_due_at > sla_started_at");
        });

        builder.HasKey(t => t.Id);

        // TICKET-REF / architecture.md §5: the reference is generated entirely by the database
        // via a sequence-backed default expression — no application-code generation, no second
        // generation mechanism (CLAUDE.md §13). EF Core reads the server-generated value back
        // into Ticket.Reference (a private setter, written via reflection like any other
        // mapped property) after INSERT.
        builder.Property(t => t.Reference)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValueSql("'FO-' || lpad(nextval('ticket_reference_seq')::text, 6, '0')")
            .ValueGeneratedOnAdd();
        builder.Property(t => t.Title).IsRequired();
        builder.Property(t => t.Description).IsRequired();
        builder.Property(t => t.WorkType).HasConversion<string>().IsRequired();
        builder.Property(t => t.Priority).HasConversion<string>().IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().IsRequired();
        builder.Property(t => t.RequesterId).IsRequired();
        builder.Property(t => t.TeamId).IsRequired();
        builder.Property(t => t.CategoryId).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.SlaTargetMinutes).IsRequired();
        builder.Property(t => t.SlaStartedAt).IsRequired();
        builder.Property(t => t.SlaDueAt).IsRequired();
        builder.Property(t => t.SlaPausedMinutes).IsRequired().HasDefaultValue(0);
        builder.Property(t => t.ResolutionCode).HasConversion<string>();
        builder.Property(t => t.ReopenCount).IsRequired().HasDefaultValue(0);
        builder.Property(t => t.AssignmentChangeCount).IsRequired().HasDefaultValue(0);

        // status_before_pending: no public property exists (Phase 2 deliberately keeps it
        // private — see Ticket._statusBeforePending's doc comment and docs/database.md §7).
        // Mapped directly to the backing field with no corresponding property.
        builder.Property<Status?>("_statusBeforePending")
            .HasColumnName("status_before_pending")
            .HasConversion<string?>();

        builder.HasOne<Team>().WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0029: sprint membership. Nullable (existing tickets simply have none); a sprint is
        // never hard-deleted, so RESTRICT.
        builder.Property(t => t.SprintBacklog).IsRequired().HasDefaultValue(false);
        builder.HasOne<FlowOps.Domain.Planning.Sprint>().WithMany().HasForeignKey(t => t.SprintId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => t.SprintId)
            .HasFilter("sprint_id IS NOT NULL")
            .HasDatabaseName("ix_tickets_sprint");

        // TICKET-ENT-04 / architecture.md §5: added in Phase 4 now that Identity exists — see
        // the Phase 3A reconciliation report, which flagged these as deferred.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);

        // AUDIT-RULE-04 / PERSIST-RULE-01: children of the aggregate, cascade-deleted with it.
        builder.HasMany(t => t.Comments)
            .WithOne()
            .HasForeignKey(c => c.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Comments).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(t => t.Events)
            .WithOne()
            .HasForeignKey(e => e.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        // PERSIST-RULE-06 / ADR-0011. A shadow property literally named "xmin" is the current
        // Npgsql EF Core provider convention for mapping PostgreSQL's system column as a
        // concurrency token — it does not create a new column, it binds to the one Postgres
        // already maintains on every row.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(t => t.Reference).IsUnique();

        // §12 indexes — copied verbatim from docs/database.md, one per documented access pattern.
        builder.HasIndex(t => t.SlaDueAt)
            .HasFilter("status NOT IN ('Resolved', 'Closed')")
            .HasDatabaseName("ix_tickets_open_sla_due");

        builder.HasIndex(t => new { t.TeamId, t.Priority })
            .HasFilter("status NOT IN ('Resolved', 'Closed')")
            .HasDatabaseName("ix_tickets_open_team_priority");

        builder.HasIndex(t => t.AssigneeId)
            .HasFilter("status NOT IN ('Resolved', 'Closed')")
            .HasDatabaseName("ix_tickets_open_assignee");

        builder.HasIndex(t => t.CreatedAt)
            .HasFilter("assignee_id IS NULL AND status NOT IN ('Resolved', 'Closed')")
            .HasDatabaseName("ix_tickets_open_unassigned");

        builder.HasIndex(t => t.ResolvedAt)
            .HasDatabaseName("ix_tickets_resolved_at");

        builder.HasIndex(t => t.Title)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("gin_tickets_title_trgm");
    }

    private static string EnumCheck(string column, IReadOnlyCollection<string> values, bool allowNull)
    {
        var inList = $"{column} IN ('{string.Join("', '", values)}')";
        return allowNull ? $"{column} IS NULL OR {inList}" : inList;
    }
}
