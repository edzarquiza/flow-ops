# 0011. Optimistic concurrency on Ticket via PostgreSQL xmin

## Status
Accepted

## Context
`Ticket` is the one mutable aggregate in FlowOps, and it is realistically edited by more than one
actor close in time (an agent transitioning status while a manager reassigns it, for instance).
`docs/domain-model.md` (`TICKET-ENT-02`) requires that no mutation bypass the aggregate, but does
not by itself prevent a *lost update*: two callers loading the same row, each saving their own
version, the second silently overwriting the first's change. Phase 2 must decide how persistence
detects and rejects that case before `docs/database.md` can finalize the `tickets` table design.

## Decision
`Ticket` carries a concurrency token mapped to PostgreSQL's built-in `xmin` system column (EF Core's
`UseXminAsConcurrencyToken()` via Npgsql), not an application-managed version column. Every `UPDATE`
to a `tickets` row implicitly checks `xmin` matches what was read; a mismatch raises
`DbUpdateConcurrencyException`, which the Application layer maps to an HTTP 409 / a "this ticket
changed while you were editing" message (CLAUDE.md §11.3, §7.4). No other table gets a concurrency
token — reference/configuration data (`teams`, `categories`, `projects`, `sla_configurations`) has
no domain rule requiring lost-update protection, and adding one speculatively would be an unjustified
abstraction (CLAUDE.md §21.6).

## Alternatives considered
- **An application-managed `row_version` / `updated_at`-as-token column**: rejected — it duplicates
  a fact PostgreSQL already tracks for free on every row (`xmin`), requires the application to
  remember to bump it on every write path, and is exactly the kind of hand-rolled infrastructure
  CLAUDE.md §16 warns against building when the platform already provides it.
- **Pessimistic locking** (`SELECT ... FOR UPDATE`): rejected — it would hold a row lock for the
  duration of a possibly slow human-driven request/response cycle (a user editing a form), risking
  lock contention and timeouts under normal use; optimistic concurrency fits a UI-driven, low-write-
  contention workload far better.
- **No concurrency protection (last write wins)**: rejected — CLAUDE.md §7.4 explicitly requires
  that "a concurrent edit produces a clear... message, not a lost update"; this is a stated
  requirement, not an optional hardening.

## Consequences
- Every write path that loads and saves a `Ticket` must surface `DbUpdateConcurrencyException` to
  the caller as a 409/reload message rather than swallowing or retrying it silently — retrying
  automatically would risk applying a stale mutation on top of someone else's change.
- Neon's connection retry policy (`EnableRetryOnFailure`, CLAUDE.md §16) must not wrap a
  concurrency-conflicted `SaveChangesAsync` in a way that masks or duplicates the conflict; this
  constrains Phase 3's retry configuration to single-`SaveChanges` operations, which CLAUDE.md §16
  already mandates for other reasons.
- No child table (`ticket_comments`, `ticket_events`) needs its own token, since neither is ever
  updated after insert — only the aggregate root's mutable fields are subject to lost updates.
