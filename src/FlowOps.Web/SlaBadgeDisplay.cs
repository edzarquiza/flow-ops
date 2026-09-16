using FlowOps.Domain.Sla;

namespace FlowOps.Web;

/// <summary>Presentation-only tone/icon mapping from <see cref="SlaStatus"/> to the shared badge
/// system — used wherever an SLA state is shown as its own label (Work Queue/At-Risk's SLA
/// column, Ticket Detail's "SLA status" fact). Distinct from <see cref="WorkflowRail.Tone"/>,
/// which colors the rail itself. Both read the same underlying SLA facts; this only picks a
/// badge's color and icon.</summary>
public static class SlaBadgeDisplay
{
    public static SemanticTone Tone(SlaStatus status) => status switch
    {
        SlaStatus.Met => SemanticTone.Ok,
        SlaStatus.Within => SemanticTone.Info,
        SlaStatus.Paused => SemanticTone.Muted,
        SlaStatus.AtRisk => SemanticTone.Warn,
        SlaStatus.Breached => SemanticTone.Danger,
        _ => SemanticTone.Muted,
    };

    public static string Icon(SlaStatus status) => status switch
    {
        SlaStatus.Met => Icons.SlaMet,
        SlaStatus.Within => Icons.SlaWithin,
        SlaStatus.Paused => Icons.SlaPaused,
        SlaStatus.AtRisk => Icons.SlaAtRisk,
        SlaStatus.Breached => Icons.SlaBreached,
        _ => Icons.SlaWithin,
    };
}
