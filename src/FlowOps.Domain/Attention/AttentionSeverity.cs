namespace FlowOps.Domain.Attention;

/// <summary>
/// Part of rule ATTN-RULE-02. Declared in ascending order so ordinal comparison
/// (<c>OrderByDescending</c>) yields "most severe first" for ATTN-RULE-04's ranking.
/// </summary>
public enum AttentionSeverity
{
    Medium,
    High,
    Critical,
}
