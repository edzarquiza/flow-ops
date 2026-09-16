using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Platform;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Infrastructure.Persistence;

/// <summary>
/// The single DbContext for FlowOps (CLAUDE.md §7.1): inherits
/// <see cref="IdentityDbContext{TUser,TRole,TKey}"/> as of Phase 4. The Domain never references
/// this type or <see cref="ApplicationUser"/> — it only ever sees plain <see cref="Guid"/> ids
/// (TICKET-ENT-04). Also implements <see cref="IDataProtectionKeyContext"/> so data protection
/// keys persist to the database (CLAUDE.md §12) — without this, every process restart
/// invalidates every cookie and antiforgery token.
/// </summary>
public sealed class FlowOpsDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IDataProtectionKeyContext
{
    public FlowOpsDbContext(DbContextOptions<FlowOpsDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<SlaConfiguration> SlaConfigurations => Set<SlaConfiguration>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    /// <summary>Exposed for read-side queries only (CLAUDE.md §7.2) — writes always go through
    /// the owning <see cref="Ticket"/> aggregate's <c>AddComment</c> method.</summary>
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();

    /// <summary>Exposed for read-side queries only — writes always go through <see cref="Ticket"/>'s
    /// mutation methods, never inserted/updated/deleted directly (AUDIT-RULE-01).</summary>
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();

    /// <summary>Phase 24 (ADR-0023): append-only, written only by <c>PlatformOrganizationService</c>/
    /// <c>PlatformUserService</c> alongside the lifecycle mutation itself, never updated/deleted.</summary>
    public DbSet<PlatformAuditEvent> PlatformAuditEvents => Set<PlatformAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowOpsDbContext).Assembly);

        // Backs the ILIKE + GIN trigram title search index (CLAUDE.md §7.2, gin_tickets_title_trgm).
        modelBuilder.HasPostgresExtension("pg_trgm");

        // Backs Ticket.Reference's default-value expression in TicketConfiguration — the sole
        // reference-generation mechanism (CLAUDE.md §13, docs/architecture.md §5).
        modelBuilder.HasSequence<int>("ticket_reference_seq").StartsAt(1).IncrementsBy(1);
    }
}
