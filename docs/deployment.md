# Deployment

This document covers **local Docker execution** (Phase 13) and **Render + Neon deployment**
(Phase 15). CI/CD pipeline mechanics (`.github/workflows/`) are covered by Phase 14, not here.

## Prerequisites

- Docker Desktop (or an equivalent Docker Engine) running.
- Nothing else — no local .NET SDK is required to run the containerized stack, only to build the
  application outside Docker (`dotnet run`, as covered in the main README/CLAUDE.md workflow).

## Two ways to run FlowOps locally

**1. Host-based (`dotnet run`), against Dockerized PostgreSQL only** — unchanged by Phase 13:

```
docker compose up -d postgres
dotnet run --project src/FlowOps.Web
```

This continues to use `appsettings.Development.json`'s connection string,
`Host=localhost;Port=5433;...` — the host process reaches the `postgres` container through its
published port, exactly as before this phase.

**2. Fully containerized**, application included:

```
docker compose up -d
```

This builds (if needed) and starts both services. The `app` container never uses `localhost:5433`
— containers on the same Compose network address each other by **service name**, so `app`'s own
connection string (supplied via its `environment:` block in `docker-compose.yml`, not
`appsettings.Development.json`) points at `Host=postgres;Port=5432` — the *internal* Postgres port,
not the host-mapped `5433`. Host-based `dotnet run` and the containerized `app` service can both be
pointed at the same underlying `postgres` container; they simply reach it by two different
addresses because one runs on the host and the other runs inside the same Docker network.

## Building the application image

```
docker compose build app
```

or directly:

```
docker build -t flowops-web .
```

The image is a multi-stage build: an SDK stage restores/builds/publishes the application, and only
the published output is copied into the final `aspnet:10.0-alpine` runtime stage — the SDK itself,
source code, and test projects never reach the runtime image (enforced by `.dockerignore`, which
excludes `tests/`, `docs/`, `.git`, and `appsettings.Development.json`, and by the Dockerfile only
copying `src/`).

## Starting and stopping the stack

```
docker compose up -d        # start postgres and app in the background
docker compose logs -f app  # follow the application's logs
docker compose down         # stop both containers (the named Postgres volume is preserved)
```

`docker compose down -v` would additionally delete the Postgres data volume — do not run that
unless you specifically want to discard local data.

## Accessing the application

Once `docker compose up` reports both services running, the application is reachable on the host
at `http://localhost:8080/` (published from the container's internal port 8080, which is what
.NET's own container base images bind to by default).

## Health endpoints

- `GET http://localhost:8080/health` — liveness only. Always returns `200` once the process is up;
  it does not check the database, so a Postgres outage does not make this endpoint (or a container
  orchestrator relying on it for liveness) restart a container that is otherwise fine.
- `GET http://localhost:8080/health/ready` — readiness. Returns `200` only when the application can
  actually reach PostgreSQL; returns a non-`200` status if the database is unavailable.

The Docker image also declares a container-level `HEALTHCHECK` against `/health` (using `wget`,
already present in the Alpine base image — no additional package was installed for this), visible
via `docker ps` or `docker inspect flowops-app-dev`.

## Database persistence

PostgreSQL's data lives in the named volume `flowops-postgres-data`, not inside either container's
own filesystem. Restarting or rebuilding the `app` container (`docker compose restart app`,
`docker compose up -d --build app`) never affects Postgres's data. Only removing the volume itself
(`docker compose down -v`, or `docker volume rm flowops-postgres-data`) does.

## Startup migrations

The `app` service sets `FlowOps__Database__ApplyMigrationsOnStartup=true`. On startup, before
serving any request, the application applies any pending EF Core migrations to whatever database
`ConnectionStrings__FlowOps` points at. This is what lets a brand-new `flowops-postgres-data` volume
go from empty to fully-schema'd automatically the first time `docker compose up` runs — see
`docs/adr/0007-startup-migrations.md` for the full reasoning, including why this is flag-gated
rather than unconditional. If a migration fails, the container fails to start and the failure is
visible in `docker compose logs app` — it is never silently skipped.

Host-based `dotnet run` does not set this flag and is unaffected; a developer working outside
Docker still runs `dotnet ef database update` by hand when the schema changes.

## Relevant environment variables

| Variable | Set by | Purpose |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `docker-compose.yml` (`app` service) | `Production` — deliberately, so the containerized app exercises its real production configuration path (database-backed Data Protection keys) locally. This is not an actual production deployment. |
| `ConnectionStrings__FlowOps` | `docker-compose.yml` (`app` service) | `Host=postgres;Port=5432;...` — the container-to-container address, distinct from `appsettings.Development.json`'s host-based `localhost:5433`. |
| `FlowOps__Database__ApplyMigrationsOnStartup` | `docker-compose.yml` (`app` service) | `true` — see "Startup migrations" above. |

## Non-root container

The runtime image runs as the non-root user Microsoft's own `aspnet:10.0-alpine` base image
provides (`USER $APP_UID` in the Dockerfile) — no root process, no manually managed UID/GID.

## Development-only credentials

The PostgreSQL username/password in `docker-compose.yml` (`flowops` / `flowops_dev_password`) are
the same development-only credentials already used by host-based `dotnet run`, documented as such
in `docker-compose.yml` itself and in `appsettings.Development.json`. They are never reused in any
non-local environment and must never be treated as a template for real credentials.

## Rebuilding after code changes

```
docker compose up -d --build app
```

Rebuilds the `app` image from the current source and restarts just that container; `postgres` and
its data are untouched.

---

## Render + Neon (Phase 15)

### Architecture

```
Browser (HTTPS)
   -> Render's edge proxy (TLS terminates here)
   -> FlowOps container, plain HTTP, X-Forwarded-Proto/X-Forwarded-For headers attached
   -> Neon PostgreSQL
```

The container never sees a TLS handshake directly — Render terminates it. `UseForwardedHeaders()`
(see `docs/adr/0013-forwarded-headers-trust.md`) makes `Request.IsHttps` correctly reflect the
original client's HTTPS connection despite that, which is what lets `CookieSecurePolicy.
SameAsRequest` (both the Identity cookie and the antiforgery cookie) mark cookies `Secure` for a
real deployed request.

### Dynamic port

Render assigns the container a port at runtime via the `PORT` environment variable — it is not
knowable ahead of time and is not the same as the local Docker Compose contract's fixed `8080`.
`Program.cs` reads `PORT` and, only when it is present, overrides Kestrel's binding to it
(`builder.WebHost.UseUrls($"http://+:{port}")`); when absent (local `dotnet run`, Docker Compose),
behavior is unchanged. Verified directly against the built container image: binding a custom
`PORT` value causes the app to listen on exactly that port and nowhere else.

### HTTPS / HSTS

The application itself does not terminate TLS or redirect to HTTPS — Render's edge proxy already
only accepts HTTPS from real clients. `UseHsts()` is enabled for `ASPNETCORE_ENVIRONMENT=Production`
only, registered after `UseForwardedHeaders()` so it sees the corrected scheme.

### Neon connection configuration

- Supply Neon's own connection string via `ConnectionStrings__FlowOps` — it already includes the
  SSL parameters Neon requires; nothing in the application needs to add them.
- CLAUDE.md §16: use Neon's **pooled** connection string, and keep `Maximum Pool Size=10` as part
  of that connection string.
- `EnableRetryOnFailure()` is enabled on the Npgsql provider (`Program.cs`) — safe given every
  write path in this codebase already follows the single-`SaveChangesAsync`-per-operation
  discipline CLAUDE.md §16 requires alongside it.

### Render Pre-Deploy Command

Render's Docker service model supports a **Pre-Deploy Command** — run, using the same built image,
after the image builds but before the new instance starts, with a materially longer timeout than
the running instance's own health-check window. Render's Pre-Deploy Command for this service must
be set to:

```
dotnet FlowOps.Web.dll init-database
```

This builds the same application/DI container the normal startup path builds, applies pending EF
migrations, runs the demo seeder if `FlowOps:Demo:Enabled` is true, logs success or failure, and
exits — **it never starts Kestrel or binds any port.** See `docs/adr/0014-render-pre-deploy-database-initialization.md`
for the full reasoning: the demo seed's dataset (CLAUDE.md §14's ~600 tickets / ~1,500 comments) is
built through thousands of sequential `TicketService` calls — fast locally, but slow enough against
Neon's real network latency to exceed a running instance's health-check timeout if it ran inline.
Running it as a Pre-Deploy step instead means the new instance's own `/health` is reachable
immediately once it starts, because the expensive work already happened beforehand.

### Migration flag

`FlowOps__Database__ApplyMigrationsOnStartup=true`, read by both the Pre-Deploy Command above and
the normal startup path's own fallback call to the same logic (see ADR-0007, ADR-0014) — so the
empty Neon database receives the full schema automatically on first deploy. Once Pre-Deploy has
already applied it, the startup-path fallback call resolves as a fast no-op (nothing pending) —
the flag does not need to be turned off between the two.

### Demo seed flag

`FlowOps__Demo__Enabled=true` for the **public portfolio demo deployment only** — never for a
"normal production" deployment of this application, if one existed separately. When enabled,
`FlowOps__Demo__PersonaPassword` **must** also be set (the app fails fast at startup otherwise,
matching the existing missing-connection-string fail-fast pattern) — it is the one password shared
by all seeded demo accounts, supplied only as an environment variable/secret, never committed.
Seeding runs once, inside a single transaction, as part of the Pre-Deploy Command above (after
migrations succeed); a failure fails the whole Pre-Deploy step — Render does not start a new
instance on top of a failed Pre-Deploy Command — rather than leaving a half-seeded demo online. See
CLAUDE.md §14 and `FlowOps.Application.Demo.DemoDataSeeder`.

### Required environment variables (Render)

| Variable | Secret? | Purpose |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | No | `Production` |
| `PORT` | No | Supplied automatically by Render |
| `ConnectionStrings__FlowOps` | **Yes** | Neon's own pooled, SSL-enabled connection string |
| `FlowOps__Database__ApplyMigrationsOnStartup` | No | `true` (see "Migration flag" above) |
| `FlowOps__Demo__Enabled` | No | `true` only for the public demo deployment |
| `FlowOps__Demo__PersonaPassword` | **Yes** (even though it is shown on the login page once the app is running, it is supplied as a secret, never committed) | The shared password for every seeded demo account |

No other value in this table is a secret; `ConnectionStrings__FlowOps` and
`FlowOps__Demo__PersonaPassword` are the only two that are.

### First deployment vs. subsequent deployments

- **First deployment**: empty Neon database. The Pre-Deploy Command applies the full schema; if
  `FlowOps__Demo__Enabled=true`, it then seeds once against that freshly-migrated, still-empty
  database — all before the first instance ever starts.
- **Subsequent deployments**: the Pre-Deploy Command's migration step is a no-op when nothing
  changed (idempotent, per `__EFMigrationsHistory`); its demo-seed step is a no-op whenever any
  ticket already exists — a redeploy never re-seeds or duplicates demo data.

### Smoke-test procedure

After a deploy, in order:

1. `GET https://<render-url>/health` — expect `200` (liveness; proves the process started and is
   listening on Render's assigned port).
2. `GET https://<render-url>/health/ready` — expect `200` (proves Neon is actually reachable).
3. `GET https://<render-url>/Account/Login` — expect a real login page; if `FlowOps__Demo__Enabled`,
   confirm the demo-credentials panel renders.
4. Inspect a `Set-Cookie` header from that response (or from an actual sign-in) — confirm `Secure`
   is present, proving the forwarded-header trust configuration is actually working in the real
   deployed environment, not merely configured in source.

Per CLAUDE.md §18: **a deployment is not successful because the workflow is green — it is
successful once `/health` genuinely returns healthy from the public URL**, and that response must
actually be shown, not assumed.

### Environment summary

| Environment | `ASPNETCORE_ENVIRONMENT` | Database | Demo seed |
|---|---|---|---|
| Local (`dotnet run`) | Development | Docker Compose Postgres, `localhost:5433` | Never |
| CI (Phase 14) | N/A (tests use ephemeral Testcontainers databases) | Ephemeral, per test run | Never |
| Local Docker Compose (`app` service) | Production | Docker Compose Postgres, `postgres:5432` | Off by default; can be enabled locally to test the seeder itself |
| Render + Neon (real/portfolio deployment) | Production | Neon | `true` only for the public portfolio demo instance |
