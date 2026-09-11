# 0005. The SLA clock pauses while a ticket is Pending

## Status
Accepted

## Context
A service desk routinely blocks on someone outside the team — most often the requester, who has
been asked for information, an approval, or a time to attend. FlowOps models that with the
`Pending` status (`TICKET-WF-04`). The question this decision settles is whether the SLA clock
keeps running during that wait. If it does, SLA compliance measures how long requesters take to
reply rather than how quickly the team works, and every compliance figure in the product becomes
an argument rather than a fact. CLAUDE.md §8.3 already states the intended behaviour and cites
this ADR; the decision needed recording as its own document, and Phase 7 is where the resulting
`Paused` status is actually surfaced to users.

## Decision
Time spent in `Pending` does not count toward the SLA target.

`PutOnHold` records `PendingSince` and nothing else — `SlaPausedMinutes` is deliberately not
touched at that moment, because the length of the pause is not yet known. On `Resume` or
`Resolve`, the elapsed pending minutes are added to `SlaPausedMinutes` **and** `SlaDueAt` is
pushed out by the same amount, so the deadline moves with the pause instead of silently consuming
it (`SLA-RULE-07`). Elapsed percentage for the at-risk calculation subtracts `SlaPausedMinutes`,
and a ticket that is currently `Pending` reports the derived status `Paused` rather than a
countdown (`SLA-RULE-10`).

The scope is exactly this and no more: **no business calendars, no working hours, no holidays, no
escalation matrices.** Those are different features with far greater cost, and none of them is in
FlowOps's scope (CLAUDE.md §23).

## Alternatives considered
- **Let the clock run during Pending (do nothing):** rejected. It is the single most common
  help-desk reality, and without pausing, the demo's SLA compliance numbers would measure
  requester responsiveness rather than team performance — making the product's headline metric
  meaningless.
- **Business-hours / working-calendar SLA:** rejected as out of scope. It solves a related but
  much larger problem (calendars, time zones, holiday tables, per-team schedules) at a cost far
  beyond the roughly twenty lines pausing actually takes, and CLAUDE.md §8.3 explicitly excludes
  it.
- **Stop the clock and recompute the deadline from scratch on resume:** rejected — it would have
  to re-resolve the target from current configuration, which would retroactively rewrite a
  ticket's SLA target and break `SLA-RULE-03`'s guarantee that historical compliance is not
  rewritten.
- **Accrue the pause on `PutOnHold` rather than on `Resume`:** rejected as simply incorrect — the
  duration is unknown when the pause begins. It is called out as a specific trap in the
  `flowops-sla-attention` skill because it is an easy mistake that silently under-counts pauses.

## Consequences
- `SlaDueAt` is not a fixed value for a ticket's lifetime: it moves outward each time a pause is
  accrued. Anything comparing against it must read the current stored value rather than caching
  one.
- A ticket can sit in `Pending` indefinitely without ever breaching. That is intended — it is
  waiting on someone else — but it means "not breaching" is not by itself evidence of progress.
  Detecting a ticket that has been parked too long is the `Stalled` attention signal's job
  (`ATTN-RULE-02`), deliberately kept separate from SLA.
- Both exit paths from `Pending` must accrue the pause. `Resume` and `Resolve` each call the same
  private `AccruePendingPause`, so a ticket resolved straight out of `Pending` is measured the
  same way as one resumed first.
- The pause is stored as whole minutes (`SlaPausedMinutes`), so sub-minute pauses round. At the
  scale of a help desk this is immaterial, and it keeps the column an `int` rather than an
  interval.
