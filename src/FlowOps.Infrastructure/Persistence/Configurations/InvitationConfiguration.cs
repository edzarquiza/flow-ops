using FlowOps.Domain.Organizations;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>Phase 18 / ADR-0017.</summary>
public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations", t =>
        {
            t.HasCheckConstraint("ck_invitations_role", EnumCheck("role", Enum.GetNames<Domain.Tickets.UserRole>()));
        });

        builder.HasKey(i => i.Id);

        builder.Property(i => i.InvitedEmail).IsRequired();
        builder.Property(i => i.NormalizedInvitedEmail).IsRequired();

        // ADR-0017: only the SHA-256 hash of the raw token is ever persisted (Step 3) — this
        // column never holds the bearer credential itself.
        builder.Property(i => i.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(i => i.Role).HasConversion<string>().IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Property(i => i.ExpiresAt).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(i => i.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(i => i.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Same PostgreSQL xmin optimistic-concurrency token TicketConfiguration already uses
        // (ADR-0011) — see Invitation's own doc remarks for why this, not a second mechanism.
        builder.Property<uint>("xmin").IsRowVersion();

        // Step 23: efficient lookup for acceptance (by token hash) and for the "does a pending
        // invitation already exist for this email in this organization" check — never a global
        // unique constraint on email, since the same address may legitimately be invited to many
        // organizations. A hash collision between two different real tokens is cryptographically
        // implausible enough that uniqueness here is a correctness canary, not a real constraint
        // the application relies on for security.
        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => new { i.OrganizationId, i.NormalizedInvitedEmail });
    }

    private static string EnumCheck(string column, IReadOnlyCollection<string> values) =>
        $"{column} IN ('{string.Join("', '", values)}')";
}
