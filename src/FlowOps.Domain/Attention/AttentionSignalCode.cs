namespace FlowOps.Domain.Attention;

/// <summary>Rule ATTN-RULE-02. Exactly the eight signals named in docs/domain-model.md.</summary>
public enum AttentionSignalCode
{
    SlaBreached,
    SlaAtRisk,
    Overdue,
    UnassignedUrgent,
    Aging,
    Stalled,
    Churn,
    Reopened,
}
