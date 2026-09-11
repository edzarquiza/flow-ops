namespace FlowOps.Domain.Tickets;

/// <summary>
/// Rules AUDIT-RULE-01, AUDIT-RULE-02. Append-only: no public setters, no update/delete method
/// anywhere in the domain. A child of the <see cref="Ticket"/> aggregate — only
/// <see cref="Ticket"/>'s own methods create one.
/// </summary>
public sealed class TicketEvent
{
    public int Id { get; private set; }
    public int TicketId { get; private set; }
    public TicketEventType EventType { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset OccurredAt { get; }
    public string? Field { get; }
    public string? OldValue { get; }
    public string? NewValue { get; }
    public string? Note { get; }

    internal TicketEvent(
        TicketEventType eventType,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        string? field,
        string? oldValue,
        string? newValue,
        string? note)
    {
        EventType = eventType;
        ActorUserId = actorUserId;
        OccurredAt = occurredAt;
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
        Note = note;
    }
}
