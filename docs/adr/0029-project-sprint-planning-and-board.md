# 0029. Project Sprint Planning and Board

## Status
Accepted

## Context
FlowOps deliberately started as "not a Jira clone": CLAUDE.md §1 excluded sprints and backlog, and
banned the words. The product owner has now decided that project work needs a lightweight weekly
planning workflow — a project board for the current sprint and a dense ticket list for planning —
because it materially improves the Project experience. The engineering constraints (modular
monolith, layering, one ticket workflow, server-side authorization, tenant isolation) are unchanged;
only the old product-scope restriction is superseded.

## Decision
Add lightweight project sprints and sprint membership while leaving the ticket workflow untouched.

- **`Sprint`** is a Project-owned entity in `FlowOps.Domain.Planning` (name, start/end date,
  `Planned → Active → Completed`), reference-data style like `Project`/`Category` — *not* a second
  aggregate root; `Ticket` stays the only aggregate (`TICKET-ENT-01`).
- **Membership lives on the ticket** (`Ticket.SprintId`, nullable) and is mutated only through
  `Ticket.MoveToSprint`, so one-sprint-per-ticket is true by construction, a move is one atomic
  ticket save with one `SprintChanged` audit event (`TICKET-INV-09`), and it inherits the ticket's
  org-scoped load, authorization, and `xmin` concurrency. Moving never changes status, assignee,
  SLA, or dates.
- **"Sprint backlog" is a planning flag, not a status** (`Ticket.SprintBacklog`): set when a ticket
  enters a sprint, cleared by "pull onto board". Board columns (Backlog, Open [Open + Assigned], In progress,
  Pending, Done) are *derived* from the existing `Status` plus that flag. There is no `Backlog`
  status and no parallel state machine.
- **Current sprint = the one Active sprint of a project.** At most one, enforced by a partial unique
  index. Starting/completing are explicit user actions; no scheduler, hosted service, or job
  (ADR-0006).
- **Completing a sprint keeps membership.** Unfinished tickets stay attached as history and are moved
  on only by an explicit move; a completed sprint accepts no new tickets, and finished
  (Resolved/Closed) tickets cannot be moved (`TICKET-INV-12`, same rationale as `TICKET-INV-08`).
- **Authorization reuses existing tiers.** Viewing needs organization membership plus the ordinary
  ticket visibility scope. Sprint create/start/complete: Admin or Manager
  (`PlanningAccessPolicy`). Planning a ticket: Admin or Manager of its team
  (`TicketAccessPolicy.CanPlan`). Status changes from the planning pages call the existing ticket
  transitions. Everything is re-checked server-side; a cross-organization sprint/project/ticket id is
  refused like a missing one.
- **Audit:** ticket membership changes are `TicketEvent`s; sprint lifecycle is recorded on the sprint
  row and structured logs (`TicketEvent` stays per-ticket business changes only).
- **UI:** Razor Pages, no JavaScript, no drag-and-drop — every move is an explicit action in a
  row/card menu. Views: Projects list, and per project Overview / Board / Tickets.
- **Date model:** sprint dates are UTC calendar dates (`date`), inclusive, consistent with
  `TICKET-INV-10`; no timezone architecture. Sprint dates are separate from a ticket's planned start
  and due date, its actual workflow timestamps, and its SLA — none of these is derived from another.

## Alternatives considered
- **No sprint planning** — rejected: the product decision is explicit.
- **Planning state only on Ticket (no Sprint entity)** — rejected: no place for dates, lifecycle, the
  one-active rule, or history of a completed sprint.
- **A `SprintTicket` join table** — rejected: it would allow a ticket in several sprints (needing a
  uniqueness constraint to get back to what a nullable column already is) and split one ticket change
  across two rows; a nullable FK is the smallest correct model. History of moves is in ticket events.
- **A `Backlog` ticket status** — rejected: a second workflow state machine that would drift from
  `TICKET-WF-*`.
- **Sprint as its own aggregate that owns membership** — rejected: two aggregates would have to
  change in one transaction to move a ticket.
- **A full Jira/Scrum subsystem** (epics, points, velocity, burndown, ceremonies) — rejected as scope.

## Consequences
- New persistence: `sprints` table, `tickets.sprint_id`/`sprint_backlog`, a 17th event type. All
  additive; existing tickets simply have no sprint.
- Sprint-to-ticket-project consistency (the sprint belongs to the ticket's project) is enforced in
  `TicketService.MoveToSprintAsync`, not by a composite foreign key — accepted for simplicity.
- Date overlap between planned/active sprints is an application check; a concurrent overlapping
  create is an accepted low-impact race (the one-active-sprint invariant is the DB-enforced one).
- History of *where a ticket was* over time is the `SprintChanged` event trail; there is no
  point-in-time membership table.
- Deferred, deliberately: bulk selection/actions, drag-and-drop, changing priority from the planning
  list (no existing service method), automatic carry-over of unfinished tickets, and REST API
  endpoints (no consumer exists).
- Supersedes only the product-scope statements in CLAUDE.md §1 and the vocabulary ban in §1.1 and the
  `flowops-ui-ux` skill; all engineering constraints stand.

> Refined by ADR-0030 (completion snapshots for historical membership, sprint cancellation, carry-forward, board drag-and-drop). Where they differ, ADR-0030 applies.
