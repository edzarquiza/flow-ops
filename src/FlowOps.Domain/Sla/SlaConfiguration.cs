using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Sla;

/// <summary>
/// Rule SLA-RULE-01. Plain reference data — no aggregate behavior of its own.
/// <see cref="WorkType"/> is null for the priority's default row.
/// </summary>
/// <remarks>
/// Phase 30D (ADR-0034): <see cref="TargetMinutes"/>/<see cref="RiskThresholdPercent"/> gained
/// private setters and <see cref="UpdateTargets"/> — a Platform Admin can now edit these values
/// (previously fixed at their seeded defaults for the whole product's lifetime). Deliberately
/// still global, not organization-scoped (ADR-0015's own explicit, unreversed decision).
/// </remarks>
public sealed class SlaConfiguration
{
    public int Id { get; }
    public WorkType? WorkType { get; }
    public Priority Priority { get; }
    public int TargetMinutes { get; private set; }
    public int RiskThresholdPercent { get; private set; }

    public SlaConfiguration(int id, WorkType? workType, Priority priority, int targetMinutes, int riskThresholdPercent)
    {
        Id = id;
        WorkType = workType;
        Priority = priority;
        TargetMinutes = targetMinutes;
        RiskThresholdPercent = riskThresholdPercent;
    }

    /// <summary>SLA-RULE-03: never retroactive. A ticket already in flight captured its own
    /// <c>SlaTargetMinutes</c>/<c>SlaDueAt</c> at clock start (or at its last priority change) and
    /// is completely untouched by this — only a ticket created, reopened, or priority-changed
    /// after this call resolves against the new values. Bounds (positive minutes, a 1-100 percent
    /// threshold) are the caller's responsibility to validate before calling this, the same
    /// division of labor <c>TeamService.RenameAsync</c>/<c>CatalogService</c> already use for
    /// their own plain-field edits — this method trusts its input.</summary>
    public void UpdateTargets(int targetMinutes, int riskThresholdPercent)
    {
        TargetMinutes = targetMinutes;
        RiskThresholdPercent = riskThresholdPercent;
    }
}
