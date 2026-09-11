namespace FlowOps.Domain.Tickets;

/// <summary>
/// The minimal, read-only slice of a ticket's state that <see cref="TicketAccessPolicy"/> needs
/// to make a decision (AUTH-RULE-04). Deliberately not the full <see cref="Ticket"/> aggregate —
/// the policy should not be able to mutate anything, only read it.
/// </summary>
public sealed record TicketAuthorizationSnapshot(
    int TicketId,
    int TeamId,
    Guid RequesterId,
    Guid? AssigneeId,
    Status Status);
