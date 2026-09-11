# 0007. Flag-gated startup migrations

## Status
Accepted

## Context
Before Phase 13, the schema only ever reached a database through a developer running
`dotnet ef database update` by hand. That is workable when a person is present, but a Docker
Compose stack starts `postgres` and the application container back-to-back with no such step —
against a freshly created Postgres volume, the application's very first query would fail with
"relation does not exist," not a clean, diagnosable startup failure. CLAUDE.md §7.3 already
anticipated this and named the shape of the fix (`FlowOps__Database__ApplyMigrationsOnStartup`,
"Single-instance deployment makes this safe; the flag makes it reversible") without this ADR
existing yet to record it.

## Decision
`Program.cs` reads `FlowOps:Database:ApplyMigrationsOnStartup` (environment variable form:
`FlowOps__Database__ApplyMigrationsOnStartup`) as a plain boolean, defaulting to `false` when
absent. When `true`, the application calls `FlowOpsDbContext.Database.MigrateAsync()` once, in a
short-lived DI scope, immediately after the host is built and before any middleware runs or any
request can be served — specifically before Data Protection's own database-backed key persistence
(registered for Production) could otherwise be the first thing to touch a table that does not yet
exist. The call is not wrapped in a try/catch: a migration failure throws, and that exception stops
the host from starting at all. The local Docker Compose `app` service sets this flag to `true`,
since a fresh Postgres volume is exactly the situation it exists for; a plain `dotnet run` against
an already-migrated developer database leaves it unset and is unaffected.

## Alternatives considered
- **A separate migration container/sidecar** (`docker compose run` a one-off migrator before
  starting `app`): rejected — it is a second moving part to keep in sync with the same
  `FlowOpsDbContext`/migration assembly for no benefit at this project's single-instance scale, and
  CLAUDE.md §16/§21.6 both warn against infrastructure a project's actual scale does not justify.
- **A wait-for-it / retry-loop script wrapping the entrypoint**: rejected — `depends_on`'s
  `service_healthy` condition on `postgres`'s own existing `pg_isready` health check already
  ensures the database is accepting connections before the `app` container starts, which is the
  actual problem such a script would solve; adding one on top would be solving an already-solved
  problem with a new dependency.
- **Always migrating unconditionally, with no flag**: rejected — this is exactly the
  "single-instance deployment makes this safe; the flag makes it reversible" reasoning CLAUDE.md
  §7.3 already states; an unconditional migration on every process start removes the ability to run
  the application against a database a human is deliberately managing by hand (e.g. Render, per
  §7.3's own note that the flag defaults to `false` there too).
- **Swallowing a migration failure and starting anyway**: rejected outright — CLAUDE.md §2 forbids
  silently proceeding past a failure that would leave the application serving requests against a
  schema it cannot trust; failing to start is the only honest outcome.

## Consequences
- The Docker Compose `app` service, and only that service, sets the flag to `true`; nothing about
  host-based `dotnet run` or the test suite (which already migrates its own ephemeral Testcontainers
  databases directly in test fixtures, unrelated to this flag) changes.
- A migration failure now surfaces as a container that exits immediately with a clear stack trace in
  its logs, rather than a running container that 500s on first request — this is the deliberate
  trade-off the flag's fail-fast design makes.
- This introduces no background scheduler, no separate migration service, and no new migration
  framework — `Database.MigrateAsync()` is EF Core's own existing mechanism, called once, at the one
  point in the process's life where it is safe to do so before anything else depends on the schema.
