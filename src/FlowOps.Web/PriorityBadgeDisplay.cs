using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>Presentation-only tone/icon mapping for <see cref="Priority"/> — intensity
/// communicated by both color and chevron count/direction, per CLAUDE.md §22 ("never color
/// alone"). The same reusable badge system Status/Role/SLA/Attention-severity use, so all five
/// semantic families read as one system everywhere they appear.</summary>
public static class PriorityBadgeDisplay
{
    public static SemanticTone Tone(Priority priority) => priority switch
    {
        Priority.Critical => SemanticTone.Danger,
        Priority.High => SemanticTone.Warn,
        Priority.Medium => SemanticTone.Info,
        Priority.Low => SemanticTone.Muted,
        _ => SemanticTone.Muted,
    };

    public static string Icon(Priority priority) => priority switch
    {
        Priority.Critical => Icons.PriorityCritical,
        Priority.High => Icons.PriorityHigh,
        Priority.Medium => Icons.PriorityMedium,
        Priority.Low => Icons.PriorityLow,
        _ => Icons.PriorityMedium,
    };
}
