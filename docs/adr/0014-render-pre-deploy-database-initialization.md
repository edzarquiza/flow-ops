# 0014. Render Pre-Deploy Command for database initialization

## Status
Accepted

## Context
ADR-0007 established flag-gated startup migrations, run inline in `Program.cs` before the web
pipeline is configured. Phase 15 extended that same inline block to also run the demo seeder
(CLAUDE.md §14) when `FlowOps:Demo:Enabled` is true. In practice on Render this caused the exact
failure ADR-0007 was trying to avoid, one level up: the demo seed's dataset (CLAUDE.md §14's ~600
tickets / ~1,500 comments) is built entirely through `TicketService` — the same authorized,
audited path a real user's request takes, deliberately, so seeded data is never a bypass of
domain invariants — which means it is thousands of sequential, unbatched database round trips.
Locally, against Testcontainers or the Docker Compose Postgres (same machine, sub-millisecond
latency), this finishes in seconds. Against Neon from Render (a genuine cross-network hop), the
same work takes long enough that Kestrel never starts listening before Render's deploy-time
health-check window elapses — the process was still inside the `await` on `app.Run()`'s inline
predecessor, so `/health` was unreachable, and Render reported "Timed Out" even though the
application would have come up successfully moments later.

Render's Docker service model supports a **Pre-Deploy Command**: a command run, using the same
built image, after the image is built but before the new instance is started, with a materially
longer timeout than the health-check window a running instance is held to.

## Decision
`Program.cs` gains a second, standalone entry mode. When invoked as `dotnet FlowOps.Web.dll
init-database`, it builds the same `WebApplication`/DI container the normal path builds, runs
`Program.InitializeDatabaseAsync` (migrate, then seed if enabled), logs success or failure, and
**returns without ever calling `app.Run()`** — Kestrel never binds a port in this mode. Render's
Pre-Deploy Command is configured to run exactly this.

`Program.InitializeDatabaseAsync` is the single implementation of "migrate, then maybe seed" —
extracted from what was previously inline code so both the standalone command and the normal
startup path call the identical logic; they cannot drift apart into two different definitions of
"initialized." The normal startup path **keeps calling it too**, unchanged in behavior, as a
fallback: local `dotnet run` and the Docker Compose `app` service have no separate pre-deploy step
and still need it to run inline. On Render specifically, once Pre-Deploy has already done the real
work, the inline call resolves as a fast no-op (no pending migrations; the seeder's own
idempotency check finds tickets already present) rather than the multi-minute cost that caused the
original failure — this is what actually fixes the timeout, not merely relocating where the slow
work happens.

The demo seeder's existing transaction/idempotency design (one execution-strategy-wrapped
transaction, `AnyAsync()`-on-tickets idempotency check) is unchanged and turns out to be exactly
what makes it safe to run as a Pre-Deploy Command: if Render kills a Pre-Deploy invocation
(timeout, cancellation), the dropped connection causes the open transaction to roll back
server-side — there is no code path that could leave a partially-seeded, *committed* state for a
later run's idempotency check to be fooled by. A retried Pre-Deploy attempt sees either "nothing
committed, seed again" or "already seeded, skip" — never a silent partial state.

## Alternatives considered
- **Move seeding to a fire-and-forget background task after `app.Run()` starts**: rejected
  explicitly — an interrupted background task risks exactly the partial-commit-then-skip-forever
  failure the transaction design above avoids by construction; running it as a killable, restart-
  safe Pre-Deploy step is strictly safer for the same underlying seeder.
- **Batch the seeder's database writes to make it fast enough to stay inline**: rejected as the
  primary fix — it would still leave `/health` blocked on a database operation the moment that
  operation is slow for any reason (network conditions, a cold Neon instance), and it would push
  the seeder further from "goes through the same TicketService/domain/audit path a real request
  takes," which CLAUDE.md §14 requires. Batching may be worth revisiting later purely for its own
  sake, but is not needed to solve this problem.
- **A separate console/worker project**: rejected — a second deployable artifact, a second
  Dockerfile or build target, and a second place to keep in sync with `FlowOpsDbContext`/DI
  registrations, for no benefit over one binary with one additional argument.
- **Move migrations to Pre-Deploy but leave demo seeding inline (or the reverse)**: rejected —
  seeding depends on the schema migrations produce; splitting the two across two different timing
  boundaries risks the seed step running against a schema this deploy hasn't applied yet. Both stay
  together, in the same command, in the same order.

## Consequences
- Render's Pre-Deploy Command must be configured to `dotnet FlowOps.Web.dll init-database` (a
  Render dashboard/`render.yaml` setting, not a repository file) — `docs/deployment.md` documents
  this.
- `/health` and `/health/ready` are unchanged and still do not depend on the demo seed in any way;
  by the time a Render instance is serving traffic at all, initialization already succeeded or the
  deploy never reached that instance.
- A failed Pre-Deploy Command (migration or seed failure) stops the deploy before any new instance
  starts — the previous instance keeps serving, which is Render's own standard behavior for a
  failed Pre-Deploy step, not something this application needs to implement itself.
- `ADR-0007` remains the authoritative record of *why* migrations are flag-gated at all; this ADR
  is the record of *where* that gate is invoked from on Render specifically, and why demo seeding
  now shares that same invocation point.
