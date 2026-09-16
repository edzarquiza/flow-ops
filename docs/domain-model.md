# FlowOps Domain Model — Rule Catalogue

This document is the formal, rule-by-rule source of truth for the FlowOps domain. It is derived
entirely from CLAUDE.md §4–§10 (with database-level reinforcement drawn from §7.4) and introduces
no new entities, statuses, work types, permissions, SLA rules, attention signals, or workflow
transitions beyond what CLAUDE.md already establishes. Where CLAUDE.md changes, this document must
be updated in the same change — it does not stand independently of the contract.

Every rule below has a **stable ID**, the **rule itself**, and a **stated owner** — the type or
service in the codebase responsible for enforcing it. Ownership is never assigned to a Razor Page,
a controller, or a database configuration when the rule is properly a domain rule; where the
database also reinforces a domain rule, that reinforcement is catalogued separately under
`PERSIST-RULE` and is explicitly marked as *reinforcement*, not the rule's origin.

## ID scheme

| Prefix | Domain area | Source |
|---|---|---|
| `TICKET-ENT-*` | Aggregate boundary & core entity structure | CLAUDE.md §4.1–4.2 |
| `TICKET-ENUM-*` | Enumerations | CLAUDE.md §4.3 |
| `TICKET-INV-*` | Ticket invariants | CLAUDE.md §4.4 |
| `TICKET-WF-*` | Workflow state machine & transitions | CLAUDE.md §5 |
| `AUTH-RULE-*` | Authorization model | CLAUDE.md §6 |
| `SLA-RULE-*` | SLA engine | CLAUDE.md §8 |
| `ATTN-RULE-*` | Attention ("at-risk") engine | CLAUDE.md §9 |
| `AUDIT-RULE-*` | Audit history | CLAUDE.md §10 |
| `PERSIST-RULE-*` | Database constraints that *reinforce* a domain rule above | CLAUDE.md §7.4 |
| `ORG-RULE-*` | Multi-tenancy & organizations | CLAUDE.md §6.3 |

IDs are permanent once assigned. If a rule is removed, its ID is retired, not reused. If a rule is
split or clarified, it gains a new ID rather than silently changing meaning under the old one.

---

## 1. Aggregate boundary & core entity structure (`TICKET-ENT`)

| ID | Rule | Owner |
|---|---|---|
| `TICKET-ENT-01` | `Ticket` is the only aggregate root in the domain; it owns its comments and its history events. | Ticket aggregate |
| `TICKET-ENT-02` | All mutations to ticket state go through methods on `Ticket`. Nothing outside `Ticket` sets `Status`, `AssigneeId`, `SlaDueAt`, or appends a `TicketEvent`. | Ticket aggregate |
| `TICKET-ENT-03` | `Team`, `Project`, `Category`, `SlaConfiguration`, and users are reference/configuration data with straightforward lifecycles; they are not manufactured into aggregates. | Directory module (Team) / Catalog module (Project, Category, SlaConfiguration) |
| `TICKET-ENT-04` | The Domain never references Identity types. `Ticket` holds `Guid` user ids (`RequesterId`, `AssigneeId`); the FK to `asp_net_users` is configured in Infrastructure, and display names are joined only in read projections. | Ticket aggregate (Domain/Infrastructure boundary) |
| `TICKET-ENT-05` | A `TicketComment` carries `IsInternal`. Internal comments are visible to Agent, Manager, and Admin; they are not visible to Viewer. | Ticket aggregate (comment ownership) / TicketAccessPolicy (`CanSeeInternalComments`, `AUTH-RULE-04`) |

## 2. Enumerations (`TICKET-ENUM`)

| ID | Rule | Owner |
|---|---|---|
| `TICKET-ENUM-01` | `WorkType` is exactly `Incident \| ServiceRequest \| Task \| Problem` — four values, per ADR-0003. `Development`, `QA Issue`, `Project Task`, and `Operational Issue` are deliberately not separate work types; that distinction is carried by `Category`/`Project`/`SlaConfiguration` instead. | Ticket aggregate |
| `TICKET-ENUM-02` | `Priority` is exactly `Critical \| High \| Medium \| Low`. | Ticket aggregate |
| `TICKET-ENUM-03` | `Status` is exactly `Open \| Assigned \| InProgress \| Pending \| Resolved \| Closed`. | Ticket aggregate |
| `TICKET-ENUM-04` | `Resolution` is exactly `Fixed \| Completed \| WorkaroundProvided \| NoFaultFound \| Duplicate \| Withdrawn`. | Ticket aggregate |

## 3. Ticket invariants (`TICKET-INV`)

| ID | Rule | Owner |
|---|---|---|
| `TICKET-INV-01` | `Title` is required, 5–200 characters. `Description` is required, at most 8000 characters. | Ticket aggregate |
| `TICKET-INV-02` | A ticket always has a `TeamId` and a `CategoryId`; the category must belong to the team. | Ticket aggregate |
| `TICKET-INV-03` | `AssigneeId`, when set, must be an active member of the ticket's team. Reassigning a ticket to a different team clears the assignee and requires a new assignment. | Ticket aggregate |
| `TICKET-INV-04` | `Status = Assigned` or `Status = InProgress` implies `AssigneeId != null`. | Ticket aggregate |
| `TICKET-INV-05` | `Status = Pending` implies a non-empty `PendingReason`. | Ticket aggregate |
| `TICKET-INV-06` | `Status = Resolved` or `Status = Closed` implies `ResolvedAt != null`, a `ResolutionCode`, and `ResolutionNotes` of at least 10 characters. | Ticket aggregate |
| `TICKET-INV-07` | `ClosedAt != null` if and only if `Status = Closed`. | Ticket aggregate |
| `TICKET-INV-08` | Assignment, priority, category, and **team** changes are rejected on tickets in `Resolved` or `Closed` status. (Team added by project-owner decision: resolved/closed tickets are completed historical work, and a team change afterward would corrupt historical ownership and team performance reporting.) | Ticket aggregate |
| `TICKET-INV-09` | Every state-changing method on `Ticket` appends exactly one `TicketEvent`; adding a comment appends one too. | Ticket aggregate |
| `TICKET-INV-10` | All timestamps are UTC. `TimeProvider` is injected wherever time is needed; `DateTime.UtcNow` is banned outside composition roots and seeders. | Ticket aggregate (domain-wide, via injected `TimeProvider`) |

## 4. Workflow state machine (`TICKET-WF`)

| ID | Rule | Owner |
|---|---|---|
| `TICKET-WF-01` | `Open → Assigned` via `Assign`; the assignee must be an active member of the ticket's team. | Ticket aggregate |
| `TICKET-WF-02` | `Assigned → Open` via `Unassign`; clears the assignee. | Ticket aggregate |
| `TICKET-WF-03` | `Assigned → InProgress` via `StartWork`; the actor must be the assignee (Manager/Admin may override). | Ticket aggregate |
| `TICKET-WF-04` | `Assigned` or `InProgress` `→ Pending` via `PutOnHold`; a reason is required; this starts the SLA pause. | Ticket aggregate (transition) / SlaPolicy (pause semantics, `SLA-RULE-07`) |
| `TICKET-WF-05` | `Pending → InProgress` via `Resume`; ends the SLA pause and extends `SlaDueAt` by the paused duration. | Ticket aggregate (transition) / SlaPolicy (`SLA-RULE-07`) |
| `TICKET-WF-06` | `Pending → Assigned` via `Resume`; permitted when work has not restarted. | Ticket aggregate |
| `TICKET-WF-07` | `InProgress` or `Pending` `→ Resolved` via `Resolve`; resolution code and notes are required; sets `SlaMet`. | Ticket aggregate (transition) / SlaPolicy (`SLA-RULE-08`) |
| `TICKET-WF-08` | `Resolved → Closed` via `Close`; performed by Manager/Admin, or by the requester confirming. | Ticket aggregate (transition) / TicketAccessPolicy (who may close) |
| `TICKET-WF-09` | `Resolved` or `Closed` `→ Open` or `Assigned` via `Reopen`; a reason is required, `ReopenCount` increments, and a new SLA cycle begins. | Ticket aggregate (transition) / SlaPolicy (`SLA-RULE-09`) |
| `TICKET-WF-10` | Any transition not explicitly listed in the state machine is rejected with a domain error. | Ticket aggregate |
| `TICKET-WF-11` | `Reassign` is legal while `Assigned`, `InProgress`, or `Pending`. It increments `AssignmentChangeCount` and records the old and new assignee. Status does not change, except `InProgress → Assigned` when reassigned to a different person. | Ticket aggregate |
| `TICKET-WF-12` | Auto-close is out of MVP scope — it would require a scheduler, which FlowOps deliberately does not have (ADR-0006). | Ticket aggregate (deliberate non-implementation) |
| `TICKET-WF-13` | There is no `Cancelled` status in MVP; `Resolve` with resolution `Withdrawn` covers that case. | Ticket aggregate |

## 5. Authorization model (`AUTH-RULE`)

| ID | Rule | Owner |
|---|---|---|
| `AUTH-RULE-01` | There are exactly four roles — `Admin`, `Manager`, `Agent`, `Viewer` — with one primary role per user. | Directory module / ASP.NET Core Identity roles |
| `AUTH-RULE-02` | Per-role capability matrix — see table immediately below. | TicketAccessPolicy (ticket-scoped decisions) / role attributes (coarse gate) |
| `AUTH-RULE-03` | A coarse authorization gate (`[Authorize(Roles=...)]`) sits on every Razor Page and API controller. It catches the obvious case only; it is not the authoritative check. | Razor Pages / API controllers (gate only, not source of truth) |
| `AUTH-RULE-04` | Resource-level authorization decisions — `CanCreate`, `CanView`, `CanComment`, `CanAssign`, `CanTransition`, `CanReopen`, `CanSeeInternalComments` — are made by a single `TicketAccessPolicy`, given a `TicketAuthorizationSnapshot` and the current user. Every application service calls it before mutating or returning a ticket; Razor Pages and the API call the same class. `CanCreate` is the one exception to the snapshot argument: no ticket exists yet at creation time, so it is decided from the current user's role alone, matching the "Create ticket" row of the AUTH-RULE-02 matrix. | TicketAccessPolicy |
| `AUTH-RULE-05` | List queries scope visibility (team membership) as a `WHERE` clause in the query itself. Results are never fetched broadly and filtered in memory. | TicketQueryService |
| `AUTH-RULE-06` | A user cannot change their own role. | UserService (Directory module) |
| `AUTH-RULE-07` | The last active Admin cannot be demoted or deactivated. | UserService |
| `AUTH-RULE-08` | Role assignment is Admin-only and is audited. | UserService |
| `AUTH-RULE-09` | While demo mode is enabled, seeded persona accounts cannot have their credentials or role changed by anyone, including an Admin. | UserService (demo mode guard) |
| `AUTH-RULE-10` | The AUTH-RULE-02 "Team analytics" row (Phase 10) is a deliberately narrower scope than "View tickets", not the same one: `TicketAccessPolicy.GetAnalyticsScope` resolves Admin → every ticket, Manager → `ManagedTeamIds`, Viewer → `MemberTeamIds`, and — the row's whole reason for existing — Agent → only tickets currently assigned to them, never their team's tickets generally. `AnalyticsQueryService` applies this scope to every aggregate query, so a KPI or workload count can never disclose data the capability matrix does not grant the caller. | TicketAccessPolicy |
| `AUTH-RULE-11` | Phase 20 (ADR-0019): the dashboard's filter bar (date range, team, work type) is never an authorization mechanism. `AnalyticsQueryService.ApplyDashboardScope` is applied strictly *after* AUTH-RULE-10's organization/role scope, never before or instead of it — a `TeamId` naming a team outside the caller's own scope (wrong organization, or a real team their role cannot see) intersects with an already-narrowed query and yields zero rows, indistinguishable from "no such team." The filter bar's own team dropdown is populated only from the caller's own accessible teams, so the control cannot be used to enumerate other teams either. | AnalyticsQueryService |

**`AUTH-RULE-02` capability matrix** (Admin / Manager / Agent / Viewer):

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

## 6. SLA engine (`SLA-RULE`)

| ID | Rule | Owner |
|---|---|---|
| `SLA-RULE-01` | `SlaConfiguration` is `{ Id, WorkType?, Priority, TargetMinutes, RiskThresholdPercent }`. Resolution order, most specific first: exact `(WorkType, Priority)` match, then the `(null, Priority)` default row. | SlaConfigurationService / SlaPolicy |
| `SLA-RULE-02` | Seeded default targets are Critical 240, High 480, Medium 1440, Low 4320 minutes, with an 80% risk threshold. | SlaConfigurationService (seed values) |
| `SLA-RULE-03` | Editing an `SlaConfiguration` does not retroactively change existing tickets; each ticket captures `SlaTargetMinutes` at the moment its SLA clock starts. | SlaPolicy / Ticket aggregate |
| `SLA-RULE-04` | A ticket carries `SlaTargetMinutes`, `SlaStartedAt`, `SlaDueAt`, `SlaPausedMinutes`, `PendingSince?`, and `SlaMet?` (a nullable bool, set once at resolution). | Ticket aggregate |
| `SLA-RULE-05` | The SLA clock starts at ticket creation: `SlaDueAt = SlaStartedAt + SlaTargetMinutes`. | SlaPolicy |
| `SLA-RULE-06` | A priority change recomputes `SlaDueAt = SlaStartedAt + newTargetMinutes + SlaPausedMinutes` and updates `SlaTargetMinutes`; the change is audited with old and new values. | SlaPolicy |
| `SLA-RULE-07` | Pending pauses the SLA clock: `PendingSince` is recorded when the ticket goes on hold. On `Resume` or `Resolve`, the elapsed pending minutes are added to `SlaPausedMinutes` and `SlaDueAt` is pushed out by that same amount. | SlaPolicy |
| `SLA-RULE-08` | `Resolve` sets `SlaMet = ResolvedAt <= SlaDueAt` as a point-in-time fact. It is persisted and never recomputed afterward. | SlaPolicy |
| `SLA-RULE-09` | `Reopen` starts a new SLA cycle: `SlaStartedAt = now`, the target is re-resolved from the current configuration, `SlaPausedMinutes = 0`, and `SlaMet = null`. The previous cycle's outcome remains visible in `ticket_events`. | SlaPolicy |
| `SLA-RULE-10` | SLA status is computed, never stored: `Resolved`/`Closed` → `Met`/`Breached` (from the persisted `SlaMet`); `Pending` → `Paused`; `now >= SlaDueAt` → `Breached`; elapsed% ≥ risk threshold → `AtRisk`; otherwise → `Within`. `elapsed% = (now − SlaStartedAt − SlaPausedMinutes) / SlaTargetMinutes`. | SlaPolicy |
| `SLA-RULE-11` | There is no background job computing or maintaining SLA state in MVP (ADR-0006); SLA status is derived fresh on every read. | SlaPolicy (deliberate non-implementation) |
| `SLA-RULE-12` | `SlaPolicy` is the single authoritative implementation of SLA deadline and status calculation. No other file — dashboard, API, UI, or ad-hoc query — computes it independently. | SlaPolicy |

## 7. Attention ("at-risk") engine (`ATTN-RULE`)

| ID | Rule | Owner |
|---|---|---|
| `ATTN-RULE-01` | `AttentionPolicy` is a pure function from a ticket, `now`, the `AttentionOptions` thresholds, and the ticket's current SLA risk threshold, to a list of `AttentionSignal { Code, Severity, Headline, DetectedAt }` — and is the single authoritative implementation of attention-signal detection. It reads the `Ticket` aggregate directly (including its events, which the `Stalled` signal needs) rather than a separate snapshot type; "pure" here means it performs no I/O and mutates nothing, not that it takes a purpose-built input record. | AttentionPolicy |
| `ATTN-RULE-02` | Exactly eight signals are defined, each with a fixed trigger condition and severity — see table immediately below. | AttentionPolicy |
| `ATTN-RULE-03` | All signal thresholds live in one `AttentionOptions` class, bound from configuration; none are scattered constants. | AttentionPolicy / AttentionOptions |
| `ATTN-RULE-04` | Ranking is deterministic: highest signal severity, then nearest `SlaDueAt`, then priority, then oldest `CreatedAt`, with ties broken by `Id`. There is no score, weighting, or "AI" ranking. | AttentionPolicy |
| `ATTN-RULE-05` | `AttentionQueryService` runs an indexed SQL prefilter to produce candidate tickets; `AttentionPolicy` evaluates the candidates in memory to produce signals. The prefilter narrows candidates — it never replaces the policy's own evaluation. | AttentionQueryService (prefilter) / AttentionPolicy (evaluation) |
| `ATTN-RULE-06` | The prefilter must be a provable superset of the policy's triggers. Any new signal or widened threshold changes the prefilter in the same commit and must be covered by a test seeding one ticket per signal — including near-miss cases — that asserts the prefilter returns every one of them. | AttentionQueryService + AttentionPolicy jointly (the one deliberate, tested exception to "one authoritative implementation") |
| `ATTN-RULE-07` | *(Optional, Phase 10)* Workload score = Σ over the assignee's non-terminal tickets of `priorityWeight × (1 + riskBoost)`, where `priorityWeight` is Critical=5/High=3/Medium=2/Low=1 and `riskBoost` is Breached=+1.0/AtRisk=+0.5/otherwise 0. | AttentionPolicy |

**`ATTN-RULE-02` signal table:**

| Code | Trigger | Severity | Example headline |
|---|---|---|---|
| `SlaBreached` | SLA status is Breached | Critical | "SLA breached 2h 14m ago" |
| `SlaAtRisk` | SLA status is AtRisk | High | "SLA breach in 42 minutes" |
| `Overdue` | `DueDate < now`, not terminal | High | "2h overdue" |
| `UnassignedUrgent` | Critical/High priority, unassigned for more than 15 minutes | Critical | "Critical and unassigned for 38m" |
| `Aging` | Open longer than the priority's aging threshold (Critical 1 day, High 3 days, Medium 10 days, Low 30 days) | Medium | "Open 14 days" |
| `Stalled` | Pending for more than 3 days, or InProgress with no event in 5 days | Medium | "No activity for 6 days" |
| `Churn` | `AssignmentChangeCount >= 3` | Medium | "3 reassignment events" |
| `Reopened` | `ReopenCount >= 1` and currently non-terminal | Medium | "Reopened twice" |

**Terminal-ticket resolution (confirmed, not an open question):** `AttentionPolicy` never produces
a signal for a `Resolved` or `Closed` ticket. This is stated explicitly in the trigger text for only
two signals (`Overdue`, `Reopened`), and is structurally impossible for three more regardless of any
extra guard (`SlaAtRisk` and `Stalled` can only be computed for statuses `Open`/`Assigned`/
`InProgress`/`Pending`; `UnassignedUrgent` requires `AssigneeId IS NULL`, which cannot occur on a
resolved/closed ticket per `TICKET-INV-04`'s chain of custody). The remaining three
(`SlaBreached`, `Aging`, `Churn`) are *not* structurally excluded by their own trigger text and
therefore require a deliberate decision: they are suppressed for terminal tickets too, on the
authority of CLAUDE.md §1/§9 ("ranked list of *at-risk work*") and §22 ("No work is at risk right
now" is the correct empty state) — completed work is definitionally not at-risk work, and no action
exists for a caller to take on a closed ticket, so surfacing one in the attention queue would
contradict the feature's own stated purpose. `AttentionPolicy.Evaluate` implements this as a single
early return for `Resolved`/`Closed`, applied uniformly rather than signal-by-signal.

## 8. Audit history (`AUDIT-RULE`)

| ID | Rule | Owner |
|---|---|---|
| `AUDIT-RULE-01` | `TicketEvent` is append-only. There is no update path and no delete path — in the domain or in any service. | Ticket aggregate |
| `AUDIT-RULE-02` | `TicketEvent` carries `Id`, `TicketId`, `EventType`, `ActorUserId`, `OccurredAt`, and optional `Field`, `OldValue`, `NewValue`, `Note`. | Ticket aggregate |
| `AUDIT-RULE-03` | Sixteen event types are defined: `Created`, `Assigned`, `Reassigned`, `Unassigned`, `StatusChanged`, `PriorityChanged`, `CategoryChanged`, `TeamChanged`, `DueDateChanged`, `SlaRecalculated`, `PutOnHold`, `Resumed`, `Resolved`, `Reopened`, `Closed`, `CommentAdded`. | Ticket aggregate |
| `AUDIT-RULE-04` | Events are children of the `Ticket` aggregate. A state-changing method mutates `Ticket` state and appends its `TicketEvent` in the same object graph, persisted by one `SaveChangesAsync`. There is no domain-event dispatcher, outbox, or interceptor, and no code path exists where a status change occurs without its audit row. | Ticket aggregate |
| `AUDIT-RULE-05` | Audit records business changes only. Page views, filter changes, sorts, and expansions are never logged as `TicketEvent`s. | Ticket aggregate |
| `AUDIT-RULE-06` | `TicketComment` (human operational context) and `TicketEvent` (machine record) are stored and queried separately, even though they render together in one merged, chronological activity timeline on the ticket page. | Ticket aggregate (both `TicketComment` and `TicketEvent` are its children; the UI only merges for display) |

**`AUDIT-RULE-03` / `TICKET-INV-09` resolution for `SlaRecalculated`:** `SlaRecalculated` is defined
as one of the sixteen event types because AUDIT-RULE-03 requires the type to exist, but no method
appends it standalone. A priority change (`SLA-RULE-06`) both changes `Priority` and recomputes
`SlaDueAt` in the same call — under `TICKET-INV-09` ("every state-changing method appends *exactly
one* `TicketEvent`"), that one call may only append one event, so the SLA recompute is folded into
the single `PriorityChanged` event rather than emitting a second `SlaRecalculated` event alongside
it. This is a resolution of the two rules' interaction, not a change to either rule: `TICKET-INV-09`
is unmodified (still exactly one event per method), and `SlaRecalculated` remains a valid,
correctly-defined event type — simply one with no current trigger in the documented workflow. If a
future rule change ever needs to record an SLA recompute that is *not* accompanied by a priority
change, `SlaRecalculated` is already available for that method to append as its one event.

## 9. Persistence constraints that reinforce domain rules (`PERSIST-RULE`)

These are **not** domain rules in their own right — they are database-level guardians that
reinforce a domain rule already catalogued above. Each entry names the domain rule it reinforces.
Application code (the rows above) remains the origin of the rule; the database is a second,
independent enforcement layer, per CLAUDE.md §7.4 ("Application code is not the only guardian").

| ID | Rule | Reinforces | Owner |
|---|---|---|---|
| `PERSIST-RULE-01` | Every relationship has a FK with a deliberate `OnDelete` behavior — `Restrict` for reference data, `Cascade` only for a ticket's own comments and events. | `TICKET-ENT-01`, `TICKET-ENT-02` | Infrastructure/Persistence (entity configurations) |
| `PERSIST-RULE-02` | Each enum column has a `CHECK` constraint listing its valid values. | `TICKET-ENUM-01`–`04` | Infrastructure/Persistence |
| `PERSIST-RULE-03` | `CHECK (status <> 'Closed' OR closed_at IS NOT NULL)` and the equivalent constraint for resolved/resolution fields. | `TICKET-INV-06`, `TICKET-INV-07` | Infrastructure/Persistence |
| `PERSIST-RULE-04` | `CHECK (sla_due_at > sla_started_at)`. | `SLA-RULE-05` | Infrastructure/Persistence |
| `PERSIST-RULE-05` | Unique constraints on `tickets.reference`, `(team_id, name)` on categories, and `(work_type, priority)` on `SlaConfiguration` (`work_type` nullable for the default row). | `TICKET-ENT-03`, `SLA-RULE-01` | Infrastructure/Persistence |
| `PERSIST-RULE-06` | `RowVersion` is mapped to PostgreSQL `xmin` as a concurrency token on `Ticket`, so a concurrent edit produces a conflict rather than a silent lost update. | `TICKET-ENT-02` | Infrastructure/Persistence |

---

## Rule count summary

| Category | Count | ID range |
|---|---:|---|
| `TICKET-ENT` | 5 | `TICKET-ENT-01`–`05` |
| `TICKET-ENUM` | 4 | `TICKET-ENUM-01`–`04` |
| `TICKET-INV` | 10 | `TICKET-INV-01`–`10` |
| `TICKET-WF` | 13 | `TICKET-WF-01`–`13` |
| `AUTH-RULE` | 11 | `AUTH-RULE-01`–`11` |
| `SLA-RULE` | 12 | `SLA-RULE-01`–`12` |
| `ATTN-RULE` | 7 | `ATTN-RULE-01`–`07` |
| `AUDIT-RULE` | 6 | `AUDIT-RULE-01`–`06` |
| `PERSIST-RULE` | 6 | `PERSIST-RULE-01`–`06` |
| `ORG-RULE` | 14 | `ORG-RULE-01`–`14` |
| **Total** | **88** | — |

Every rule above has a stable ID and a stated, non-UI, non-database-only owner where the rule is a
genuine domain rule (`PERSIST-RULE` entries are the deliberate exception, explicitly scoped to
database reinforcement). This satisfies the Phase 1 gate: *"Every rule has an id and a stated
owner."*

---

## 10. Multi-tenancy & organizations (`ORG-RULE`)

Phase 16 introduced `Organization`/`OrganizationMembership` as a new, outer authorization boundary
(ADR-0015); Phase 18 added invitations and member management on top of it (ADR-0017); Phase 19
replaced the original deterministic current-organization pick with explicit, persisted context
(ADR-0018). All are authoritative in CLAUDE.md — `ORG-RULE-01`–`06` and `ORG-RULE-14` in §6.3,
`ORG-RULE-07`–`13` in §6.4 — and their canonical text lives there; this table exists so every
`ORG-RULE-*` id is catalogued alongside every other rule prefix, per the ID scheme above.

| ID | Rule | Owner |
|---|---|---|
| `ORG-RULE-01` | An Organization is the outer authorization boundary: every Team, Project, and (transitively, via Team) Ticket belongs to exactly one Organization. | `Organization`, `Team`, `Project` (Domain) |
| `ORG-RULE-02` | A user's role is a fact about their `OrganizationMembership`, not about the user — the same user may hold a different `UserRole` in each Organization they belong to. | `OrganizationMembership` (Domain) |
| `ORG-RULE-03` | The organization boundary is applied before, and independently of, every existing team/role scoping rule (`AUTH-RULE-*`) — including Admin's unconditional team-scope bypass, which stops at the organization boundary. | `TicketQueryService`, `AttentionQueryService`, `AnalyticsQueryService`, `TicketService.MutateAsync` |
| `ORG-RULE-04` | The caller's current organization is never trusted from a client-supplied value — it is re-derived from `OrganizationMembership` rows on every request, the same way `CurrentUser.Role`/team membership already are. Preserved until a future phase deliberately introduces an organization switcher. | `CurrentUserAccessor` |
| `ORG-RULE-05` | An Organization's administrator is represented purely as an `OrganizationMembership` with `Role = Admin` — no separate ownership field exists. | `OrganizationMembership` (Domain) |
| `ORG-RULE-06` | Every organization-scoped read and write must enforce the boundary: queries capable of exposing organization-owned data filter by organization; ticket mutation enforces it at ticket load time; a cross-organization resource id is refused indistinguishably from a nonexistent one; ticket creation rejects a Team/Category/Project outside the caller's organization. | `TicketQueryService`, `AttentionQueryService`, `AnalyticsQueryService`, `TicketService` |
| `ORG-RULE-07` | An invitation is organization-scoped: it belongs to exactly one Organization. | `Invitation` (Domain) |
| `ORG-RULE-08` | An invitation token is single-use; concurrent acceptance of the same token resolves via the `xmin` optimistic-concurrency token (ADR-0011), never a second concurrency mechanism. | `Invitation` (Domain), `InvitationService` (Application) |
| `ORG-RULE-09` | An invitation is bound to its invited email; acceptance requires the accepting account's normalized email to match exactly, and a new account created via acceptance always uses the invitation's own email. | `Invitation` (Domain), `InvitationService` (Application) |
| `ORG-RULE-10` | Invitations expire after a fixed lifetime, checked server-side on every acceptance attempt; expired rows are never deleted. | `Invitation` (Domain) |
| `ORG-RULE-11` | Only Admin/Manager may invite or manage members; Manager is least-privilege (Agent/Viewer only — never Admin, never another Manager). Every check resolves the organization server-side, never from a client-supplied id. | `OrganizationAccessPolicy` (Domain) |
| `ORG-RULE-12` | No action (account deletion, membership role change, member removal) may leave an organization with zero Admins. | `SoleAdminGuard` (Application) |
| `ORG-RULE-13` | Removing a member deletes only the `OrganizationMembership` row — the user's identity, other-organization memberships, and historical ticket/comment/event records are untouched. | `MembershipService` (Application) |
| `ORG-RULE-14` | Organization context is explicit, persisted, and re-validated on every use; a selected value is never itself an authorization grant. Switching is allowed only among the caller's own real memberships, fails identically for an inaccessible or nonexistent id, accepts no client-supplied return path, and is cleared on logout. | `CurrentUserAccessor` (Application) |
