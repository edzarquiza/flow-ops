# 0001. Modular monolith over microservices

## Status
Accepted

## Context
FlowOps needs a service-desk/ops-delivery domain (tickets, workflow, SLA, attention, analytics)
served by a single team, deployed to a single free-tier instance (Render + Neon), at a scale of a
few thousand tickets and a handful of concurrent users (CLAUDE.md §16). Phase 2 requires deciding
the deployable shape before the Domain/Application/Infrastructure/Web project boundaries can be
drawn.

## Decision
FlowOps is one deployable ASP.NET Core application — a modular monolith — with module boundaries
expressed as folders (`Tickets`, `Sla`, `Attention`, `Directory`, `Catalog`, `Analytics`) inside four
runtime projects (`FlowOps.Domain`, `FlowOps.Infrastructure`, `FlowOps.Application`, `FlowOps.Web`),
not as separately deployable services (CLAUDE.md §3.1, §3.3).

## Alternatives considered
- **Microservices per module** (e.g. a Tickets service, an SLA service): rejected — there is one
  team, one database, and no independent scaling or deployment need; splitting would add network
  calls, distributed transactions, and operational overhead (service discovery, inter-service auth)
  to solve a problem that does not exist at this scale.
- **A single unstructured project with no module boundaries**: rejected — it would make the
  one-authoritative-implementation rule (CLAUDE.md §2 rule 7) hard to enforce and erode the
  Domain/Infrastructure separation that lets `FlowOps.Domain.Tests` run without a database.

## Consequences
- Module boundaries are a discipline, not a deployment boundary — a module may only be reached
  through its application service or domain policy (CLAUDE.md §3.3), and this is enforced by review
  (§21), not by network isolation.
- All modules share one `FlowOpsDbContext` and one PostgreSQL database — there is no per-module
  data store to keep in sync.
- If FlowOps ever needed independent scaling of one module, that would be a new ADR and a real
  re-architecture, not an incremental change; this is accepted as a known limit of the decision.
