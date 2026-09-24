using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Platform;

/// <summary>
/// Phase 30D (ADR-0034): Platform-level SLA configuration administration — the SLA target
/// minutes/risk threshold every organization's tickets resolve against (SLA-RULE-01) were fixed
/// at their seeded defaults for the whole product's lifetime; this is the first way to edit them.
/// Deliberately global, not organization-scoped — ADR-0015 already made that call explicitly
/// ("no requirement yet for per-org SLA customization"), so this stays under <c>/Platform</c>
/// rather than reopening it. Every method accepts a <see cref="PlatformAdminIdentity"/>, matching
/// every other Platform service's own shape, even where (like <c>PlatformOrganizationService.RenameOrganizationAsync</c>)
/// the actor is not yet written to an audit trail — see this module's own ADR for why SLA changes
/// do not extend <see cref="FlowOps.Domain.Platform.PlatformAuditEvent"/>.
/// </summary>
public sealed class SlaConfigurationService
{
    public const int MinTargetMinutes = 1;
    public const int MinRiskThresholdPercent = 1;
    public const int MaxRiskThresholdPercent = 100;

    private readonly FlowOpsDbContext _dbContext;

    public SlaConfigurationService(FlowOpsDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Every SLA configuration row — the four priority defaults and any per-WorkType
    /// overrides — ordered by Priority, then WorkType (defaults first within a priority, since
    /// <see langword="null"/> sorts before every enum value in this provider's default collation
    /// for a nullable column).</summary>
    public async Task<IReadOnlyList<SlaConfigurationListItem>> GetAllAsync(
        PlatformAdminIdentity actor,
        CancellationToken cancellationToken = default) =>
        await _dbContext.SlaConfigurations
            .AsNoTracking()
            .OrderBy(c => c.Priority).ThenBy(c => c.WorkType)
            .Select(c => new SlaConfigurationListItem(c.Id, c.WorkType, c.Priority, c.TargetMinutes, c.RiskThresholdPercent))
            .ToListAsync(cancellationToken);

    /// <summary>Edits an existing row's target/threshold — never its <c>WorkType</c>/<c>Priority</c>
    /// identity, which would just be a different row entirely (create one, delete the other).
    /// SLA-RULE-03: never retroactive — see <see cref="SlaConfiguration.UpdateTargets"/>.</summary>
    public async Task<PlatformMutationResult> UpdateAsync(
        PlatformAdminIdentity actor,
        int id,
        int targetMinutes,
        int riskThresholdPercent,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(targetMinutes, riskThresholdPercent);
        if (validationError is not null)
        {
            return PlatformMutationResult.Failed(validationError);
        }

        var configuration = await _dbContext.SlaConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (configuration is null)
        {
            return PlatformMutationResult.Failed("This SLA configuration no longer exists.");
        }

        configuration.UpdateTargets(targetMinutes, riskThresholdPercent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    /// <summary>Adds a new per-WorkType override for a priority that does not already have one —
    /// the filtered unique index (<c>ix_sla_configurations_work_type_priority</c>) is the final
    /// guard against a concurrent duplicate, mirroring every other "create" method's own
    /// up-front-check-plus-<see cref="DbUpdateException"/>-catch pattern in this codebase.</summary>
    public async Task<PlatformMutationResult> CreateOverrideAsync(
        PlatformAdminIdentity actor,
        WorkType workType,
        Priority priority,
        int targetMinutes,
        int riskThresholdPercent,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(targetMinutes, riskThresholdPercent);
        if (validationError is not null)
        {
            return PlatformMutationResult.Failed(validationError);
        }

        var alreadyExists = await _dbContext.SlaConfigurations
            .AsNoTracking()
            .AnyAsync(c => c.WorkType == workType && c.Priority == priority, cancellationToken);
        if (alreadyExists)
        {
            return PlatformMutationResult.Failed($"A configuration for {workType} / {priority} already exists.");
        }

        _dbContext.SlaConfigurations.Add(new SlaConfiguration(0, workType, priority, targetMinutes, riskThresholdPercent));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return PlatformMutationResult.Failed($"A configuration for {workType} / {priority} already exists.");
        }

        return PlatformMutationResult.Success();
    }

    /// <summary>Removes a per-WorkType override, reverting that (WorkType, Priority) pair to the
    /// priority's own default row. Never removes a default row itself (<c>WorkType is null</c>) —
    /// every priority must always resolve to something (SLA-RULE-01); without one,
    /// <c>SlaPolicy.ResolveTargetMinutes</c> throws and no ticket of that priority could be created
    /// or reopened. Hard-deleted, not soft-deleted like Team/Category/Project: unlike those, no
    /// ticket ever holds a foreign key to a specific <see cref="SlaConfiguration"/> row — a ticket
    /// only ever copies <c>TargetMinutes</c> out of one, once, at its own SLA clock start
    /// (SLA-RULE-03), so there is nothing left referencing this row for a delete to orphan.</summary>
    public async Task<PlatformMutationResult> DeleteOverrideAsync(
        PlatformAdminIdentity actor,
        int id,
        CancellationToken cancellationToken = default)
    {
        var configuration = await _dbContext.SlaConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (configuration is null)
        {
            return PlatformMutationResult.Failed("This SLA configuration no longer exists.");
        }

        if (configuration.WorkType is null)
        {
            return PlatformMutationResult.Failed("The default configuration for a priority cannot be removed — every priority must always resolve to one.");
        }

        _dbContext.SlaConfigurations.Remove(configuration);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return PlatformMutationResult.Success();
    }

    private static string? Validate(int targetMinutes, int riskThresholdPercent)
    {
        if (targetMinutes < MinTargetMinutes)
        {
            return $"Target minutes must be at least {MinTargetMinutes}.";
        }

        if (riskThresholdPercent is < MinRiskThresholdPercent or > MaxRiskThresholdPercent)
        {
            return $"Risk threshold percent must be between {MinRiskThresholdPercent} and {MaxRiskThresholdPercent}.";
        }

        return null;
    }
}
