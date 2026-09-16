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
