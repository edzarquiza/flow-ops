# 0006. No background scheduler — SLA status is derived on read

## Status
Accepted

## Context
SLA state changes with the passage of time: a ticket that is `Within` target becomes `AtRisk` at
80% elapsed and `Breached` at the deadline, with no user action in between. The conventional
reflex is a background job that periodically sweeps open tickets and writes their current SLA
state to a column. FlowOps needs to decide whether it has such a job before any read path is
built, because the answer determines whether SLA status is a stored fact or a computed one.
CLAUDE.md §8.5 already states the intended answer and cites this ADR; Phase 7 is where SLA status
is first surfaced, so it is where the decision is exercised.

## Decision
**FlowOps has no background scheduler, and SLA status is derived on every read.**

`SlaPolicy.GetStatus` is a pure function of the ticket's stored SLA columns (`sla_started_at`,
`sla_due_at`, `sla_paused_minutes`, `sla_target_minutes`, `sla_met`), its status, the resolved
risk threshold, and `now`. `SlaDueAt` is stored because it is deterministic and indexable; the
*status* is never stored, never cached, and has no column (`SLA-RULE-10`, `docs/database.md` §13).

Concretely and permanently excluded: **no `IHostedService`, no `BackgroundService`, no timer, no
polling worker, no Redis, no message broker, no scheduler of any kind.** Queries that need to
filter on SLA compare against `sla_due_at` in SQL so the database does the work.

## Alternatives considered
- **A periodic job writing a `sla_status` column:** rejected. There is nothing to "process" — the
  status is a pure function of data already stored plus the clock, so a job would only be copying
  a derivable value into a column that starts drifting the instant it is written. It would also
  create a second execution path for a rule that must have exactly one (CLAUDE.md §2 rule 7).
- **A hosted service inside the web app:** rejected. It would add a scheduler, idempotency
  concerns, and startup/shutdown coordination, on free-tier infrastructure that sleeps when idle —
  so the job would not reliably run anyway, and a stored status maintained by an unreliable job is
  worse than no stored status at all.
- **An external scheduler (cron, a worker dyno, a queue):** rejected — it multiplies the
  deployment footprint for a single-instance portfolio application (CLAUDE.md §16) and is
  explicitly out of scope (§2.1, §23).
- **Caching computed status in memory:** rejected — it is cheap to compute (integer arithmetic on
  values already loaded), so a cache would add invalidation risk in exchange for nothing
  measurable.

## Consequences
- Every read path that shows SLA status must supply `now` from the injected `TimeProvider`; the
  Domain never reads the clock itself (`TICKET-INV-10`). This is why `TicketQueryService` takes a
  `TimeProvider`.
- Status is always correct at the moment it is rendered and never stale, because it is computed at
  that moment. A page open in a browser does not update itself — but the next request is accurate,
  which is the right trade for a work tool without a real-time requirement.
- SLA status cannot be used directly as a SQL predicate, since the rule lives in C#. Queries that
  need to narrow by SLA filter on `sla_due_at` against `now()` and let the policy make the final
  determination in memory — the same superset-then-evaluate shape the attention prefilter uses
  (`ATTN-RULE-05`).
- If notifications are ever added — "tell me when this is about to breach" — they would genuinely
  need something time-triggered, and this decision would have to be revisited rather than worked
  around. Notifications are currently out of scope (CLAUDE.md §23).
