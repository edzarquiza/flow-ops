# FlowOps Domain Test Plan (Phase 2 design — implemented in Phase 3+)

This document maps every rule in `docs/domain-model.md` to a test strategy and the test project
that owns it, per CLAUDE.md §15's three-project split. No test code is written here. No behavior
beyond what `docs/domain-model.md` already states is introduced — where a rule needs a boundary
case (e.g. "just above/below threshold"), that boundary is implied directly by the rule's own
wording, not invented.

Test project key: **DT** = `FlowOps.Domain.Tests` (no I/O, no database) · **AT** =
`FlowOps.Application.Tests` (real PostgreSQL via Testcontainers) · **WT** = `FlowOps.Web.Tests`
(`WebApplicationFactory`, real authentication).

---

## 1. `TICKET-ENT` — aggregate boundary & core entity structure

| Rule | Test strategy | Project |
|---|---|---|
| `TICKET-ENT-01` | Construct a `Ticket`; assert its `Comments`/`Events` collections are populated only via `Ticket` methods (`AddComment`, transition methods) — no public constructor/setter path exists to add one directly. A compile-time/API-shape assertion more than a runtime one; cover with a test that calling any mutation method always yields a corresponding collection entry. | DT |
| `TICKET-ENT-02` | For each transition method, assert `Status`, `AssigneeId`, `SlaDueAt` change only as a result of calling that method — no other public API surface can set them (reflection-based "no public setters besides via constructor" test, or simply the absence of setters makes this a compile-time guarantee, asserted once). | DT |
| `TICKET-ENT-03` | Not a `Ticket` behavior test — covered structurally by `Team`/`Category`/`Project`/`SlaConfiguration` having no aggregate methods (no domain-rule test needed beyond confirming they are plain classes with property setters, which is a design-time fact, not a unit test). | DT (design-time only, no dedicated test required) |
| `TICKET-ENT-04` | Assert `Ticket`'s public API only exposes `Guid` for `RequesterId`/`AssigneeId` — no reference to an Identity type compiles into `FlowOps.Domain` (enforced by `FlowOps.Domain` having no Identity package reference at all — a project-reference assertion, not a unit test). | DT (project-reference check, not a runtime test) |
| `TICKET-ENT-05` | `TicketAccessPolicy.CanSeeInternalComments`: true for Admin/Manager/Agent, false for Viewer, given an internal comment — table-driven over all four roles. | DT |

## 2. `TICKET-ENUM` — enumerations

| Rule | Test strategy | Project |
|---|---|---|
| `TICKET-ENUM-01`…`04` | Enumerate `Enum.GetValues<T>()` for each of `WorkType`, `Priority`, `Status`, `Resolution` and assert the exact expected member set and count — a change to any enum trips this test immediately, which is the point (guards against silent drift from the four-work-type decision, ADR-0003). | DT |

## 3. `TICKET-INV` — ticket invariants

| Rule | Test strategy | Project |
|---|---|---|
| `TICKET-INV-01` | Boundary tests on `Title` (4/5/200/201 chars) and `Description` (8000/8001 chars) via the ticket-creation factory method; assert `DomainRuleException` at the invalid boundary. | DT |
| `TICKET-INV-02` | Create with a category that does not belong to the given team → rejected. Create with matching team/category → accepted. | DT |
| `TICKET-INV-03` | Assign to a user who is not an active member of the ticket's team → rejected. Assign to an active member → accepted. Reassign the ticket to a different team → assert assignee is cleared and a subsequent `Assign` is required. | DT |
| `TICKET-INV-04` | For each transition landing in `Assigned`/`InProgress`, assert `AssigneeId != null` afterward; assert the invariant is unreachable in violation (i.e., no method can produce `Assigned`/`InProgress` with a null assignee). | DT |
| `TICKET-INV-05` | `PutOnHold` with an empty/whitespace reason → rejected. With a non-empty reason → accepted, `PendingReason` set. | DT |
| `TICKET-INV-06` | `Resolve` with missing resolution code, or notes < 10 chars → rejected. With both present and valid → accepted, `ResolvedAt` set. Same table for `Close`. | DT |
| `TICKET-INV-07` | After `Close`, assert `ClosedAt != null`. For every other status, assert `ClosedAt == null` (covered across the transition test matrix in §4 below, not a standalone test). | DT |
| `TICKET-INV-08` | On a `Resolved` ticket and a `Closed` ticket, attempt assignment, priority change, category change, and **team change** → each rejected with a domain error. Also assert team change remains legal on every non-terminal status the domain contract permits it from (`Open`, `Assigned`, `InProgress`, `Pending`). | DT |
| `TICKET-INV-09` | For every state-changing method, assert `Events.Count` increases by exactly one after the call; for `AddComment`, assert the same. | DT |
| `TICKET-INV-10` | Inject a fake `TimeProvider` fixed at a known instant; assert every timestamp the method sets equals that instant (not `DateTime.UtcNow` at test-run time) — this is what proves the ban is actually honored, not just documented. | DT |

## 4. `TICKET-WF` — workflow state machine

| Rule | Test strategy | Project |
|---|---|---|
| `TICKET-WF-01`…`09` | One table-driven test enumerating every `(fromStatus, method, expectedToStatus)` row from the state table, asserting success and the documented side effects (assignee set/cleared, `SlaDueAt` recomputed, `ReopenCount`/`AssignmentChangeCount` incremented as applicable). `TICKET-WF-03` additionally asserts actor-must-be-assignee unless Manager/Admin; `TICKET-WF-08` asserts Close is restricted to Manager/Admin/requester. | DT |
| `TICKET-WF-10` | The same table extended with every `(fromStatus, method)` pair **not** in the legal table → each asserted to throw a domain error. This is the exhaustive "everything else is rejected" complement to the table above — both halves of one matrix. | DT |
| `TICKET-WF-11` | `Reassign` in each of `Assigned`/`InProgress`/`Pending` → `AssignmentChangeCount` increments, old/new assignee recorded; from `InProgress` to a different person → status becomes `Assigned`; to the same person → status unchanged. `Reassign` from `Open`/`Resolved`/`Closed` → rejected (covered by the `TICKET-WF-10` matrix). | DT |
| `TICKET-WF-12` | No auto-close method exists on `Ticket` — a negative/design-time check (no test asserts behavior that does not exist); confirmed by the absence of any such method in the transition matrix above. | DT (design-time only) |
| `TICKET-WF-13` | Assert `Resolve` accepts `Resolution.Withdrawn` as a valid code and produces the same audit/SLA behavior as any other resolution; assert no `Cancelled` value exists in `Status` (covered by `TICKET-ENUM-03`'s exhaustive enum-value test). | DT |

## 5. `AUTH-RULE` — authorization model

| Rule | Test strategy | Project |
|---|---|---|
| `AUTH-RULE-01` | Assert exactly the four roles exist in seeded Identity roles at startup/migration time (an integration-level fact, not a domain unit test). | AT |
| `AUTH-RULE-02` | Full truth table: for every `(role, capability)` cell in the matrix, a `TicketAccessPolicy` test asserting the exact allow/deny/scope result — this is the single largest DT suite in the project, matching CLAUDE.md §15's call for `TicketAccessPolicy` to be "unit-tested exhaustively as a truth table." | DT |
| `AUTH-RULE-03` | `[Authorize(Roles=...)]` presence on each Razor Page/API controller is asserted by an endpoint test hitting it anonymously and with a wrong-role user, expecting redirect/401/403 — this is a coarse-gate smoke test, not a substitute for `AUTH-RULE-02`'s exhaustive table. | WT |
| `AUTH-RULE-04` | For each `TicketAccessPolicy` method (`CanView`, `CanComment`, `CanAssign`, `CanTransition`, `CanReopen`, `CanSeeInternalComments`), assert every `Application` service that touches a ticket calls it before mutating/returning — enforced by a WT integration test per role/team-membership combination hitting each endpoint, expecting a 403 exactly where the policy says so, plus the DT truth table underneath. | DT (policy logic) + WT (call-site enforcement, per role) |
| `AUTH-RULE-05` | Seed tickets across two teams; log in as a user scoped to one team; assert the list query returns only that team's tickets, and assert (via generated-SQL inspection or row-count-before-filter) that the scoping happened in the `WHERE` clause, not after fetching. | AT |
| `AUTH-RULE-06` | Attempt to change one's own role via the application service → rejected regardless of role, including Admin. | AT |
| `AUTH-RULE-07` | With exactly one active Admin, attempt to demote or deactivate them → rejected. With two active Admins, demoting one → accepted. | AT |
| `AUTH-RULE-08` | Assign a role as Admin → `TicketEvent`-equivalent audit record for the role change is written (per CLAUDE.md §12 admin-config-change logging) and the action is rejected for non-Admin callers. | AT |
| `AUTH-RULE-09` | With demo mode enabled, attempt to change a seeded persona's password/role as Admin → rejected; with demo mode disabled, the same action → accepted (proves the guard is demo-mode-conditional, not a permanent restriction). | AT |

## 6. `SLA-RULE` — SLA engine

| Rule | Test strategy | Project |
|---|---|---|
| `SLA-RULE-01` | `SlaPolicy` resolution-order test: exact `(WorkType, Priority)` row present → used; absent → falls back to `(null, Priority)` default row; neither present → a defined error/fallback (per whatever `docs/domain-model.md`/CLAUDE.md ultimately specifies as the no-match case — flagged for confirmation in Phase 3 if a gap surfaces). | DT |
| `SLA-RULE-02` | Assert seed data produces exactly Critical 240 / High 480 / Medium 1440 / Low 4320 minutes and an 80% threshold (a seeding/fixture assertion). | AT |
| `SLA-RULE-03` | Change an `SlaConfiguration`'s `TargetMinutes`; assert an existing ticket's `SlaTargetMinutes`/`SlaDueAt` are unchanged; assert a newly created ticket picks up the new value. | AT (needs persisted config + existing ticket fixture) |
| `SLA-RULE-04` | Construct a ticket; assert all five SLA fields are present and correctly initialized at creation. | DT |
| `SLA-RULE-05` | `SlaDueAt == SlaStartedAt + SlaTargetMinutes` at creation, exact-minute boundary check. | DT |
| `SLA-RULE-06` | Change priority mid-flight with a non-zero `SlaPausedMinutes` already accrued; assert `SlaDueAt = SlaStartedAt + newTarget + SlaPausedMinutes` exactly (this is the specific formula the `flowops-sla-attention` skill calls out as easy to get wrong by resetting `SlaStartedAt`). | DT |
| `SLA-RULE-07` | `PutOnHold` then `Resume` after a known fake-clock interval → `SlaPausedMinutes` increases by exactly that interval and `SlaDueAt` shifts by the same amount; repeat ending in `Resolve` instead of `Resume` to prove both exit paths accrue the pause. Assert `SlaPausedMinutes` does **not** change at the moment of `PutOnHold` itself (only `PendingSince` does) — this is the specific wrong-event mistake the skill warns about. | DT |
| `SLA-RULE-08` | Resolve exactly at `SlaDueAt` → `SlaMet == true` (boundary is inclusive, `ResolvedAt <= SlaDueAt`). One tick after → `SlaMet == false`. Assert a later call cannot change `SlaMet` (no method exists to recompute it post-resolution). | DT |
| `SLA-RULE-09` | `Reopen` a resolved ticket with fake-clock `now`; assert `SlaStartedAt == now`, target re-resolved from current config (possibly different from the original if config changed), `SlaPausedMinutes == 0`, `SlaMet == null`; assert the prior cycle's `TicketEvent`s remain unchanged. | DT |
| `SLA-RULE-10` | `SlaPolicy.GetStatus` table-driven over all five branches: Resolved+Met, Resolved+Breached (SlaMet false), Pending, now ≥ SlaDueAt (Breached), elapsed% at/just-above/just-below RiskThreshold (AtRisk vs Within boundary), and comfortably-within (Within). Every boundary named in the rule gets an exact-boundary case, not just an interior one. | DT |
| `SLA-RULE-11` | Negative/design-time check: no `IHostedService`/background worker exists in the solution that touches SLA fields (confirmed by absence, not a runtime test). | AT (solution-shape check at Phase 3+) |
| `SLA-RULE-12` | Confirm (by code search / architecture test, e.g. a dependency-boundary test asserting no other project defines SLA-deadline math) that `SlaPolicy` is the only place `SlaDueAt`-style comparisons occur outside itself — an architectural fitness test, not a behavior test. | AT (architecture/fitness test) |

## 7. `ATTN-RULE` — attention engine

| Rule | Test strategy | Project |
|---|---|---|
| `ATTN-RULE-01` | `AttentionPolicy.Evaluate` is a pure function: same snapshot + same `now` in → same signals out, called twice, asserting reference/value equality of results (no hidden I/O or mutation). | DT |
| `ATTN-RULE-02` | One test ticket snapshot per signal, each engineered to trigger exactly that signal and no other; assert the signal fires with the documented severity and a non-empty headline. Pair with a near-miss snapshot per signal (e.g. unassigned for 14 minutes vs 16) asserting the signal does **not** fire just below threshold. | DT |
| `ATTN-RULE-03` | Construct `AttentionPolicy` with a non-default `AttentionOptions` (e.g. a different aging threshold) and assert the evaluated result respects the injected threshold, not a hardcoded one — proves thresholds aren't scattered constants. | DT |
| `ATTN-RULE-04` | Construct a set of tickets whose signals collide on severity, then on `SlaDueAt`, then on priority, then on `CreatedAt`; assert the ranking breaks each tie exactly as specified, down to the final `Id` tiebreak. | DT |
| `ATTN-RULE-05` | The prefilter/policy split itself isn't independently "tested" — it's tested via `ATTN-RULE-06`'s superset test, which is the one place this split is directly verified. | AT |
| `ATTN-RULE-06` | **The prefilter-superset test** (CLAUDE.md §9.3): seed one ticket per signal in `ATTN-RULE-02`'s table, including every near-miss case, into a real Postgres database; run `AttentionQueryService`'s SQL prefilter; assert every signal-triggering ticket is present in the prefilter's candidate set (near-miss tickets may or may not appear — the assertion is superset, not exact match). This test must be re-run and kept green any time a signal or threshold changes; it is the one deliberately duplicated piece of logic in the whole system and its test is what makes that duplication safe. | AT |
| `ATTN-RULE-07` | Table-driven over priority × risk-boost combinations, asserting the exact score formula for a synthetic set of the assignee's non-terminal tickets (optional — implement alongside Phase 10 if built). | DT |

## 8. `AUDIT-RULE` — audit history

| Rule | Test strategy | Project |
|---|---|---|
| `AUDIT-RULE-01` | Design-time/API-shape check: `TicketEvent` has no public setters and no service exposes an update/delete method touching `ticket_events` — confirmed by the absence of such a method (no test can "prove a negative" better than the API surface itself; an architecture fitness test can assert no `UPDATE`/`DELETE` SQL is ever generated against `ticket_events` in the AT suite's captured query log). | DT (API shape) + AT (generated-SQL fitness check) |
| `AUDIT-RULE-02` | Construct a `TicketEvent` via a `Ticket` method and assert all required fields (`Id` after save, `TicketId`, `EventType`, `ActorUserId`, `OccurredAt`) are populated; optional fields (`Field`/`OldValue`/`NewValue`/`Note`) populated only where the triggering method defines them. | DT |
| `AUDIT-RULE-03` | Enumerate `TicketEventType` and assert the exact 16-member set (mirrors the `TICKET-ENUM` exhaustive-enum-value pattern). | DT |
| `AUDIT-RULE-04` | Call a transition method, then assert both the `Ticket`'s new state and its new `TicketEvent` are visible in the same unsaved change set (DT), and — separately — that a single `SaveChangesAsync` persists both, verified by killing the connection/transaction mid-way in an AT test and confirming neither the state change nor the event survive a rollback (proves atomicity, not just co-occurrence in memory). | DT (co-occurrence) + AT (transactional atomicity) |
| `AUDIT-RULE-05` | Negative check: no application code path calls any ticket-event-writing method in response to a page view, filter change, sort, or list-expansion action (confirmed by the absence of such call sites in the query/read-only services — an architecture/code-search check, not a behavior test). | AT (architecture check) |
| `AUDIT-RULE-06` | Query `ticket_comments` and `ticket_events` independently for a given ticket and assert each returns only its own record type; assert the UI-level merged timeline (built in a later phase) is a presentation-only concatenation, not a shared table. | AT |

## 9. `PERSIST-RULE` — database-level reinforcement

These are database-level guardians, not domain behavior, so they are proven only where a real
database can reject an invalid row — `FlowOps.Domain.Tests` has no database and cannot exercise
them.

| Rule | Test strategy | Project |
|---|---|---|
| `PERSIST-RULE-01` | Attempt to delete a `Team` referenced by a `Ticket` → FK `RESTRICT` rejects it; delete a `Ticket` → its `ticket_comments`/`ticket_events` cascade-delete. | AT |
| `PERSIST-RULE-02` | Attempt to insert a ticket row with an out-of-range `status`/`priority`/`work_type`/`resolution_code` value via raw SQL (bypassing the domain) → `CHECK` constraint rejects it. This is the specific test CLAUDE.md §15 calls out: "database CHECK constraints actually reject invalid rows (prove the DB is a real guardian)." | AT |
| `PERSIST-RULE-03` | Raw-SQL insert/update a `Closed` ticket with `closed_at IS NULL` → rejected; same for the resolved/resolution equivalent. | AT |
| `PERSIST-RULE-04` | Raw-SQL insert/update a ticket with `sla_due_at <= sla_started_at` → rejected. | AT |
| `PERSIST-RULE-05` | Attempt to insert a duplicate `tickets.reference`, a duplicate `(team_id, name)` category, or a duplicate `(work_type, priority)` SLA configuration row → each rejected by its unique constraint/index. | AT |
| `PERSIST-RULE-06` | Load the same `Ticket` in two separate `DbContext` instances, save the first, then attempt to save the second's stale copy → `DbUpdateConcurrencyException` (the direct test of ADR-0011). | AT |

---

## Coverage summary by rule family

| Family | Rule count | Primary project(s) |
|---|---:|---|
| `TICKET-ENT` | 5 | DT |
| `TICKET-ENUM` | 4 | DT |
| `TICKET-INV` | 10 | DT |
| `TICKET-WF` | 13 | DT |
| `AUTH-RULE` | 9 | DT (policy truth table) + AT/WT (enforcement, privilege guards) |
| `SLA-RULE` | 12 | DT (math/policy) + AT (config persistence, architecture fitness) |
| `ATTN-RULE` | 7 | DT (policy) + AT (prefilter-superset test, §9.3) |
| `AUDIT-RULE` | 6 | DT (shape/co-occurrence) + AT (atomicity, architecture checks) |
| `PERSIST-RULE` | 6 | AT (real Postgres required — these cannot be proven without a database) |

Every rule family in `docs/domain-model.md` has an identified test strategy and project above; no
rule ID is left without one. No new business behavior was introduced to make a rule "more
testable" — where a rule's own text already implies a boundary (a threshold, an inclusive
comparison), that boundary is the test case; nothing beyond it was added.
