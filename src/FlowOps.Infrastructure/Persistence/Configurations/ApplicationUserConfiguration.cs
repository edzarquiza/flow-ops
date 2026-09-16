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
