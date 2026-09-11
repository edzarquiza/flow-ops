# 0014. Render Pre-Deploy Command for database initialization

## Status
Accepted, amended — see "Amendment: Render Free tier has no Pre-Deploy Command" below. The
original decision (invoke `init-database` before the new instance starts, and why) stands
unchanged; only *how* that invocation reaches Render changed.

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

## Amendment: Render Free tier has no Pre-Deploy Command

Verified directly against the Render dashboard for this service: **Pre-Deploy Command is not
available on the Free compute plan** this deployment intentionally stays on (CLAUDE.md's portfolio
deployment is not meant to require a paid tier). Everything in Context/Decision above about *why*
`init-database` must run to completion before the web process starts remains true — Free tier
changes only where that invocation is anchored, not the underlying problem or its fix.

**Two inline Docker Command attempts were tried directly on the Render dashboard and rejected**:
- `-c "dotnet FlowOps.Web.dll init-database && dotnet FlowOps.Web.dll"` — exited status 128.
- `/bin/sh -c "dotnet FlowOps.Web.dll init-database && dotnet FlowOps.Web.dll"` — Render's own
  command-field parsing did not preserve the quoted string as a single shell argument, producing
  `/bin/sh: dotnet FlowOps.Web.dll init-database && dotnet FlowOps.Web.dll: not found` (the whole
  string was passed to `sh` as one literal, unparsed token rather than `-c` plus its argument).

**Decision (amendment)**: a real, executable file — `render-start.sh` at the repository root — is
copied into the image at `/app/render-start.sh` (`COPY --chmod=755`, since `COPY` defaults to root
ownership regardless of the preceding `USER $APP_UID`, and the non-root runtime user still needs
execute permission). Render's Docker Command field for this service is set to
`/app/render-start.sh` — a path to a real file, not an inline quoted command Render's field parser
has to get right. The script itself is the same two-line sequence the earlier inline attempts were
trying to express, using `set -e` (stop on the first failure) and `exec` (replace the shell process
with the web server, so it directly receives signals — e.g. `SIGTERM` on a Render restart — rather
than being a grandchild of a shell that never exits):

```sh
#!/bin/sh
set -e
dotnet FlowOps.Web.dll init-database
exec dotnet FlowOps.Web.dll
```

The Dockerfile's `ENTRYPOINT ["dotnet", "FlowOps.Web.dll"]` is **unchanged** — `render-start.sh` is
invoked by explicitly overriding it (`--entrypoint`/Render's Docker Command field), never by
editing the image's own default. Local `dotnet run`, the Docker Compose `app` service, and any
plain `docker run <image>` with no override all continue to use the bare entrypoint exactly as
before; the script is Render-Free-specific opt-in, not a new default behavior for the image.

**Verified directly against the built image** (not merely reasoned about): the script exists at
`/app/render-start.sh` with mode `-rwxr-xr-x`, executable by the non-root runtime user
(`uid=1654`); a successful run logs `Database initialization completed successfully.` followed by
`Now listening on: http://[::]:8080`, `docker ps` reports the container `healthy`, and
`docker top` shows exactly one process (`dotnet FlowOps.Web.dll`, no lingering shell) — confirming
`exec` genuinely replaced the shell rather than merely backgrounding it; a failing run (unreachable
database) exits the container with status `1` and never starts Kestrel — no port is ever bound.

### Alternatives considered (amendment)
- **Inline quoted Docker Command**: what was actually tried; rejected by evidence (both attempts
  failed on the real dashboard, as recorded above), not by suspicion — the way Render's Docker
  Command field parses quoting/shell operators is what could not be gotten to work reliably.
- **Upgrade to a paid Render plan for Pre-Deploy Command**: rejected — an explicit, standing
  constraint for this portfolio deployment; solving this with a script costs nothing and keeps the
  deployment free.
- **A separate Render Cron Job / Background Worker / one-off Job run for initialization**: rejected
  — Cron Jobs are a paid-plan feature; a Background Worker would be a second, independently
  deployed service to coordinate for no benefit; a manually-triggered one-off Job would not
  automatically re-run on every future deploy that introduces a new migration, unlike the script,
  which runs automatically, every deploy, with no operator action required.
