using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Sla;

/// <summary>
/// Rule SLA-RULE-01. Plain reference data — no aggregate behavior of its own.
/// <see cref="WorkType"/> is null for the priority's default row.
/// </summary>
public sealed class SlaConfiguration
{
    public int Id { get; }
    public WorkType? WorkType { get; }
    public Priority Priority { get; }
    public int TargetMinutes { get; }
    public int RiskThresholdPercent { get; }

    public SlaConfiguration(int id, WorkType? workType, Priority priority, int targetMinutes, int riskThresholdPercent)
    {
        Id = id;
        WorkType = workType;
        Priority = priority;
        TargetMinutes = targetMinutes;
        RiskThresholdPercent = riskThresholdPercent;
    }
}
