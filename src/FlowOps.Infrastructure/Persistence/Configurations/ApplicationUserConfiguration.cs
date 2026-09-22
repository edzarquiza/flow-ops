using FlowOps.Domain.Accounts;
using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// docs/database.md §1: the four columns FlowOps adds to Identity's own <c>AspNetUsers</c> table
/// (CLAUDE.md §4.2). Everything else about the table (Id, Email, PasswordHash, etc.) is
/// configured by <c>IdentityDbContext</c> itself.
/// </summary>
public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        // Phase 29C: the appearance choice is text (ADR-0009) with a database check, so an invalid
        // value can never be written and, if one ever appeared, would surface as an error rather than
        // being silently treated as some default.
        builder.ToTable("AspNetUsers", t =>
            t.HasCheckConstraint("ck_users_appearance", $"appearance IN ('{string.Join("', '", Enum.GetNames<AppearancePreference>())}')"));
        builder.Property(u => u.Appearance).HasConversion<string>().HasMaxLength(10).IsRequired().HasDefaultValue(AppearancePreference.Dark);

        builder.Property(u => u.DisplayName).IsRequired();
        builder.Property(u => u.JobTitle);
        builder.Property(u => u.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(u => u.IsDemoProtected).IsRequired().HasDefaultValue(false);
        builder.Property(u => u.IsPlatformAdmin).IsRequired().HasDefaultValue(false);
        builder.Property(u => u.RegistrationApprovedAt);
        builder.Property(u => u.RegistrationRejectedAt);

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(u => u.PrimaryTeamId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
