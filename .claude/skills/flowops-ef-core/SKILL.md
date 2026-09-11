---
name: flowops-ef-core
description: EF Core, query, and migration conventions for FlowOps. Use when adding a DbContext entity configuration, writing a query, adding an index, or creating a migration.
paths: ["**/Infrastructure/Persistence/**", "**/Infrastructure/Migrations/**"]
---

# FlowOps persistence conventions

Full rules in CLAUDE.md §7. This is the checklist for the moment you're actually in this code.

## Before writing a query

- Read path → `AsNoTracking()` + `Select` straight to a DTO. Never load entities to hand-map
  them.
- Write path → tracked load of the aggregate, mutate via a `Ticket` domain method, one
  `SaveChangesAsync`. Don't add a second `SaveChangesAsync` unless there's a genuine reason two
  transactions are required — and if so, say why in a comment.
- About to write `.Include()` three levels deep, or notice N+1 in generated SQL? Project
  directly with `Select` instead.
- Aggregating (`GroupBy`, `Count`, `Avg`)? It must translate to SQL. If EF can't do it, use
  `FromSql` with parameters — never pull to memory first with `AsEnumerable()`.
- New list endpoint or page? It needs pagination (page size 25, max 100) with a deterministic
  `ORDER BY <sort>, id` tiebreak.

## Before adding/changing an entity configuration

- One `IEntityTypeConfiguration<T>` per entity. Nothing in `OnModelCreating` bodies.
- Every FK needs a deliberate `OnDelete` — `Restrict` for reference data, `Cascade` only for a
  ticket's own comments/events.
- Enum columns are text with a `CHECK` constraint, not `int`.
- New required business invariant? Ask whether it can also be a database `CHECK` constraint —
  see the list in §7.4. Application code should not be the only guardian.
- New query pattern that filters or sorts on a column? Check §7.5's index list first — most
  common cases already have a partial index; add one if genuinely new, and say why.

## Migrations

- Never edit an applied migration — add a new one.
- Name it for what it does: `AddTicketSlaPauseTracking`, not `Update1`.
- Raw SQL in a migration is fine and expected for CHECK constraints, partial indexes, sequences.

## Banned reflexes

`IRepository<T>` · a generic Unit of Work wrapper · Dapper alongside EF Core · an in-memory or
SQLite provider for tests (Testcontainers + real Postgres only, per §15). If you're reaching for
any of these, re-read ADR-0002 first.
