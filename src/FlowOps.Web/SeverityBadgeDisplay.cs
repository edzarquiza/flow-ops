using FlowOps.Domain.Attention;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only tone/icon mapping for <see cref="AttentionSeverity"/> — the Dashboard
/// Attention section and At-Risk's "why it needs attention" list. Deliberately reuses the same
/// tones and icons <see cref="PriorityBadgeDisplay"/> uses for Priority's own Critical/High/Medium
/// tier (AttentionSeverity has no Low), so a "Critical" reads identically whether it names a
/// ticket's priority or an attention signal's severity — one reusable badge system, not a
/// dashboard-specific one-off.
/// </summary>
public static class SeverityBadgeDisplay
{
    public static SemanticTone Tone(AttentionSeverity severity) => severity switch
    {
        AttentionSeverity.Critical => SemanticTone.Danger,
        AttentionSeverity.High => SemanticTone.Warn,
        AttentionSeverity.Medium => SemanticTone.Info,
        _ => SemanticTone.Info,
    };

    public static string Icon(AttentionSeverity severity) => severity switch
    {
        AttentionSeverity.Critical => Icons.PriorityCritical,
        AttentionSeverity.High => Icons.PriorityHigh,
        AttentionSeverity.Medium => Icons.PriorityMedium,
        _ => Icons.PriorityMedium,
    };
}
