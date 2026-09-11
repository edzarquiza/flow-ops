namespace FlowOps.Domain.Tickets;

/// <summary>
/// The acting user's identity and role, passed into the few <see cref="Ticket"/> methods whose
/// own rule text names a role condition (e.g. TICKET-WF-03, TICKET-WF-08) — not a general
/// authorization mechanism. Broader resource authorization is <see cref="TicketAccessPolicy"/>'s
/// job (AUTH-RULE-04); this type exists only because those two specific transition rules are
/// domain invariants, not access-control decisions, per their stated ownership in
/// docs/domain-model.md.
/// </summary>
public readonly record struct TicketActor(Guid UserId, UserRole Role);
