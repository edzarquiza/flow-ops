# 0033. Ticket field editing after creation

## Status
Accepted

## Context
A ticket's Priority, Category, Team, and Due Date could only be set once, at creation — there was
no way to correct a miscategorized or mispriced ticket afterward. Investigating this revealed that
`Ticket.ChangePriority`, `ChangeCategory`, `ChangeTeam`, and `ChangeDueDate` already existed fully
implemented in the Domain (correct invariant enforcement, SLA recalculation, audit events, and even
their timeline display copy already written) but were never called from `TicketService` or exposed
in any page — a prior phase built the Domain side of this feature and never finished it. Wiring
these up is the immediate, addressable gap; Title, Description, and Planned Start Date have no
Domain mutator at all (private setters, no method), which is a materially larger change, and stays
out of scope here.

Separately, `docs/domain-model.md`'s AUTH-RULE-02 capability matrix documented "Change priority"
but had no row at all for Category, Team, or Due Date — those three Domain methods had no assigned
authorization rule. And `Ticket.ChangeTeam` moves `TeamId` alone, never touching `CategoryId`;
calling it by itself would leave a ticket referencing a category that belongs to its *old* team,
silently violating TICKET-INV-02 ("category must belong to team") the moment it returned.

## Decision
`TicketService` gains four new methods — `ChangePriorityAsync`, `ChangeCategoryAsync`,
`ChangeTeamAsync`, `ChangeDueDateAsync` — each authorized by the existing
`TicketAccessPolicy.CanTransition` (Admin all / Manager own-teams / Agent own-assigned tickets /
Viewer none), the same footprint "Change priority" already had. `docs/domain-model.md`'s matrix
gains three new rows (Change category, Change team, Change due date) with that same footprint,
rather than leaving them undocumented. No new `TicketAccessPolicy` method is introduced — matching
how "Change priority" itself already piggybacks on `CanTransition` rather than getting its own.

`ChangeTeamAsync` takes only a destination category id — its team is the destination team, never a
second, independently chosen id — one call, two audit events (`TeamChanged` then `CategoryChanged`),
one `SaveChangesAsync`, rather than exposing `Ticket.ChangeTeam` as an independent action. The edit
UI offers one grouped picker (every eligible team's categories, grouped by team) rather than two
dependent dropdowns, so no client-side cascading logic is needed at all — deriving the team from the
chosen category makes the invalid "team changed, category not" state unreachable rather than merely
validated against.

Category and Team edits re-validate the caller-supplied id (exists, active, correct organization/
team) exactly as `CreateAsync` already validates a new ticket's team/category — a forged id is
refused identically to a missing one, never disclosed by a different error shape. Due Date carries
no such restriction, matching `Ticket.ChangeDueDate`'s own documented absence of an invariant: it
may be corrected even on a Resolved or Closed ticket.

Title, Description, and Planned Start Date editing are explicitly deferred — no Domain mutator
exists for them yet, and adding one (new invariants, new audit event types, new Domain tests) is a
larger, separate decision.

## Alternatives considered
- **A narrower policy for Change team** (e.g. Admin/Manager only, since it can clear the assignee
  and move a ticket out of an Agent's own queue). Rejected for this phase — the capability matrix
  gives every other ticket-mutating action an Agent an "own assigned tickets" scope, and Change team
  behaves like any other field edit from that same seat; revisit if real usage shows this needs to
  be narrower.
- **Exposing `Ticket.ChangeTeam` on its own**, leaving the caller to also call `ChangeCategoryAsync`
  as a second request. Rejected — it allows a genuinely invalid intermediate state (wrong-team
  category) to be committed if the second call never arrives, and doubles the audit noise for what
  the user experiences as one action.
- **Also building Title/Description/Planned-Start-Date editing now**, to leave nothing half-done.
  Rejected — a real feature addition (new Domain method, invariant, event type, test coverage) that
  the existing Priority/Category/Team/Due-Date work does not require.

## Consequences
Makes correcting a miscategorized or mispriced ticket possible without reopening/recreating it.
Keeps the authorization surface unchanged (no new policy method) and the capability matrix complete
rather than silently under-documented. Change team is now a two-field form instead of one, which is
slightly more UI than a bare team dropdown but avoids ever persisting an invalid category/team pair.
Title/Description/Planned Start Date remain read-only after creation until a future phase adds the
Domain support they need.
