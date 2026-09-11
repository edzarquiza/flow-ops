# CLAUDE.md — FlowOps

**FlowOps — Operations & Delivery Management Platform**

This file is the engineering contract for FlowOps. It is loaded into every Claude Code session and
overrides generic habits, tutorial patterns, and framework defaults. Where this document and a blog
post, a scaffold template, or a "best practice" disagree, **this document wins**. Where this document
and the actual repository disagree, **the repository wins and this document must be updated**.

---

## 0. How to use this document

CLAUDE.md is the **operating contract**: architecture, boundaries, rules, prohibitions, workflow.
It is deliberately kept dense. Long-form detail lives in `docs/` and is read on demand:

| Document | Contains | Read when |
|---|---|---|
| `docs/domain-model.md` | Full entity/field reference, state machine tables, business rules with IDs | Working on domain or persistence |
| `docs/adr/NNNN-*.md` | Architecture Decision Records | Making or revisiting a significant decision |
| `docs/database.md` | Schema, indexes, constraints, migration history notes | Schema work |
| `docs/api.md` | Endpoint catalogue, DTO shapes, error contract | API work |
| `docs/deployment.md` | Local, CI, and Render/Neon setup and runbook | Deploy or CI work |
| `docs/demo.md` | Demo personas, seed data profile, reset procedure | Demo/seed work |
| `README.md` | Public-facing portfolio narrative, screenshots, how to run | Phase 16, and whenever setup changes |

If a `docs/` file referenced above does not exist yet, it is created in the phase that first needs it.
Do not create empty placeholder docs.

---

## 1. Product definition

FlowOps helps operational teams **see what needs attention and act on it**. The chain is:

```
WORK  →  WORKFLOW  →  INTELLIGENCE  →  ACTION
```

Every feature must strengthen a link in that chain. If it does not, it does not ship.

FlowOps answers, for a specific logged-in person:

- What needs my attention right now, ranked?
- What is breaching SLA, and in how long?
- What is unassigned, aging, stalled, or bouncing between people?
- Who on my team is overloaded?
- How fast are we actually resolving work, and is that trending well?

### FlowOps is not

- a Jira clone (no epics, sprints, story points, velocity, backlog grooming, burndown)
- a generic admin dashboard (no decorative KPI cards, no charts without a decision attached)
- a chat product (comments are an operational record, not messaging)
- a developer tool (see §2)

### 1.1 IT Support is the primary persona

**IT Support / Service Desk is a first-class use case, not a skin.** A help desk technician must be
able to use every module without translation. This has concrete consequences:

- **Terminology is operational, not engineering.** Use: ticket, work item, requester, assignee, team,
  category, priority, due date, SLA, resolution. **Banned in UI, DB, code, and docs:** epic, sprint,
  story, story points, backlog, velocity, sprint goal, groomed, swimlane, burndown.
- The default seeded content, the example categories, and the empty-state copy are IT-support
  flavoured (VPN, password reset, laptop hardware, access provisioning, printer, outage).
- Development, QA, project, and business-operations work is supported by the **same model** — via
  `Category` and `Project`, never via a parallel subsystem or a second ticket table.

---

## 2. Non-negotiable rules

These are the rules Claude Code violates most often. They are absolute.

1. **Never fabricate results.** Do not claim tests pass without pasting the actual `dotnet test`
   output. Do not claim a deployment succeeded without an actual HTTP response from the deployed
   `/health` endpoint. Do not claim a migration applied without the command output. "It should work"
   is never a completion statement.
2. **Never invent code that does not exist.** Before referencing a file, type, method, table,
   column, package, or endpoint, verify it in the repository. If it does not exist, say so.
3. **Inspect before modifying.** Read the current implementation and its tests before changing it.
4. **Smallest correct change.** Never rewrite working code to match a preferred style.
5. **No new NuGet package, no new project, no new abstraction layer without an ADR** (§20).
6. **No secrets in the repository.** Ever. Not in `appsettings.json`, not in a Dockerfile, not in a
   test fixture, not in a commit message, not in a comment "temporarily".
7. **One authoritative implementation per business rule** (§8, §9). If the UI, the API, and a query
   each compute SLA status independently, that is a defect, not a convenience.
8. **Server-side authorization always.** Hiding a button is a UX affordance and never a security
   boundary.
9. **Synthetic data only.** No real names, emails, or company data anywhere.
10. **Build must be clean.** `Nullable` enabled, `TreatWarningsAsErrors` on, analyzers on. Do not
    suppress a warning to make it go away; fix it or justify the suppression inline.

### 2.1 Prohibited without an ADR that proves necessity

MediatR · AutoMapper · generic repository / `IRepository<T>` · Unit-of-Work wrapper over `DbContext` ·
CQRS with separate write/read stores · event sourcing · domain event dispatcher infrastructure ·
Redis · message brokers · Hangfire/Quartz · SignalR · microservices · Kubernetes · React/Vue/Angular ·
a Node build pipeline · GraphQL · Dapper alongside EF Core · a service mesh · multi-tenancy.

Most of these are good technologies. None of them solve a problem FlowOps has.

---

## 3. Solution architecture

### 3.1 Shape

**A modular monolith: one deployable ASP.NET Core application, four runtime projects, module
boundaries expressed as folders inside those projects.**

```
FlowOps.sln
├─ src/
│  ├─ FlowOps.Domain/          # Business rules. No EF, no ASP.NET, no I/O. Zero infra packages.
│  ├─ FlowOps.Infrastructure/  # EF Core DbContext, entity configs, migrations, Identity, TimeProvider
│  ├─ FlowOps.Application/     # Use cases: orchestration, authorization decisions, transactions, DTOs
│  └─ FlowOps.Web/             # Razor Pages UI + /api controllers + composition root
├─ tests/
│  ├─ FlowOps.Domain.Tests/         # Fast, in-memory, no database, no mocks
│  ├─ FlowOps.Application.Tests/    # Integration: real PostgreSQL via Testcontainers
│  └─ FlowOps.Web.Tests/            # Endpoint/authorization tests via WebApplicationFactory
└─ docs/
```

**Reference direction:** `Web → Application → Infrastructure → Domain`. Strictly one-way.

### 3.2 Why this and not Clean/Onion Architecture

This is classic layering, not onion. `Application` references `Infrastructure` directly and injects
`FlowOpsDbContext` into use-case services. This is deliberate — record it in ADR-0002:

- There is exactly one database and no credible scenario in which it is swapped. An `IRepository`
  or `IFlowOpsDbContext` seam would buy testability we already get from real-PostgreSQL integration
  tests, at the cost of an abstraction that leaks `IQueryable` anyway.
- `DbContext` **is** a Unit of Work and its `DbSet<T>` **is** a repository. Wrapping it duplicates it.
- The boundary that actually matters — **the domain project must not know how it is persisted** — is
  enforced at compile time by `FlowOps.Domain` having no infrastructure package references.

**Known trade-off:** if an external integration (email, webhook) is ever added, its interface cannot
live in `Application` and be implemented in `Infrastructure` without inverting the reference. When
that day comes, define the interface in `FlowOps.Domain/Abstractions` (or a small `FlowOps.Integrations`
project) and wire it in the Web composition root. Do not pre-build this. Note it in the ADR so the
decision is visibly informed rather than accidental.

### 3.3 Modules

Modules are folders, consistently named across all four projects.

| Module | Owns | Public surface |
|---|---|---|
| `Tickets` | Ticket aggregate, workflow, assignment, comments, history | `TicketService`, `TicketQueryService` |
| `Sla` | SLA configuration resolution, deadline math, SLA status | `SlaPolicy` (Domain), `SlaConfigurationService` |
| `Attention` | At-risk signal detection and ranking | `AttentionPolicy` (Domain), `AttentionQueryService` |
| `Directory` | Users, teams, membership, roles | `UserService`, `TeamService` |
| `Catalog` | Categories, projects, SLA configuration admin | `CatalogService` |
| `Analytics` | Dashboard KPIs, workload, trends, aging | `AnalyticsQueryService` |

**Module rules:**

- A module calls another module only through that module's application service or domain policy —
  never by reaching into its internals, and never by duplicating its logic.
- Read-side analytics may query tables owned by other modules (single database, no artificial
  boundaries), but **must not re-derive owned rules**: use `SlaPolicy` / `AttentionPolicy`, never a
  hand-rolled copy of the same arithmetic.
- No module gets its own DbContext.

---

## 4. Domain model

Model the business first. `docs/domain-model.md` holds the full reference; this section holds the
decisions that constrain everything else.

### 4.1 Aggregates

**`Ticket` is the only real aggregate root.** It owns its comments and its history events. All
mutations go through methods on `Ticket`. Nothing outside `Ticket` sets `Status`, `AssigneeId`,
`SlaDueAt`, or appends a `TicketEvent`.

Everything else — `Team`, `Project`, `Category`, `SlaConfiguration`, users — is reference/configuration
data with straightforward lifecycles. Do not manufacture aggregates for them.

### 4.2 Core entities

- **`Ticket`** — `Id` (int, identity), `Reference` (`FO-000123`, unique, from a PostgreSQL sequence),
  `Title`, `Description`, `WorkType`, `Priority`, `Status`, `RequesterId`, `AssigneeId?`, `TeamId`,
  `ProjectId?`, `CategoryId`, `CreatedAt`, `UpdatedAt`, `DueDate?`, SLA fields (§8), `ResolvedAt?`,
  `ClosedAt?`, `ResolutionCode?`, `ResolutionNotes?`, `ReopenCount`, `AssignmentChangeCount`,
  `PendingReason?`, `PendingSince?`, `RowVersion` (Postgres `xmin`).
- **`TicketComment`** — `TicketId`, `AuthorId`, `Body` (plain text), `CreatedAt`, `IsInternal`.
  Internal comments are visible to Agent/Manager/Admin, not to Viewer.
- **`TicketEvent`** — append-only audit record (§10).
- **`Team`**, **`TeamMember`** (user↔team, with the team's manager flagged), **`Project`**,
  **`Category`** (belongs to a team, carries a default work type), **`SlaConfiguration`** (§8).
- **Users** live in ASP.NET Core Identity (`ApplicationUser : IdentityUser<Guid>`) in
  `FlowOps.Infrastructure`, carrying `DisplayName`, `JobTitle`, `IsActive`, `PrimaryTeamId?`.
  **The Domain never references Identity types.** `Ticket` holds `Guid` user ids; the FK to
  `asp_net_users` is configured in Infrastructure. Display names are joined in read projections.

### 4.3 Enums — deliberately small

```
WorkType   : Incident | ServiceRequest | Task | Problem
Priority   : Critical | High | Medium | Low
Status     : Open | Assigned | InProgress | Pending | Resolved | Closed
Resolution : Fixed | Completed | WorkaroundProvided | NoFaultFound | Duplicate | Withdrawn
```

**Decision (ADR-0003): four work types, not eight.** `Development`, `QA Issue`, `Project Task`, and
`Operational Issue` from the brief do **not** change any behaviour — they change SLA target and
reporting slice, both of which are already expressed by `Category`, `Project`, and `SlaConfiguration`.
Adding them as types would create four enum members with identical rules, which is complexity without
meaning. A developer's bug is a `Problem` or `Task` in the "Application Defects" category on the
"Platform" team. A QA finding is a `Task` in "QA Defects". Verify this reads naturally in the seeded
demo data before locking it in; if it does not, revisit the ADR rather than quietly adding enum values.

Enums are persisted **as text** with a `CHECK` constraint listing valid values (§7.4). Slightly larger
than `int`, immeasurably better for ad-hoc SQL, database-level integrity, and portfolio readability.

### 4.4 Invariants (enforced in `Ticket`, tested in `FlowOps.Domain.Tests`)

- Title required, 5–200 chars. Description required, ≤ 8000 chars.
- A ticket always has a `TeamId` and a `CategoryId`. The category must belong to the team.
- `AssigneeId`, when set, must be an active member of the ticket's team. Reassigning to another team
  clears the assignee and requires a new assignment.
- `Status = Assigned | InProgress` implies `AssigneeId != null`.
- `Status = Pending` implies a non-empty `PendingReason`.
- `Status = Resolved | Closed` implies `ResolvedAt != null`, a `ResolutionCode`, and
  `ResolutionNotes` of at least 10 characters.
- `ClosedAt != null` iff `Status = Closed`.
- Assignment, priority, and category changes are rejected on `Resolved` and `Closed` tickets.
- Every state-changing method appends exactly one `TicketEvent` (comments append one too).
- All timestamps are UTC. `TimeProvider` is injected; **`DateTime.UtcNow` is banned outside
  composition roots and seeders.**

---

## 5. Workflow state machine

```
        ┌──────────────── reopen (reason required) ──────────────┐
        │                                                        │
      OPEN ──assign──► ASSIGNED ──start──► IN PROGRESS ──resolve──► RESOLVED ──close──► CLOSED
        ▲                 │  ▲                 │   ▲                                       │
        └── unassign ─────┘  │                 │   │                                       │
                             │             hold│   │resume                                 │
                             └──── hold ───► PENDING ◄──────────────────────────────────────┘
                                                                        (reopen from CLOSED)
```

Allowed transitions — anything not listed is rejected with a domain error:

| From | To | Method | Rule |
|---|---|---|---|
| Open | Assigned | `Assign` | assignee must be active member of ticket's team |
| Assigned | Open | `Unassign` | clears assignee |
| Assigned | InProgress | `StartWork` | actor must be the assignee (Manager/Admin may override) |
| Assigned / InProgress | Pending | `PutOnHold` | reason required; starts SLA pause |
| Pending | InProgress | `Resume` | ends SLA pause, extends `SlaDueAt` by paused duration |
| Pending | Assigned | `Resume` | permitted when work has not restarted |
| InProgress / Pending | Resolved | `Resolve` | resolution code + notes required; sets `SlaMet` |
| Resolved | Closed | `Close` | Manager/Admin, or requester confirming |
| Resolved / Closed | Open or Assigned | `Reopen` | reason required; `ReopenCount++`; new SLA cycle |

**Reassignment** (`Reassign`) is legal in `Assigned`, `InProgress`, `Pending`; it increments
`AssignmentChangeCount` and records old/new assignee. Status does not change, except
`InProgress → Assigned` when reassigned to a different person.

**Auto-close is out of MVP scope.** It needs a scheduler; §8.5 explains why FlowOps has none.

There is no `Cancelled` status in MVP. `Resolve` with `Withdrawn` covers it. If demo data shows this
reads badly, propose the addition in an ADR — do not add it silently.

---

## 6. Authorization model

### 6.1 Roles

`Admin` · `Manager` · `Agent` · `Viewer` — ASP.NET Core Identity roles, one primary role per user.

| Capability | Admin | Manager | Agent | Viewer |
|---|:--:|:--:|:--:|:--:|
| View tickets | all | own teams | own teams | own teams |
| Create ticket | ✓ | ✓ | ✓ | ✗ |
| Comment (public) | ✓ | ✓ | ✓ | ✗ |
| See internal comments | ✓ | ✓ | ✓ | ✗ |
| Assign / reassign | ✓ | own teams | self-assign only | ✗ |
| Transition status | ✓ | own teams | own assigned tickets | ✗ |
| Change priority | ✓ | own teams | own assigned tickets | ✗ |
| Reopen | ✓ | own teams | requester of the ticket | ✗ |
| Team analytics | all | own teams | own workload only | own teams (read) |
| Manage users/teams/categories/SLA | ✓ | ✗ | ✗ | ✗ |

### 6.2 Where authorization lives

Two layers, both server-side, neither optional:

1. **Coarse gate** — `[Authorize(Roles = ...)]` on Razor Pages and API controllers. Catches the
   obvious.
2. **Resource authorization** — `TicketAccessPolicy` in `FlowOps.Application/Tickets`. A single
   class answering `CanView`, `CanComment`, `CanAssign`, `CanTransition`, `CanReopen`,
   `CanSeeInternalComments`, given `(TicketAuthorizationSnapshot ticket, CurrentUser user)`.
   **Every** application service calls it before mutating or returning a ticket. The Razor Pages and
   the API call the *same* class. It is unit-tested exhaustively as a truth table.

**List queries are filtered, not post-checked.** `TicketQueryService` applies the caller's visibility
scope (team membership) as a `WHERE` clause. Never fetch broadly and filter in memory — that is both
a performance bug and a leak waiting for a pagination change.

**Privilege escalation guards:** users cannot change their own role; the last active Admin cannot be
demoted or deactivated; role assignment is Admin-only and audited; demo accounts (§14) cannot have
their credentials or roles changed at all.

---

## 7. Persistence & EF Core

### 7.1 Ownership

- One `FlowOpsDbContext` in `FlowOps.Infrastructure/Persistence`, inheriting
  `IdentityDbContext<ApplicationUser, ApplicationRole, Guid>`.
- **No entity configuration in `OnModelCreating` bodies.** One `IEntityTypeConfiguration<T>` per
  entity in `Persistence/Configurations`, applied via `ApplyConfigurationsFromAssembly`.
- Snake_case naming via `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention`). Approved
  dependency: it makes hand-written SQL and `psql` inspection idiomatic.

### 7.2 Query rules

- **Writes:** tracked queries, load the aggregate with its needed children, call domain methods, one
  `SaveChangesAsync`. EF's change tracker plus a single `SaveChanges` is the transaction — do not add
  an explicit transaction unless more than one `SaveChanges` is genuinely required.
- **Reads:** `AsNoTracking()` and **project directly to DTOs** with `Select`. Never load entities to
  map them by hand or with a mapping library.
- **N+1 is a build-breaking defect.** Project or `Include` explicitly. Review generated SQL for every
  dashboard query at least once and note surprising plans in `docs/database.md`.
- Aggregations (`GroupBy`, `Count`, `Avg`) must translate to SQL. If EF cannot translate it, write
  the SQL explicitly with `FromSql` and parameters — never `AsEnumerable()` before aggregating.
- Every list endpoint and page is paginated. Offset pagination, page size 25 (max 100), with a
  deterministic ordering tiebreak (`ORDER BY <sort>, id`). Keyset pagination is a documented future
  option, not an MVP need.
- String search: `ILIKE` on `title` and `reference`, backed by a `pg_trgm` GIN index. Not full-text
  search, not Elasticsearch.

### 7.3 Migrations

- EF Core migrations, committed, one per logical change, named descriptively
  (`AddTicketSlaPauseTracking`).
- Never edit an applied migration; add a new one.
- Migrations run at startup **only** when `FlowOps__Database__ApplyMigrationsOnStartup=true` (true on
  Render, false locally by default). Single-instance deployment makes this safe; the flag makes it
  reversible. Document this in ADR-0007.
- Raw SQL inside migrations is allowed and expected for check constraints, partial indexes, and the
  ticket-reference sequence.

### 7.4 Database-level integrity

Application code is not the only guardian.

- FKs on every relationship with deliberate `OnDelete` behaviour (`Restrict` for reference data,
  `Cascade` only for a ticket's own comments and events).
- `CHECK` constraints for each enum column's valid values.
- `CHECK (status <> 'Closed' OR closed_at IS NOT NULL)` and the equivalent for
  resolved/resolution fields.
- `CHECK (sla_due_at > sla_started_at)`.
- Unique: `tickets.reference`; `(team_id, name)` on categories; `(work_type, priority)` on SLA
  configuration (with `work_type` nullable for the default row) — use a unique index with
  `COALESCE`/partial-index handling for the nullable column.
- `NOT NULL` wherever the domain says required; nullable columns must correspond to a real optional
  concept.
- Concurrency: `RowVersion` mapped to Postgres `xmin` as a concurrency token on `Ticket`. A concurrent
  edit produces a clear "this ticket changed while you were editing" message, not a lost update.

### 7.5 Indexes (minimum set — justify additions)

```sql
-- open-work and SLA queries: partial indexes keep them small and hot
ix_tickets_open_sla_due       ON tickets (sla_due_at)  WHERE status NOT IN ('Resolved','Closed')
ix_tickets_open_team_priority ON tickets (team_id, priority) WHERE status NOT IN ('Resolved','Closed')
ix_tickets_open_assignee      ON tickets (assignee_id) WHERE status NOT IN ('Resolved','Closed')
ix_tickets_open_unassigned    ON tickets (created_at) WHERE assignee_id IS NULL
                                                        AND status NOT IN ('Resolved','Closed')
ix_tickets_resolved_at        ON tickets (resolved_at)  -- resolution trend/compliance reporting
ix_ticket_events_ticket       ON ticket_events (ticket_id, occurred_at DESC)
ix_ticket_comments_ticket     ON ticket_comments (ticket_id, created_at DESC)
gin_tickets_title_trgm        ON tickets USING gin (title gin_trgm_ops)
```

---

## 8. SLA engine

**Single authoritative implementation: `FlowOps.Domain/Sla/SlaPolicy`. Pure, static-friendly,
deterministic, zero dependencies beyond the values passed in.** No other file computes an SLA
deadline or status. Not the dashboard. Not the API. Not a Razor page.

> These SLA targets are **portfolio demonstration values**, not industry standards. State this in the
> UI (SLA configuration page) and in the README.

### 8.1 Configuration

`SlaConfiguration { Id, WorkType? , Priority, TargetMinutes, RiskThresholdPercent }`

Resolution order (most specific wins): exact `(WorkType, Priority)` → `(null, Priority)` default row.
Seeded defaults: Critical 240 · High 480 · Medium 1440 · Low 4320 minutes. Risk threshold 80%.

Admins may edit targets. **Editing a configuration does not retroactively change existing tickets** —
each ticket captures `SlaTargetMinutes` at the point the clock starts. This is a real business rule
(historical compliance must not be rewritten), and it is testable.

### 8.2 Ticket SLA fields

`SlaTargetMinutes` · `SlaStartedAt` · `SlaDueAt` · `SlaPausedMinutes` · `PendingSince?` ·
`SlaMet?` (nullable bool, set once at resolution).

### 8.3 Rules

- Clock starts at ticket creation: `SlaDueAt = SlaStartedAt + SlaTargetMinutes`.
- **Priority change** recomputes: `SlaDueAt = SlaStartedAt + newTarget + SlaPausedMinutes`, and
  `SlaTargetMinutes` is updated. Audited with old/new values.
- **Pending pauses the clock.** On `PutOnHold`, record `PendingSince`. On `Resume`/`Resolve`, add the
  elapsed pending minutes to `SlaPausedMinutes` and push `SlaDueAt` out by the same amount.
  *Rationale (ADR-0005): waiting on a requester is the single most common help-desk reality; without
  it, SLA compliance figures in the demo are meaningless. It costs ~20 lines and is fully unit
  testable. It is explicitly not business calendars, holidays, or escalation matrices — those stay
  out of scope.*
- **Resolve** sets `SlaMet = ResolvedAt <= SlaDueAt`. This is a point-in-time fact and is persisted;
  it never recomputes.
- **Reopen** starts a new cycle: `SlaStartedAt = now`, target re-resolved from current configuration,
  `SlaPausedMinutes = 0`, `SlaMet = null`. The previous outcome remains in `ticket_events`.

### 8.4 SLA status — computed, never stored

```
if Status is Resolved or Closed  → SlaMet == true ? Met : Breached
else if Status is Pending        → Paused
else if now >= SlaDueAt          → Breached
else if elapsed% >= RiskThreshold → AtRisk
else                              → Within
```

`elapsed% = (now - SlaStartedAt - SlaPausedMinutes) / SlaTargetMinutes`.

Stored SLA *state* would immediately drift and would require a scheduler to maintain. `SlaDueAt` is
stored (deterministic, indexable); status is derived. Queries filter on `sla_due_at` against `now()`
so the database does the work.

### 8.5 No background jobs in MVP

Deliberate (ADR-0006). SLA state is derived on read, so there is nothing to "process". A hosted
service would add a scheduler, idempotency concerns, and a second execution path for the same rule —
on infrastructure that sleeps on idle. If notifications are ever added (stretch scope), revisit.

---

## 9. Attention engine ("At-Risk Work")

FlowOps's signature capability. **Single authoritative implementation:
`FlowOps.Domain/Attention/AttentionPolicy` — a pure function** from a ticket snapshot + `now` to a
ranked list of `AttentionSignal { Code, Severity, Headline, DetectedAt }`.

### 9.1 Signals

| Code | Trigger | Severity | Example headline |
|---|---|---|---|
| `SlaBreached` | SLA status is Breached | Critical | "SLA breached 2h 14m ago" |
| `SlaAtRisk` | SLA status is AtRisk | High | "SLA breach in 42 minutes" |
| `Overdue` | `DueDate < now`, not terminal | High | "2h overdue" |
| `UnassignedUrgent` | Critical/High, unassigned > 15 min | Critical | "Critical and unassigned for 38m" |
| `Aging` | open longer than priority threshold (C 1d, H 3d, M 10d, L 30d) | Medium | "Open 14 days" |
| `Stalled` | Pending > 3 days, or InProgress with no event in 5 days | Medium | "No activity for 6 days" |
| `Churn` | `AssignmentChangeCount >= 3` | Medium | "3 reassignment events" |
| `Reopened` | `ReopenCount >= 1` and currently non-terminal | Medium | "Reopened twice" |

Thresholds live in one options class (`AttentionOptions`), bound from configuration, with the seeded
defaults above. Not scattered constants.

### 9.2 Ranking

Deterministic ordering: highest signal severity → nearest `SlaDueAt` → priority → oldest `CreatedAt`.
Ties broken by `Id`. No score, no weighting mystery, no "AI".

### 9.3 Query strategy and the superset rule

The queue cannot evaluate the policy over the whole table. So:

1. `AttentionQueryService` runs an **indexed SQL prefilter** returning candidate tickets.
2. `AttentionPolicy` evaluates candidates in memory and produces signals.

**Rule: the prefilter must be a provable superset of the policy's triggers.** If a signal is added or
a threshold widened, the prefilter changes in the same commit. There must be a test that seeds tickets
covering every signal (and near-miss cases) and asserts the prefilter returns each one. This is the
one place where duplicated logic is unavoidable; it is therefore the one place that gets an explicit
test guarding the duplication.

### 9.4 Workload score (Phase 10, optional)

If built, it is exactly this and is shown in a tooltip in the UI:

```
score = Σ over the assignee's non-terminal tickets of
        priorityWeight × (1 + riskBoost)
priorityWeight: Critical 5, High 3, Medium 2, Low 1
riskBoost:      Breached +1.0, AtRisk +0.5, otherwise 0
```

Explainable, deterministic, testable, business-relevant. If it cannot be explained in one sentence to
a service desk manager, it does not ship.

---

## 10. Audit history

`TicketEvent` — **append-only**. No update path, no delete path, no service method that offers one.

Fields: `Id`, `TicketId`, `EventType`, `ActorUserId`, `OccurredAt`, `Field?`, `OldValue?`,
`NewValue?`, `Note?`.

Event types: `Created` · `Assigned` · `Reassigned` · `Unassigned` · `StatusChanged` ·
`PriorityChanged` · `CategoryChanged` · `TeamChanged` · `DueDateChanged` · `SlaRecalculated` ·
`PutOnHold` · `Resumed` · `Resolved` · `Reopened` · `Closed` · `CommentAdded`.

**Atomicity:** events are children of the `Ticket` aggregate. `Ticket.Resolve(...)` mutates state
*and* appends the event in the same object graph, persisted by one `SaveChangesAsync`. There is no
domain-event dispatcher, no outbox, no interceptor — the transactional boundary is the aggregate, and
it is impossible to have a status change without its audit row.

Audit records *business* changes only. Never log page views, filter changes, sorts, or expansions.

**Comments are not audit.** `TicketComment` is human operational context; `TicketEvent` is machine
record. They render in one merged, chronological activity timeline on the ticket page, but are stored
and queried separately.

---

## 11. Web architecture

### 11.1 UI: Razor Pages

Razor Pages for all UI (ADR-0004). Server-rendered, one page = one file pair, model binding and
anti-forgery built in, no client build pipeline, no API-shaped duplication of every screen. FlowOps is
an information-dense, form-and-table business application; this is what Razor Pages is good at.

- PageModels are **thin**: bind input → call an application service → map result to view model →
  return. No EF, no business rules, no SLA math, no authorization arithmetic in a PageModel.
- Shared UI logic in view components / partials (`_TicketStatusBadge`, `_SlaIndicator`,
  `_AttentionSignals`, `_Pager`).
- JavaScript: vanilla, progressive enhancement only (filter submit, confirm dialogs, live "time
  remaining" tick). Every page must work with JS disabled, degrading to full-page posts.
- CSS: locally hosted Bootstrap 5 + `flowops.css` with design tokens for status/priority/SLA colour
  semantics. **No CDN links** (CSP, offline dev, availability). **No Node build step.**
- Charts: Chart.js, locally hosted, data supplied as a JSON payload from the PageModel. Maximum four
  charts on the dashboard, each answering a named question.

### 11.2 REST API

Scope: `/api/v1`, JSON, **cookie authentication** (same origin, same app). No JWT — there is no
third-party consumer and no separate client, so token lifecycle management would be pure cost
(ADR-0008). Revisit in Project 8, which is where an API product belongs.

Build only endpoints with a real consumer or a real integration story:

```
GET    /api/v1/tickets                 filtered, paginated
GET    /api/v1/tickets/{reference}
POST   /api/v1/tickets
POST   /api/v1/tickets/{ref}/assign
POST   /api/v1/tickets/{ref}/status    { status, reason?, resolutionCode?, resolutionNotes? }
POST   /api/v1/tickets/{ref}/comments
GET    /api/v1/attention               ranked at-risk work for the caller's scope
GET    /api/v1/analytics/summary       dashboard KPIs
GET    /api/v1/teams/{id}/workload
```

Rules: DTOs in / DTOs out — **entities never cross the API boundary**. Correct status codes
(201 + `Location` on create, 204 on state change with no body, 400 validation, 403 authorization,
404 not found, 409 concurrency/invalid transition, 422 domain rule violation). Same
`TicketAccessPolicy` as the UI. Anti-forgery does not apply to the API; cookie auth means
`SameSite=Strict` plus an explicit CORS policy that allows no cross-origin callers.

OpenAPI via Swashbuckle, exposed at `/api/docs` in all environments (portfolio value), requiring
authentication and never exposing schemas of Identity tables.

### 11.3 Error handling

- `ProblemDetails` (RFC 7807) for all API errors, with a stable `type` slug and, for validation, an
  `errors` dictionary. `traceId` always included.
- UI errors: friendly page for 4xx/5xx, no stack traces in Production, correlation id shown so it can
  be matched to logs.
- Domain rule violations throw `DomainRuleException` (carrying a rule code) → mapped to 422 / an
  inline form error. Not-found → 404. Authorization failure → 403 (never 404-as-obfuscation; team
  scoping is already applied in the query, so a 403 leaks nothing).
- Concurrency (`DbUpdateConcurrencyException`) → 409 with a "reload and retry" message.
- **Never swallow an exception.** Never `catch (Exception) { }`. Catch specific exceptions at
  boundaries only.
- One global exception handler (`UseExceptionHandler`), not try/catch scattered through controllers.

### 11.4 Validation

Three distinct tiers — do not collapse them:

1. **Shape** — DataAnnotations on request DTOs / PageModel input models (required, length, range,
   email). Handled by model binding. *(DataAnnotations, not FluentValidation: no extra dependency for
   what the framework already does adequately at this scale.)*
2. **Existence & permission** — application services (category belongs to team, assignee is active,
   caller may act).
3. **Invariants** — domain methods on `Ticket`. These are the last line and are never bypassed, even
   by seeders (see §14) or admin tooling.

---

## 12. Security

- HTTPS enforced; HSTS in Production; `UseForwardedHeaders` configured for Render's proxy (otherwise
  redirect loops and wrong scheme in generated links).
- ASP.NET Core Identity with default password hashing (PBKDF2). Never hand-roll hashing.
- Cookies: `HttpOnly`, `Secure`, `SameSite=Strict`, sliding expiration 8h, `/Account/Login` path.
- **Data protection keys persisted to the database** (`PersistKeysToDbContext`). Without this, every
  Render restart invalidates every cookie and antiforgery token. This is a real, easily-missed
  production requirement.
- Antiforgery on every state-changing form (Razor Pages default — do not disable it).
- Rate limiting (built-in `AddRateLimiter`): login and password endpoints (5/min/IP), API (100/min/user).
  Identity lockout after 5 failures for 15 minutes.
- Security headers: `Content-Security-Policy` (no `unsafe-inline`; use nonces for the small inline
  bootstrap script if any), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `X-Frame-Options: DENY`, `Permissions-Policy` minimal.
- All user text is rendered through Razor's automatic HTML encoding. **`@Html.Raw` is banned** on
  user-supplied content. Comments and descriptions are plain text; therefore no HTML sanitizer
  dependency is needed — do not add rich text without an ADR.
- All data access through EF Core parameterized queries. Raw SQL uses `FromSql` interpolation or
  explicit parameters, never string concatenation.
- No PII anywhere. Log user ids, never emails or names.
- Dependabot enabled; `dotnet list package --vulnerable` runs in CI.

---

## 13. Configuration, logging, observability

**Configuration**

- `appsettings.json` — non-secret defaults, committed.
- `appsettings.Development.json` — local non-secret overrides, committed.
- **Secrets:** user-secrets locally, environment variables in Render. Never files, never committed.
- Strongly-typed options (`SlaOptions`, `AttentionOptions`, `DemoOptions`, `DatabaseOptions`) bound at
  startup with `ValidateOnStart`. **The app must fail fast at startup on missing/invalid config**, not
  fail later at first request.
- No `if (env == "Production")` branching in business code. Environment differences belong in
  `Program.cs` composition and configuration values.

**Logging** — built-in `Microsoft.Extensions.Logging` only. Console JSON formatter in Production
(Render captures stdout). No Serilog, no Seq, no OpenTelemetry stack for a single-instance demo.

- Log at Information: ticket created/assigned/status-changed/resolved/reopened, login success/failure,
  admin configuration changes, migration and seed execution.
- Log at Warning: authorization denials, concurrency conflicts, rate-limit rejections.
- Log at Error: unhandled exceptions, database failures, startup validation failures.
- Structured, templated messages with a `TicketReference` / `ActorUserId` scope. Never interpolate.
- `/health` (liveness) and `/health/ready` (database connectivity) via `AddHealthChecks`.

---

## 14. Demo data & personas

Seeding runs at startup when `FlowOps__Demo__Enabled=true`, idempotent (skips if tickets exist),
inside a transaction, logged.

**Volume:** 5 teams · 25 users · 8 projects · ~600 tickets · ~1500 comments · full history.

**Composition:** IT Support (~45%), Development (~20%), QA (~10%), Project delivery (~15%), Business
Operations (~10%). Teams: *Service Desk*, *IT Infrastructure*, *Application Support*, *Platform
Engineering*, *Business Operations*.

**Requirements that make the demo actually demonstrate something:**

- Timestamps are **relative to seed time**, not absolute. Freshly deployed or seeded months later, the
  dashboard always shows live breaches, at-risk items minutes from breach, and a meaningful 90-day
  trend. Absolute dates would make the demo dead on arrival.
- Deterministic: fixed RNG seed, so the same data set is reproducible for screenshots and tests.
- Every signal in §9.1 has at least three examples. SLA compliance lands around 80–88% — a credible
  number, neither perfect nor broken.
- Resolved tickets carry realistic resolution codes and notes; history reflects plausible paths
  including reassignments and reopens.
- **Seeded tickets are created through the domain methods**, not by inserting rows that bypass
  invariants. If the seeder cannot produce a state through legal transitions, the state is illegal and
  the seeder is right to fail.

**Personas** (credentials supplied via environment variables, displayed on the login page, never in
git):

| Persona | Role | Shows |
|---|---|---|
| Service Desk Manager | Manager | At-risk queue, team workload, SLA performance |
| IT Support Agent | Agent | Personal queue, ticket handling, resolution flow |
| Application Support Agent | Agent | Cross-category work, escalation, reassignment |
| Executive Viewer | Viewer | Read-only analytics and authorization boundaries |

**Demo mode guard:** when `Demo:Enabled`, seeded persona accounts cannot be deleted, renamed,
role-changed, or password-changed, by anyone including Admin. A persistent banner identifies the site
as a portfolio demonstration with synthetic data.

---

## 15. Testing strategy

Confidence, not coverage percentages. Three projects, three purposes.

**`FlowOps.Domain.Tests` — fast, no I/O, no mocks.** Domain rules take values in and return values
out, so they need neither. This is where the majority of assertions live:

- every legal and illegal state transition (table-driven)
- all `Ticket` invariants and their error codes
- `SlaPolicy`: deadline calculation, priority change mid-flight, pause/resume arithmetic, `SlaMet` at
  boundary (`ResolvedAt == SlaDueAt`), reopen cycle
- `AttentionPolicy`: each signal at, just below, and just above threshold; ranking determinism
- `TicketAccessPolicy`: full role × relationship × operation truth table
- workload score arithmetic

**`FlowOps.Application.Tests` — real PostgreSQL via Testcontainers**, migrations applied, one database
per test class, transaction rollback or respawn between tests. No in-memory provider and no SQLite
substitute — they do not have Postgres's constraints, and the constraints are part of the design:

- ticket creation → correct persisted state + `Created` event, in one transaction
- assignment, reassignment, transitions each write exactly one audit row
- database CHECK constraints actually reject invalid rows (prove the DB is a real guardian)
- concurrency token produces a conflict on simultaneous edit
- the §9.3 **prefilter-superset test**
- pagination and visibility-scoped queries return the right rows
- analytics aggregations against a known fixture data set

**`FlowOps.Web.Tests` — `WebApplicationFactory` endpoint tests with real authentication.** Do not stub
the auth handler: log in as seeded test users of each role. Cover: anonymous is redirected/401; each
role's allowed and forbidden operations on tickets inside and outside their team; antiforgery is
enforced; API returns the documented status codes and `ProblemDetails` shape; error responses contain
no stack trace in Production configuration.

**Rules:** every bug fix starts with a failing test. Tests are named
`Method_Condition_ExpectedResult`. No test depends on another test's ordering. Never delete or
`[Skip]` a failing test to make CI green — fix the code or the test, and say which.

---

## 16. Performance

Target scale: single instance, free tier, a few thousand tickets, a handful of concurrent users. Design
responsibly for that; do not design for a scale that does not exist.

- Every user-facing list is paginated and indexed.
- Dashboard renders within a small, fixed number of queries — aim for ≤ 6 aggregate queries, executed
  concurrently where independent and safe. If it needs more, reconsider what the dashboard is asking.
- **No caching infrastructure.** No Redis, no distributed cache. If a query is slow, fix the query or
  the index. If, after that, in-memory caching of *static reference data* (categories, teams, SLA
  config) is genuinely warranted, use `IMemoryCache` with explicit invalidation on admin edit — and
  write an ADR.
- Neon: use the **pooled** connection string, keep the pool small (`Maximum Pool Size=10`), enable
  `EnableRetryOnFailure` — and be aware retries and explicit transactions interact badly, so keep to
  single-`SaveChanges` operations (§7.2).
- Cold starts are expected on free tier. Do not build warming infrastructure; state it in the README.

---

## 17. Docker

Multi-stage, and nothing more clever than it needs to be.

- Build stage: `mcr.microsoft.com/dotnet/sdk:10.0`. Runtime stage:
  `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (or `-noble-chiseled`).
- Restore layer separated from source copy for cache efficiency.
- **Non-root user.** Read-only-friendly. No secrets, no `appsettings.Production.json` with values, no
  `.env` in the image.
- Listens on `${PORT}` (Render supplies it); `ASPNETCORE_ENVIRONMENT=Production`.
- `.dockerignore` excludes `bin/`, `obj/`, `.git`, tests, docs.
- `docker-compose.yml` for **local development only**: app + PostgreSQL, with development credentials
  clearly marked as such and never reused anywhere else.
- Container `HEALTHCHECK` hitting `/health`.

---

## 18. CI/CD & deployment

```
GitHub → Actions (restore → build → test → vulnerability scan → container build)
       → GHCR → Render (Docker) → Neon PostgreSQL
```

**`ci.yml`** on every push and PR: restore · build with warnings as errors · `dotnet format --verify-no-changes`
· `dotnet test` (Testcontainers needs Docker — the Ubuntu runner has it) · `dotnet list package --vulnerable --include-transitive`
· build the container image (proves the Dockerfile works on every commit).

**`deploy.yml`** on `main` only, after CI passes: push image to GHCR · trigger the Render deploy hook ·
**poll `/health` until healthy or fail after a timeout** · report the deployed commit SHA.

> **A deployment is not successful because the workflow is green. It is successful when `/health`
> returns healthy from the public URL.** The workflow must actually check, and Claude Code must show
> the response before claiming success.

**Environment separation:** Development (local, user-secrets, local Postgres in Docker) · Test (CI,
ephemeral Testcontainers database) · Production (Render + Neon, env vars). No environment-specific
values in code; no production connection string on a developer machine.

Branching: short-lived feature branches → PR → `main`. `main` is always deployable. Conventional
Commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`).

---

## 19. Development workflow for Claude Code

### 19.1 Loop for every change

1. **Inspect** — read the affected files and their tests. State what currently exists.
2. **Locate** — name the layer the change belongs in, using §3. If a change wants to live in a
   PageModel or a controller, it is probably a domain or application concern.
3. **Plan** — smallest change that fully solves it; note files to be touched.
4. **Implement** — following the rules in this document.
5. **Test** — write/extend tests; run them; **paste the output**.
6. **Review** — re-read the diff against §21's checklist.
7. **Document** — update `docs/` and this file if a rule or boundary changed; add an ADR if the
   decision was significant.

### 19.2 Phases — do not jump ahead

| Phase | Deliverable | Gate (must pass to proceed) |
|---|---|---|
| 1 | Domain rules documented in `docs/domain-model.md` | Every rule has an id and a stated owner |
| 2 | Domain project + schema design + ADRs 1–8 | Domain tests exist and pass with no database |
| 3 | ASP.NET Core skeleton, DbContext, first migration, health checks | App runs locally against Docker Postgres |
| 4 | Identity, roles, `TicketAccessPolicy`, login/logout | Authorization truth-table tests pass |
| 5 | Ticket CRUD, work queue, ticket detail | Create→view→list works end to end, paginated |
| 6 | Workflow transitions + audit events | Illegal transitions rejected and tested |
| 7 | SLA engine | Pause/resume/priority-change/reopen tests pass |
| 8 | Attention engine + At-Risk queue | Prefilter-superset test passes |
| 9 | Comments + merged activity timeline | Internal-comment visibility enforced |
| 10 | Dashboard + analytics + workload | Query count and plans reviewed |
| 11 | UI refinement + accessibility pass | §22 checklist passes on key pages |
| 12 | Test hardening + security review | §21 review clean; no warnings |
| 13 | Docker | Image runs locally, non-root, health check green |
| 14 | CI/CD | Pipeline green on a real commit |
| 15 | Render + Neon deployment + seeded demo | `/health` verified publicly; personas log in |
| 16 | README, ADR index, screenshots, architecture diagram | A stranger can understand and run it |

Attractive later features (charts, dashboards) do not justify skipping earlier phases. A dashboard
built on a wrong domain model is rework, not progress.

### 19.3 When to ask, when to decide

**Decide and document** — naming, folder placement within the agreed structure, DTO shapes, page
layout, index choices, test structure, copy, seed content, minor library-free implementation details.

**Stop and ask** — anything that changes: architecture or project structure · a security or
authorization boundary · a domain rule or state machine · database schema in a destructive way ·
project scope · a new dependency · anything irreversible in production data.

Do not stop for small decisions. Do not proceed silently on large ones.

---

## 20. Architecture Decision Records

`docs/adr/NNNN-short-title.md`. Format: **Context · Decision · Alternatives considered · Consequences ·
Status**. Keep each under a page. Write it when the decision is made, not retroactively.

Expected initial set:

- ADR-0001 Modular monolith over microservices
- ADR-0002 Layered architecture with Application → Infrastructure (and why not Onion)
- ADR-0003 Four work types instead of eight
- ADR-0004 Razor Pages over a SPA
- ADR-0005 SLA pauses while Pending
- ADR-0006 No background scheduler; SLA status derived on read
- ADR-0007 Startup migrations behind a flag
- ADR-0008 Cookie authentication for the API instead of JWT
- ADR-0009 PostgreSQL/Neon and text-valued enums with check constraints

**Also record what was deliberately not adopted, and why.** "We did not add Redis because there is no
measured cache need at this scale" is stronger engineering evidence than adding Redis.

---

## 21. Standing review checklist

Run before completing any phase, and before any PR to `main`:

1. Is business logic in the domain, or has it leaked into a PageModel, controller, query, or JS?
2. Does any rule (SLA, transition, at-risk, permission) now have two implementations?
3. Is every mutating path authorized server-side by `TicketAccessPolicy`?
4. Does every state change produce exactly one audit event, in the same transaction?
5. Any N+1, any unbounded query, any missing index for a new query shape?
6. Any abstraction added that has exactly one implementation and no test seam value? Remove it.
7. Any new dependency? Justified in an ADR?
8. Any secret, credential, or real personal data anywhere in the diff?
9. Do tests cover the behaviour, or only the happy path?
10. Are the nullable columns genuinely optional, and the constraints present at the database level?
11. Could a service desk technician understand this screen without a developer's vocabulary?
12. Could this decision be defended in an interview in two sentences?

---

## 22. UI/UX & accessibility standards

**Design intent:** operational, dense, calm. A tool someone stares at for eight hours. Information
hierarchy over decoration.

- The dashboard opens on **"What needs my attention"**, not a row of vanity counters. KPIs are limited
  to Open Work, Overdue, SLA Compliance, Average Resolution Time — each clickable through to the
  filtered work it represents. A metric you cannot act on does not belong.
- Urgency is conveyed by consistent semantic tokens (breached / at-risk / within / paused), never by
  colour alone — always paired with text or an icon.
- Ticket rows show: reference, title, priority, status, assignee, team, SLA remaining ("42m left",
  "2h 14m over"), and attention signals. Relative times with absolute times in `title` attributes.
- No gradients-for-decoration, no animated counters, no hero sections, no emoji as UI, no AI-styled
  glassmorphism. This is a work tool.
- **Accessibility is quality, not a checkbox:** semantic HTML (`<table>` for tabular data with real
  `<th scope>`), labels tied to inputs, keyboard operability for every action, visible focus states,
  WCAG AA contrast, validation messages tied via `aria-describedby`, `<main>`/landmarks, skip link.
- Useful empty states ("No work is at risk right now" beats a blank panel) and honest error states.
- Responsive to tablet width at minimum; the work queue must remain usable on a support technician's
  smaller screen.

---

## 23. Scope control

**Core (build):** authentication · roles & authorization · users · teams · projects · categories ·
tickets · workflow · comments · audit history · SLA engine · attention engine · work queue ·
dashboard & analytics · workload · PostgreSQL · justified REST API · Docker · CI/CD · public
deployment · documentation.

**Stretch (only after Phase 16, only with an ADR):** attachments · email notifications · saved
filters · CSV export · Kanban view · advanced trend analytics · demo auto-reset job.

**Out of scope — belongs to other portfolio projects:** AI, LLM features, agents, anomaly detection
(Project 7) · public API product, integrations platform, embedded analytics (Project 8) ·
microservices, Kubernetes, Redis, message queues, real-time chat, mobile app, multi-tenancy (never).

If a request would pull Project 7 or 8 capability into FlowOps, say so and decline. The portfolio
progression is DATA → PIPELINE → **BUSINESS APPLICATION** → INTELLIGENT AUTOMATION → PLATFORM. FlowOps
proves the third step, and it proves it best by being an excellent business application rather than a
mediocre preview of the next two.

---

## 24. Explainability contract

Every question below must be answerable by pointing at one place in the code. If a change makes an
answer harder, the change is wrong.

| Question | Answer lives in |
|---|---|
| Why this structure? | `docs/adr/0001`, `0002` |
| Where do business rules live? | `FlowOps.Domain` — `Ticket`, `SlaPolicy`, `AttentionPolicy`, `TicketAccessPolicy` |
| Where does database logic live? | `FlowOps.Infrastructure/Persistence` — DbContext, configurations, migrations |
| How does authentication work? | ASP.NET Core Identity, cookies, `Program.cs` + `Infrastructure/Identity` |
| How does authorization work? | Role attributes as a gate + `TicketAccessPolicy` for resource decisions |
| How are APIs separated from UI? | Razor PageModels and API controllers both call the same application services |
| How does validation work? | Three tiers, §11.4 |
| How do transactions work? | Aggregate + one `SaveChangesAsync`; audit is a child of the aggregate |
| How does audit history work? | `TicketEvent`, append-only, written by `Ticket` methods |
| How is SLA calculated? | `SlaPolicy` — one implementation, pure, unit-tested |
| How is at-risk work detected? | `AttentionPolicy` + prefilter with a superset test |
| How do tests isolate business behaviour? | Domain tests need no database; integration tests use real Postgres |
| How is it deployed? | GitHub Actions → GHCR → Render → Neon, verified by `/health` |
| How could it evolve? | Module boundaries in §3.3; the deliberate non-decisions in the ADRs |

---

## 25. Mental model

**FlowOps is not a ticket database.** It is a system that turns operational work into structured
workflow, workflow into signals, signals into management insight, and insight into action.

Before adding anything, ask: *which link in that chain does this strengthen?* If the answer is "none,
but it would look impressive," delete it.

The goal is not the most sophisticated architecture. It is the **simplest architecture that a senior
engineer would recognise as professionally correct** — clear boundaries, explicit rules, real security,
honest tests, and a deployment that actually works.
