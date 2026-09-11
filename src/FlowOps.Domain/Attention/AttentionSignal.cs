namespace FlowOps.Domain.Attention;

/// <summary>Rule ATTN-RULE-01.</summary>
public sealed record AttentionSignal(
    AttentionSignalCode Code,
    AttentionSeverity Severity,
    string Headline,
    DateTimeOffset DetectedAt);
