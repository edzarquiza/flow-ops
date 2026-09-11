# Deployment

This document covers **local Docker execution** (Phase 13). Render/Neon deployment (Phase 15) is
not covered here yet — it will be added in that phase, alongside HTTPS/HSTS/forwarded-headers
configuration, which remains deliberately deferred until then.

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
