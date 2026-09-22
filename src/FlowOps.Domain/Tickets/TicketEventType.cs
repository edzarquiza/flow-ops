namespace FlowOps.Domain.Tickets;

/// <summary>
/// Rule AUDIT-RULE-03. Exactly the seventeen event types named in docs/domain-model.md.
/// <see cref="SlaRecalculated"/> exists because AUDIT-RULE-03 requires the type, but no method
/// appends it standalone: a priority change folds its SLA recompute into the single
/// <see cref="PriorityChanged"/> event, since TICKET-INV-09 permits exactly one event per method
/// call. See the "AUDIT-RULE-03 / TICKET-INV-09 resolution" note in docs/domain-model.md §8.
/// </summary>
public enum TicketEventType
{
    Created,
    Assigned,
    Reassigned,
    Unassigned,
    StatusChanged,
    PriorityChanged,
    CategoryChanged,
    TeamChanged,
    DueDateChanged,
    SlaRecalculated,
    PutOnHold,
    Resumed,
    Resolved,
    Reopened,
    Closed,
    CommentAdded,
    SprintChanged,
}
