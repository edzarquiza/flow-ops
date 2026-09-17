using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from a plain <see cref="Status"/> to a badge tone/icon used in table
/// rows (Work Queue, At-Risk, Ticket Detail) — a status-of-the-work color, distinct from
/// <see cref="WorkflowRail.Tone"/>'s SLA-of-the-moment color used on the rail itself. Both read
/// the same underlying <see cref="Status"/> facts; this decides nothing new.
/// </summary>
/// <remarks>
/// Visual-refresh pass: six genuinely distinct tones, one per status, rather than the earlier
/// neutral-pill/shared-Muted scheme — the product owner's explicit request that each label be
/// visibly its own color, not four names collapsed onto one. Danger (red) is deliberately never
/// used here: it stays reserved for Priority/SLA urgency alone (Principle 1/2,
/// docs/ui/design-system.md), so a status label is never mistaken for an alert. Resolved uses Teal
/// specifically to match the workflow rail's own resolved-stop color — the one place this mapping
/// intentionally echoes an existing convention rather than picking arbitrarily.
/// </remarks>
public static class StatusBadgeDisplay
{
    public static SemanticTone Tone(Status status) => status switch
    {
        Status.Open => SemanticTone.Muted,
        Status.Assigned => SemanticTone.Info,
        Status.InProgress => SemanticTone.Warn,
        Status.Pending => SemanticTone.Violet,
        Status.Resolved => SemanticTone.Teal,
        Status.Closed => SemanticTone.Ok,
        _ => SemanticTone.Muted,
    };

    /// <summary>The <c>.status-badge--*</c> modifier class for this status's tone — e.g.
    /// <c>"status-badge--warn"</c> for <see cref="Status.InProgress"/>.</summary>
    public static string CssClass(Status status) => $"status-badge--{Tone(status).ToString().ToLowerInvariant()}";

    /// <summary>The matching icon from the status progression (Icons.StatusOpen through
    /// Icons.StatusClosed) — a ring that fills clockwise as work advances.</summary>
    public static string Icon(Status status) => status switch
    {
        Status.Open => Icons.StatusOpen,
        Status.Assigned => Icons.StatusAssigned,
        Status.InProgress => Icons.StatusInProgress,
        Status.Pending => Icons.StatusPending,
        Status.Resolved => Icons.StatusResolved,
        Status.Closed => Icons.StatusClosed,
        _ => Icons.StatusOpen,
    };
}
