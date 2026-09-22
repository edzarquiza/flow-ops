using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from an already-merged <see cref="TicketTimelineEntry"/> onto a
/// timeline dot tone (docs/ui/design-system.md §8, Timeline). Same pattern as
/// <see cref="WorkflowRail"/>: it decides nothing about the ticket, it only picks a colour for a
/// value <see cref="FlowOps.Domain.Attention.AttentionPolicy"/> and the workflow methods on
/// <c>Ticket</c> already produced. Reads only <see cref="TicketTimelineEntry.Kind"/> and
/// <see cref="TicketTimelineEntry.EventType"/> — both closed, reliable, typed fields already on the
/// DTO — never the free-text <c>Note</c>/<c>Body</c>/<c>Field</c> values, so this never infers a
/// business meaning from arbitrary text.
/// </summary>
public static class TimelinePresentation
{
    public static TimelineTone Tone(TicketTimelineEntry entry)
    {
        if (entry.Kind == TicketTimelineEntryKind.Comment)
        {
            return TimelineTone.Comment;
        }

        return entry.EventType switch
        {
            // The two events that actually end the ticket's life — the same meaning the rail's
            // own resolved stop carries, so the two stay visually consistent.
            TicketEventType.Resolved or TicketEventType.Closed => TimelineTone.Resolved,
            // A step backward: work that was done is being reopened.
            TicketEventType.Reopened => TimelineTone.Warn,
            // The SLA clock stops here — same neutral-grey treatment the rail gives a paused stop.
            TicketEventType.PutOnHold => TimelineTone.Paused,
            // Forward movement that isn't a terminal outcome.
            TicketEventType.Assigned or TicketEventType.Reassigned or TicketEventType.Resumed or TicketEventType.StatusChanged => TimelineTone.Info,
            _ => TimelineTone.Neutral,
        };
    }
}

/// <summary>A timeline entry's dot tone — see docs/ui/design-system.md §8.</summary>
public enum TimelineTone
{
    Neutral,
    Info,
    Warn,
    Paused,
    Resolved,
    Comment,
}

/// <summary>
/// Plain-language title and detail for an audit-history entry. Presentation only: the audit record
/// keeps its technical event types and stored values; ids are resolved to names by the query
/// (<see cref="TicketTimelineEntry.OldDisplay"/>/<see cref="TicketTimelineEntry.NewDisplay"/>), and
/// anything unresolved falls back to a neutral word rather than a raw id.
/// </summary>
public static class TimelineDisplay
{
    public static string Title(TicketTimelineEntry entry)
    {
        if (entry.Kind == TicketTimelineEntryKind.Comment)
        {
            return entry.IsInternal == true ? "Internal comment" : "Comment";
        }

        return entry.EventType switch
        {
            TicketEventType.Created => "Created",
            TicketEventType.Assigned => "Assigned",
            TicketEventType.Reassigned => "Reassigned",
            TicketEventType.Unassigned => "Unassigned",
            TicketEventType.StatusChanged => "Status changed",
            TicketEventType.PriorityChanged => "Priority changed",
            TicketEventType.CategoryChanged => "Category changed",
            TicketEventType.TeamChanged => "Team changed",
            TicketEventType.DueDateChanged => "Due date changed",
            TicketEventType.SlaRecalculated => "Deadline recalculated",
            TicketEventType.PutOnHold => "Put on hold",
            TicketEventType.Resumed => "Resumed",
            TicketEventType.Resolved => "Resolved",
            TicketEventType.Reopened => "Reopened",
            TicketEventType.Closed => "Closed",
            TicketEventType.CommentAdded => "Comment added",
            TicketEventType.SprintChanged => SprintTitle(entry),
            _ => "Updated",
        };
    }

    public static string? Detail(TicketTimelineEntry entry)
    {
        if (entry.Kind == TicketTimelineEntryKind.Comment)
        {
            return entry.Body;
        }

        var note = string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note;
        return entry.EventType switch
        {
            TicketEventType.Assigned => $"To {entry.NewDisplay ?? "a team member"}",
            TicketEventType.Reassigned => $"{entry.OldDisplay ?? "Unassigned"} → {entry.NewDisplay ?? "a team member"}",
            TicketEventType.Unassigned => entry.OldDisplay is null ? null : $"Was {entry.OldDisplay}",
            TicketEventType.StatusChanged => $"{StatusName(entry.OldValue)} → {StatusName(entry.NewValue)}",
            TicketEventType.PriorityChanged => $"{entry.OldValue ?? "—"} → {entry.NewValue ?? "—"}",
            TicketEventType.CategoryChanged => $"{entry.OldDisplay ?? "Unknown"} → {entry.NewDisplay ?? "Unknown"}",
            TicketEventType.TeamChanged => $"{entry.OldDisplay ?? "Unknown"} → {entry.NewDisplay ?? "Unknown"}",
            TicketEventType.DueDateChanged => $"{DateText(entry.OldValue)} → {DateText(entry.NewValue)}",
            TicketEventType.SprintChanged when entry.Field == "SprintId" => $"{entry.OldDisplay ?? "No sprint"} → {entry.NewDisplay ?? "No sprint"}",
            _ => note,
        };
    }

    private static string SprintTitle(TicketTimelineEntry entry)
    {
        if (entry.Field == "SprintBacklog")
        {
            // Planned for the sprint (flag true) versus pulled onto the board, into Open (flag false).
            return string.Equals(entry.NewValue, bool.TrueString, StringComparison.OrdinalIgnoreCase)
                ? "Moved back to planned"
                : "Moved to Open";
        }

        return entry.NewDisplay is { } name ? $"Moved to {name}" : "Moved out of sprint";
    }

    private static string StatusName(string? value) =>
        Enum.TryParse<Status>(value, out var status) ? WorkflowRail.Label(status) : "—";

    private static string DateText(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToString("MMM d, yyyy HH:mm")
            : "Not set";
}
