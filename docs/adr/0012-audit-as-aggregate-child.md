# 0012. Audit history as an append-only aggregate child, no dispatcher or outbox

## Status
Accepted

## Context
`docs/domain-model.md` (`AUDIT-RULE-01`…`06`) requires that every state-changing `Ticket` method
produce exactly one `TicketEvent`, atomically with the state change, with no update or delete path
ever offered. Phase 2 must decide the persistence mechanism that guarantees this atomicity before
`docs/database.md`'s `ticket_events` table and its relationship to `tickets` can be finalized. The
common ways to guarantee "state change and its audit trail are never out of sync" in a
transactional system with future integration ambitions are a domain-event dispatcher, an outbox
table, or treating the audit record as a plain child of the aggregate persisted in the same
transaction.

## Decision
`TicketEvent` is modeled and persisted as a child entity of the `Ticket` aggregate. A domain method
(e.g. `Ticket.Resolve(...)`) mutates the ticket's own fields *and* appends a `TicketEvent` to the
same in-memory object graph; both are written by the single `SaveChangesAsync` call the Application
layer already performs for that use case (CLAUDE.md §7.2, §10). There is no domain-event dispatcher,
no outbox table, and no persistence interceptor standing between the domain method call and the
database write.

## Alternatives considered
- **A domain-event dispatcher** (`Ticket` raises an in-memory event, a handler writes the audit
  row): rejected. It introduces a second execution path for what is really one fact, and a place
  where the handler could fail to run, be forgotten for a new event type, or run outside the
  original transaction — undermining the exact guarantee (§10) this design exists to provide.
  Explicitly banned without a further ADR (CLAUDE.md §2.1).
- **An outbox table** (write an outbox row in the same transaction, a background process publishes
  it): rejected — this pattern exists to bridge a database transaction to an external system (a
  message broker, another service) reliably. FlowOps has no external consumer of ticket events in
  MVP scope (CLAUDE.md §23); introducing an outbox with nothing reading from it is complexity with
  no corresponding need, and MVP has no background worker to drain it (ADR-0006).
- **Event sourcing** (the event stream is the source of truth, current state is a projection):
  rejected — explicitly out of scope (CLAUDE.md §2.1). It would replace the entire persistence
  model this document defines, for a benefit (full historical replay) FlowOps does not need; the
  current-state-plus-audit-trail model already answers every question the product asks (CLAUDE.md
  §1).

## Consequences
- Atomicity is guaranteed by the transaction boundary of a single `SaveChangesAsync`, not by any
  additional infrastructure — if it compiles and the aggregate method appended an event, the event
  is persisted exactly when the state change is, and never otherwise.
- There is no path to "replay" or "resubscribe" to historical ticket events for a future consumer
  without building that capability later as its own explicit decision (this ADR does not preclude
  it — it just does not build it now).
- Every new state-changing method added to `Ticket` in Phase 3+ must itself append its `TicketEvent`
  — there is no dispatcher to fall back on, so forgetting it is a code-review-time defect
  (CLAUDE.md §21.4), not something infrastructure will catch for free.
