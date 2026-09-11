---
name: flowops-sla-attention
description: SLA calculation and at-risk/attention signal rules for FlowOps. Use when writing or reviewing code in SlaPolicy, AttentionPolicy, ticket status transitions, or anything touching SlaDueAt, SlaMet, or attention signals.
paths: ["**/Domain/Sla/**", "**/Domain/Attention/**", "**/Domain/Tickets/Ticket.cs"]
---

# FlowOps SLA & Attention logic

Full architecture is in CLAUDE.md §8-9. This skill is the implementation guardrail for
working inside these two files specifically.

## Non-negotiables

- `SlaPolicy` and `AttentionPolicy` are the ONLY places that compute SLA status or attention
  signals. If you're about to write `if (dueDate < now)` anywhere else — in a Razor page, a
  query, a controller — stop. Call the policy instead.
- SLA *status* is never stored, only `SlaDueAt` is. Don't add a `SlaStatus` column or cache
  a computed status on the entity.
- Priority change formula: `SlaDueAt = SlaStartedAt + newTargetMinutes + SlaPausedMinutes`.
  Getting this wrong (e.g. resetting `SlaStartedAt`) silently breaks compliance reporting.
- Pending pauses the clock — `SlaPausedMinutes` accumulates on `Resume`/`Resolve`, not on
  `PutOnHold`. If you find yourself updating `SlaPausedMinutes` inside `PutOnHold`, that's
  the wrong event.
- Reopen resets `SlaMet = null` and starts a fresh cycle. Don't let a reopened ticket carry
  its previous SLA outcome forward.

## The prefilter-superset rule (§9.3)

`AttentionQueryService`'s SQL prefilter and `AttentionPolicy`'s in-memory checks MUST stay in
sync. Any time you touch a threshold in `AttentionOptions` or add a signal to `AttentionPolicy`:

1. Update the prefilter query in the same commit.
2. Update or add to the superset test that seeds one ticket per signal (including near-miss
   cases) and asserts the prefilter returns all of them.

If you can't point to that test passing, the change isn't done.

## Before you finish

- New signal or threshold? It needs a one-sentence business justification a service desk
  manager would recognize (§9.1's table is the model).
- Every SLA/attention change needs a `FlowOps.Domain.Tests` case with no database — see §15.
