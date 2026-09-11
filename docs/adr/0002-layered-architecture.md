# 0002. Layered architecture (Application → Infrastructure), not Onion/Clean

## Status
Accepted

## Context
Phase 2 must define the Domain/Application/Infrastructure/Web project boundaries (CLAUDE.md §3) so
that `docs/architecture.md` and `docs/database.md` have a concrete reference direction to design
against. The common alternative in ASP.NET Core projects is Onion/Clean Architecture, where
`Application` depends only on abstractions (`IRepository<T>`, `IUnitOfWork`) that `Infrastructure`
implements, inverting the reference so `Application` never touches `Infrastructure` directly. That
pattern needs to be explicitly accepted or rejected before any service or entity configuration is
written, since it changes what `TicketService` and friends are allowed to depend on.

## Decision
FlowOps uses classic layering: `FlowOps.Web → FlowOps.Application → FlowOps.Infrastructure →
FlowOps.Domain`, strictly one-way. `FlowOps.Application` references `FlowOps.Infrastructure`
directly and injects `FlowOpsDbContext` into its services — there is no `IRepository<T>` or
`IUnitOfWork` seam. `FlowOps.Domain` has zero infrastructure package references; that is the
boundary that is actually enforced (compile-time, by the `.csproj` reference graph built in
Phase 3).

## Alternatives considered
- **Onion/Clean Architecture** (interface in `Application`, implementation in `Infrastructure`):
  rejected. There is exactly one database (PostgreSQL/Neon) and no credible scenario where it is
  swapped. An `IRepository`/`IFlowOpsDbContext` seam would buy testability FlowOps already gets from
  real-PostgreSQL integration tests (CLAUDE.md §15), at the cost of an abstraction that leaks
  `IQueryable` anyway. `DbContext` **is** a Unit of Work and its `DbSet<T>` **is** a repository;
  wrapping it duplicates it.
- **Generic repository + Unit of Work over EF Core**: rejected for the same reason, and explicitly
  banned without a further ADR (CLAUDE.md §2.1).
- **CQRS with separate read/write stores**: rejected — no measured need for separate read models at
  this scale; reads already use `AsNoTracking()` + `Select` projections against the same store
  (CLAUDE.md §7.2).

## Consequences
- `Application` services are simple to write and test against a real database, at the cost of being
  coupled to EF Core — if FlowOps ever needed to swap its ORM or database engine, that coupling
  would need to be unwound; this is accepted as a known, deliberate trade-off (CLAUDE.md §3.2).
- The one boundary that matters — the Domain must not know how it is persisted — is still fully
  enforced, since `FlowOps.Domain` never references `FlowOps.Infrastructure` or EF Core.
- If an external integration (email, webhook) is ever added, its interface must live in
  `FlowOps.Domain/Abstractions` (or a small `FlowOps.Integrations` project) and be wired in the Web
  composition root, since `Application` cannot define an interface that `Infrastructure` implements
  without inverting this reference direction. This is not pre-built; it is noted so the day it is
  needed, the decision is informed rather than accidental.
