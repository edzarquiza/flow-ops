# FlowOps Architecture — Domain / Application / Infrastructure Boundaries (Phase 2)

This document translates `docs/domain-model.md` (the authoritative rule catalogue) and CLAUDE.md
§3 into concrete implementation boundaries: which project a concept lives in, which type owns it,
and what each layer is — and is not — responsible for. It invents no new business rule; every
mapping below traces to a rule ID in `docs/domain-model.md` or an explicit architectural
requirement in CLAUDE.md §3. No source code is created by this document.

---

## 1. Layering and dependency direction

Per CLAUDE.md §3.1/§3.2 (formalized in ADR-0001, ADR-0002):

```
FlowOps.Web  →  FlowOps.Application  →  FlowOps.Infrastructure  →  FlowOps.Domain
```

Strictly one-way. `FlowOps.Domain` has **zero** package references beyond the .NET base class
library — no ASP.NET Core, no EF Core, no PostgreSQL client, no Identity, no HTTP, no logging
framework. This is enforced at compile time by the `.csproj` reference graph (built in Phase 3) and
is the boundary the domain-model.md rule catalogue depends on for every "Owner: <DomainType>" rule
being independently testable with no I/O.

`FlowOps.Application` is permitted to reference `FlowOps.Infrastructure` directly (it injects
`FlowOpsDbContext`) — this is the layered-not-onion decision recorded in ADR-0002. `FlowOps.Web`
(Razor Pages + API controllers) calls only `FlowOps.Application` services; it never touches EF Core
or the domain aggregate directly.

## 2. Domain concept mapping

Every concept below lives in `FlowOps.Domain`, has no I/O, and is exercised by
`FlowOps.Domain.Tests` with no database (per CLAUDE.md §15).

| Domain concept | Implementation concept | Location (folder) | Rule IDs owned |
|---|---|---|---|
| Ticket aggregate | `Ticket` class — private setters, mutation only via named methods (`Assign`, `Unassign`, `StartWork`, `PutOnHold`, `Resume`, `Resolve`, `Close`, `Reopen`, `Reassign`, `AddComment`) | `Domain/Tickets/Ticket.cs` | `TICKET-ENT-*`, `TICKET-INV-*`, `TICKET-WF-*` |
| Ticket status/workflow | `Status` enum + transition methods on `Ticket` enforcing the state table; a `DomainRuleException` (with a rule code) is thrown for any transition not in the table | `Domain/Tickets/Status.cs`, methods on `Ticket` | `TICKET-ENUM-03`, `TICKET-WF-01`…`13` |
| Ticket priority | `Priority` enum; priority-change method on `Ticket` that also calls into `SlaPolicy` for recompute | `Domain/Tickets/Priority.cs` | `TICKET-ENUM-02`, `SLA-RULE-06` |
| Ticket type | `WorkType` enum (four values only, ADR-0003) | `Domain/Tickets/WorkType.cs` | `TICKET-ENUM-01` |
| Resolution | `Resolution` enum | `Domain/Tickets/Resolution.cs` | `TICKET-ENUM-04` |
| Ticket comment | `TicketComment` — child entity of `Ticket`, created only via `Ticket.AddComment(...)`, never constructed standalone | `Domain/Tickets/TicketComment.cs` | `TICKET-ENT-05`, `TICKET-INV-09` (comment half) |
| Audit event | `TicketEvent` — child entity of `Ticket`, append-only, no public setters, no update/delete method anywhere in the domain | `Domain/Tickets/TicketEvent.cs`, `Domain/Tickets/TicketEventType.cs` | `AUDIT-RULE-01`…`06` |
| User/role concepts (domain-visible slice only) | The Domain does **not** define a `User` type. It holds `Guid` ids (`RequesterId`, `AssigneeId`, `ActorUserId`) and a minimal `TicketAuthorizationSnapshot`/`CurrentUser` shape (role, user id, team memberships) passed into `TicketAccessPolicy` — not an entity, a read-only value used only for authorization decisions | `Domain/Tickets/TicketAccessPolicy.cs` (+ its input records) | `TICKET-ENT-04`, `AUTH-RULE-02`, `AUTH-RULE-04` |
| SLA configuration | `SlaConfiguration` — plain reference-data class (no aggregate behavior); resolution-order lookup is a pure function, not a method on the entity itself | `Domain/Sla/SlaConfiguration.cs` | `SLA-RULE-01`, `SLA-RULE-02` |
| SLA policy | `SlaPolicy` — static-friendly pure class: deadline math, pause/resume arithmetic, priority-change recompute, status derivation | `Domain/Sla/SlaPolicy.cs` | `SLA-RULE-03`…`SLA-RULE-12` |
| SLA status | Not a stored type — a computed `enum SlaStatus { Within, AtRisk, Breached, Paused, Met }` returned by `SlaPolicy.GetStatus(...)`, never persisted | `Domain/Sla/SlaStatus.cs` (enum only, no table) | `SLA-RULE-10` |
| Attention policy | `AttentionPolicy` — pure function `IReadOnlyList<AttentionSignal> Evaluate(Ticket, DateTimeOffset now, AttentionOptions, int slaRiskThresholdPercent)`, plus `Rank(...)`. Takes the `Ticket` aggregate itself, not a separate snapshot type: the `Stalled` signal needs the ticket's event history, and no snapshot type was introduced purely to restate what the aggregate already exposes (CLAUDE.md §21.6). Purity is unaffected — it reads the ticket and returns signals, mutating nothing. | `Domain/Attention/AttentionPolicy.cs` | `ATTN-RULE-01`, `ATTN-RULE-02`, `ATTN-RULE-04`, `ATTN-RULE-07` |
| Attention signals | `AttentionSignal { Code, Severity, Headline, DetectedAt }` value type + `AttentionSignalCode`/`AttentionSeverity` enums | `Domain/Attention/AttentionSignal.cs` | `ATTN-RULE-02` |
| Attention thresholds | `AttentionOptions` — plain options class (bound from configuration in Infrastructure/Web composition root; the class itself is a Domain-visible POCO with no binding attributes) | `Domain/Attention/AttentionOptions.cs` | `ATTN-RULE-03` |
| Audit concepts | Covered above under "Audit event" — no separate audit subsystem; `TicketEvent` *is* the audit concept | — | `AUDIT-RULE-*` |

**Explicitly not created:** no `IRepository<T>`, no domain-event dispatcher type, no value object for
`Ticket.Reference` (it is a plain `string`, formatted and made unique by Infrastructure via a
Postgres sequence — nothing in the domain contract requires it to carry behavior). Introducing
either would violate CLAUDE.md §2.1/§16 with no rule in `docs/domain-model.md` requiring it.

## 3. Application boundaries

`FlowOps.Application` orchestrates: binds a request to a use case, loads the aggregate (tracked),
calls `TicketAccessPolicy` before doing anything else, calls domain methods, persists with one
`SaveChangesAsync`, and maps to DTOs for read paths. It contains **no** business rule of its own —
every service below either calls into `Ticket`, `SlaPolicy`, `AttentionPolicy`, or
`TicketAccessPolicy`, or it is doing pure plumbing (pagination, DTO projection).

| Application service | Responsibility | Rule IDs orchestrated | Explicitly NOT responsible for |
|---|---|---|---|
| `TicketService` | Ticket creation, all workflow transitions (`Assign`/`Unassign`/`StartWork`/`PutOnHold`/`Resume`/`Resolve`/`Close`/`Reopen`/`Reassign`), comment creation | `TICKET-INV-*`, `TICKET-WF-*`, `TICKET-ENT-05`, `AUDIT-RULE-04` | Computing SLA status or attention signals (delegates to `SlaPolicy`/`AttentionPolicy`); authorization decisions (delegates to `TicketAccessPolicy`) |
| `TicketQueryService` | Paginated, team-scoped ticket list and detail retrieval, projected straight to DTOs | `AUTH-RULE-05` | Ticket mutation; SLA/attention computation beyond calling the policies to annotate a DTO |
| `SlaConfigurationService` | Admin CRUD over `SlaConfiguration` rows | `SLA-RULE-01`, `SLA-RULE-02` | Retroactively touching existing tickets' `SlaTargetMinutes` (`SLA-RULE-03` forbids this — the service must not attempt it) |
| `AttentionQueryService` | Runs the indexed SQL prefilter, then calls `AttentionPolicy.Evaluate` over the candidates, returns ranked results | `ATTN-RULE-05`, `ATTN-RULE-06` | Inventing its own at-risk logic — every signal decision belongs to `AttentionPolicy` |
| `AnalyticsQueryService` | The Phase 10 dashboard: the four capped KPIs (Open Work, Overdue, SLA Compliance, Average Resolution Time) plus the current workload distribution, all scoped by `TicketAccessPolicy.GetAnalyticsScope` | `AUTH-RULE-10`, `SLA-RULE-08` | Re-deriving SLA status (counts the persisted `SlaMet` fact only); the optional ATTN-RULE-07 workload score (not built); any historical trend beyond the fixed 90-day window |
| `UserService` | User/role administration: role assignment, activation, the privilege-escalation guards | `AUTH-RULE-06`…`09` | Ticket-resource authorization (that is `TicketAccessPolicy`, not `UserService`) |
| `TeamService` / `CatalogService` | CRUD over `Team`, `TeamMember`, `Project`, `Category` reference data | `TICKET-ENT-03` | Any ticket workflow or SLA rule |

Every one of these is a *use case orchestrator*, not a second implementation of a domain rule —
this is the distinction CLAUDE.md §7 (one authoritative implementation) exists to protect, and the
`flowops-authorization` and `flowops-sla-attention` skills exist to catch violations of it during
implementation.

## 4. Infrastructure boundaries

`FlowOps.Infrastructure` owns everything that touches the outside world:

- `FlowOpsDbContext` (`IdentityDbContext<ApplicationUser, ApplicationRole, Guid>`), entity
  configurations (one `IEntityTypeConfiguration<T>` per entity), migrations.
- `ApplicationUser : IdentityUser<Guid>` and the Identity schema — the only place the Domain's
  `Guid` user ids resolve to a real user record.
- `TimeProvider` registration (the composition root binds the real clock; tests substitute a fake
  one — see `TICKET-INV-10`).
- Options binding: `AttentionOptions`, `SlaOptions`-equivalent configuration, `DemoOptions`,
  `DatabaseOptions`, bound with `ValidateOnStart` (CLAUDE.md §13).

Persistence structure is detailed in `docs/database.md`.

## 5. Domain vs. Application vs. Infrastructure — the borderline cases

These are the places a rule could plausibly be misplaced; the domain contract already resolves each
one, and this table exists to keep that resolution visible before implementation starts.

| Situation | Where it lives | Why |
|---|---|---|
| Deciding whether a transition is legal | Domain (`Ticket`) | `TICKET-WF-10`: illegal transitions throw a domain error — this must be true regardless of caller (Web, API, a future integration), so it cannot live in Application. |
| Deciding whether *this user* may perform a (legal) transition | Domain (`TicketAccessPolicy`) | `AUTH-RULE-04` places this in a single policy class, not per-caller checks in Razor Pages/API controllers. |
| Deciding which tickets are visible to a caller in a list | Application (`TicketQueryService`, `WHERE` clause) | `AUTH-RULE-05` — this is a query-shape concern (team-scoped SQL), not a per-row domain decision; doing it per-row would be the fetch-broadly-then-filter anti-pattern the skill exists to catch. |
| Computing SLA status for display | Domain (`SlaPolicy`), called from wherever it's displayed | `SLA-RULE-10`, `SLA-RULE-12` — one authoritative implementation, called by Application/Web, never reimplemented there. |
| Enforcing enum validity, non-null FKs, resolved/closed timestamp consistency | Both — Domain invariant *and* Infrastructure `CHECK` constraint | `TICKET-INV-06`/`07` (Domain) reinforced by `PERSIST-RULE-02`/`03` (Infrastructure). The Domain rule is the origin; the database is a second, independent guardian — never the other way around. |
| Generating the human-readable ticket reference (`FO-000123`) | Infrastructure (Postgres sequence) surfaced through `Ticket.Reference` | Not a business rule in `docs/domain-model.md` — it is an identity-formatting detail with no behavioral consequence, so it is infrastructure plumbing, not a domain concept. |

## 6. Traceability

Every concept named in §2–§5 above maps to one or more IDs in `docs/domain-model.md`, or to an
explicit structural requirement in CLAUDE.md §3 (layering) with no invented rule. The one addition
this document makes beyond the rule catalogue is naming *where in the folder structure* each rule's
owner lives — that is an implementation-location decision, not a business rule, and is flagged as
such rather than silently presented as if it came from the domain contract.
