using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from a plain <see cref="Status"/> to a badge tone/icon used in table
/// rows (Work Queue, At-Risk, Ticket Detail) — a status-of-the-work color, distinct from
/// <see cref="WorkflowRail.Tone"/>'s SLA-of-the-moment color used on the rail itself. Both read
/// the same underlying <see cref="Status"/> facts; this decides nothing new. Pending and Closed
/// deliberately share Muted — both are "inactive" states, and the icon (three dots vs. a dimmed
/// ring) still tells them apart, so color is never the only signal (CLAUDE.md §22).
/// </summary>
public static class StatusBadgeDisplay
{
    public static SemanticTone Tone(Status status) => status switch
    {
        Status.Open => SemanticTone.Info,
        Status.Assigned => SemanticTone.Info,
        Status.InProgress => SemanticTone.Warn,
        Status.Pending => SemanticTone.Muted,
        Status.Resolved => SemanticTone.Ok,
        Status.Closed => SemanticTone.Muted,
        _ => SemanticTone.Muted,
    };

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
