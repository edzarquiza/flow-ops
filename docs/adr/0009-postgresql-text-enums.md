# 0009. PostgreSQL relational persistence with text-valued enums

## Status
Accepted

## Context
`docs/database.md` needs a concrete storage engine and a concrete representation for the four
domain enums (`WorkType`, `Priority`, `Status`, `Resolution`) before tables can be finalized. The
database also needs to be a genuine second guardian of domain invariants (CLAUDE.md §7.4, "the
database is not the only guardian" — no, correctly: "application code is not the only guardian"),
which shapes both the engine choice and the enum representation.

## Decision
FlowOps persists to PostgreSQL (Neon in production), accessed exclusively through EF Core with the
Npgsql provider. All four domain enums are stored as `text` columns, each backed by a `CHECK`
constraint enumerating its valid values, mapped in EF Core via `HasConversion<string>()`.

## Alternatives considered
- **Enums stored as `int`**: rejected. Smaller on disk, but opaque under `psql`/ad-hoc SQL
  inspection and in database-level integrity checks — a `CHECK (status IN (0,1,2,3,4,5))` communicates
  nothing to a reader, while `CHECK (status IN ('Open','Assigned',...))` is self-documenting and
  portfolio-readable (CLAUDE.md §4.3).
- **PostgreSQL native `ENUM` types**: rejected. They require a schema migration to add a new value
  (`ALTER TYPE ... ADD VALUE`, which cannot run inside the same transaction as other DDL in older
  Postgres versions) and add a PostgreSQL-specific type EF Core must special-case; a `text` column
  with a `CHECK` constraint is portable, simple to alter, and equally strict.
- **A document database (e.g. MongoDB) instead of a relational one**: rejected — FlowOps's core
  value is aggregate queries across relationships (SLA-due filtering joined to team/category,
  workload aggregation across assignees) that a relational engine with real `JOIN`s and partial
  indexes serves directly; a document store would push that work into the application layer.
- **Dapper alongside EF Core** for the aggregate-heavy analytics queries: rejected and explicitly
  banned without a further ADR (CLAUDE.md §2.1) — EF Core's `FromSql` covers the cases where LINQ
  cannot translate an aggregation (CLAUDE.md §7.2).

## Consequences
- Every enum column needs a paired `CHECK` constraint written as raw SQL in its migration
  (`PERSIST-RULE-02`) — this is a small, recurring migration cost, accepted for the readability and
  integrity gain.
- Adding a new enum value is a one-line `CHECK` constraint edit in a new migration, not a
  `PostgreSQL ALTER TYPE`, keeping enum changes low-ceremony.
- Snake_case naming (`EFCore.NamingConventions`) and `text`-typed enums together mean the schema
  reads naturally from `psql` with no translation layer, which is also useful for the portfolio
  goal of an inspectable, professionally-recognizable schema (CLAUDE.md §25).
