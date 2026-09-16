using FlowOps.Application.Tickets;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from an already-derived <see cref="Status"/> and
/// <see cref="TicketSlaView"/> onto the six-stop workflow rail (docs/ui/design-system.md §6). Like
/// <see cref="SlaDisplay"/>, this computes no status and no deadline — it only decides where an
/// already-decided value falls on the rail's fixed geometry. The rail's six stops are exactly the
/// workflow state machine's states (CLAUDE.md §5); this class does not introduce a second notion of
/// ticket state, it renders the existing one.
/// </summary>
public static class WorkflowRail
{
    /// <summary>Rail order — identical to CLAUDE.md §5's state machine, left to right.</summary>
    private static readonly Status[] Stops =
    [
        Status.Open,
        Status.Assigned,
        Status.InProgress,
        Status.Pending,
        Status.Resolved,
        Status.Closed,
    ];

    public static int StopCount => Stops.Length;

    /// <summary>The ticket's current position, 0-based left to right.</summary>
    public static int CurrentIndex(Status status) => Array.IndexOf(Stops, status);

    /// <summary>The word shown beneath each stop on the expanded rail.</summary>
    public static string Label(int index) => Label(Stops[index]);

    /// <summary>The same status-name spacing, usable anywhere a bare <see cref="Status"/> — not a
    /// rail position — needs product-facing copy (e.g. the dashboard's status breakdown).</summary>
    public static string Label(Status status) => status switch
    {
        Status.InProgress => "In Progress",
        var s => s.ToString(),
    };

    /// <summary>
    /// Share of the SLA target already consumed, clamped to [0, 1] — the current stop's fill
    /// fraction. Null when there is nothing meaningful to fill (a terminal ticket has no running
    /// clock; docs/ui/design-system.md §6 renders those stops as solid rather than partial).
    /// </summary>
    public static double? ElapsedFraction(TicketSlaView sla)
    {
        if (sla.Remaining is not { } remaining || sla.TargetMinutes <= 0)
        {
            return null;
        }

        var elapsedMinutes = sla.TargetMinutes - remaining.TotalMinutes;
        return Math.Clamp(elapsedMinutes / sla.TargetMinutes, 0, 1);
    }

    /// <summary>
    /// The current stop's rendering tone (docs/ui/design-system.md §6's stop-state table). One
    /// authoritative mapping from <see cref="SlaStatus"/> to the rail's visual language, so no
    /// Razor page re-decides which colour a breach gets.
    /// </summary>
    public static RailTone Tone(Status status, TicketSlaView sla) => status switch
    {
        Status.Resolved or Status.Closed => RailTone.Resolved,
        _ => sla.Status switch
        {
            SlaStatus.Breached => RailTone.Breached,
            SlaStatus.AtRisk => RailTone.AtRisk,
            SlaStatus.Paused => RailTone.Paused,
            _ => RailTone.Healthy,
        },
    };
}

/// <summary>The current rail stop's visual tone — see docs/ui/design-system.md §6.</summary>
public enum RailTone
{
    Healthy,
    AtRisk,
    Breached,
    Paused,
    Resolved,
}

/// <summary>Model for the <c>_WorkflowRail</c> partial — nothing beyond what
/// <see cref="WorkflowRail"/> itself needs to compute stop position, fill, and tone.</summary>
public sealed record RailViewModel(Status Status, TicketSlaView Sla, bool Expanded = false);
