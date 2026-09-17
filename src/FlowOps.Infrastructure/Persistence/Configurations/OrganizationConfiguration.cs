using FlowOps.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowOps.Infrastructure.Persistence.Configurations;

/// <summary>Phase 16 multi-tenant foundation ADR.</summary>
public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name).IsRequired();
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(o => o.InviteStepSkippedAt);
        builder.Property(o => o.ProjectStepSkippedAt);

        // Phase 17: deliberately NOT unique. Two unrelated organizations may share a display name
        // (e.g. two different "Acme Support" registrations) — nothing in the product requires
        // organization names to be globally distinct, unlike Team/Project names, which are unique
        // only *within* their own organization (see TeamConfiguration/ProjectConfiguration).
    }
}
