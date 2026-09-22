# 0030. Sprint Archive, Cancellation, and Board Drag-and-Drop

## Status
Accepted (refines ADR-0029)

## Context
ADR-0029 stored sprint membership as one nullable `Ticket.SprintId`. That is the right shape for
*current planning*, but it overwrites history: once an unfinished ticket is carried into the next
sprint, nothing says it was ever in the previous one, so a completed sprint's archive would silently
change. Browser QA also showed we need a way to abandon a sprint that never started, a navigable
sprint history, and — as a product request — mouse drag-and-drop on the board, without weakening the
rule that the ticket workflow is the only workflow.

## Decision
- **Historical membership: a completion snapshot, not a redesign.** `sprints` stays as is;
  `tickets.sprint_id` stays the single *current* planning membership. New append-only table
  `sprint_ticket_snapshots (sprint_id, ticket_id, status_at_completion, was_done)` is written in the
  same save as `Sprint.Complete`, one row per member ticket, and never updated or deleted. A completed
  sprint's archive (counts, finished / not-finished lists) reads the snapshot, so it stays true after
  carry-forward; the ticket's *current* location is shown beside it. A sprint completed before this
  table existed has no snapshot and falls back to live membership (the page says so).
- **Cancellation, never deletion.** `Planned → Cancelled` (`Sprint.Cancel`, `SPRINT-INV-07`); an
  Active sprint is *completed*, a Completed or Cancelled one is read-only history. Cancelled sprints
  accept no tickets and are not move targets. Cancelling releases the sprint's non-finished tickets
  (they were only *selected* for a sprint that never ran) with an audited `SprintChanged` per
  ticket. Permanent deletion is not offered at all: sprint rows are history.
- **Carry-forward is an explicit action.** "Move unfinished tickets to the current sprint" (and a
  per-ticket "Move to current sprint") on a completed sprint. Only tickets still in that sprint and
  not Resolved/Closed, and only those the caller may plan, are moved; each goes through
  `Ticket.MoveToSprint` (status, assignee, SLA, dates untouched), all-or-nothing. Nothing is ever
  moved automatically.
- **Sprints page and Sprint detail.** Project → Overview / Board / Tickets / Sprints. The Overview
  shows the three most recent sprints and a link to all; each sprint opens a detail view that opens
  tickets on the existing Ticket Detail page.
- **Board drag-and-drop, with the workflow untouched.** A drag is *planned* by the pure
  `BoardMovePlanner` into a list of operations that already exist as explicit actions (pull / return
  to backlog, assign to me, start work, resume, put on hold, resolve, reopen). `TicketService.
  MoveOnBoardAsync` executes them inside one load-authorize-mutate-save: each step is authorized by
  the same `TicketAccessPolicy` call and validated by the same `Ticket` method as its explicit action;
  nothing sets a status. A drag the workflow does not allow is rejected with a message and changes
  nothing. Hold/resolve/reopen ask for the same reason/notes the ticket forms require, in a native
  `<dialog>`. The one new domain operation is `Ticket.ReturnToSprintBacklog` (the planning inverse of
  pulling, `TICKET-INV-13`). Clearing the backlog flag when work starts is done only for callers who
  may plan, so an Agent starting work is not blocked by planning authority.
- **JavaScript, narrowly (CLAUDE.md §11.1).** `board-dnd.js` (native HTML5 drag events, ~150 lines,
  no library) and `row-menu.js` (one open menu at a time, outside click, Escape, focus-out — a native
  `<details>` cannot do these). Both are progressive enhancement: every drag has a menu action, the
  drop submits the page's real form and the server re-renders, and nothing is applied optimistically.
  Touch/keyboard users use the menus. The board is `Backlog | Open | In progress | Pending | Done`;
  Assigned is shown inside Open (the card shows its assignee).

## Alternatives considered
- **Derive history from `SprintChanged` events** — rejected: reconstructing a sprint's roster and
  each ticket's status at completion from an event stream is fragile, slow, and unauditable at a
  glance; the snapshot is one small, obviously-correct table.
- **Replace `sprint_id` with a membership join table** — rejected again (see ADR-0029): it reopens
  "a ticket in two sprints" for no gain once history has its own table.
- **Auto carry-forward on complete** — rejected: it hides a planning decision and rewrites the story
  of the sprint.
- **Delete a cancelled/empty sprint** — rejected: sprint history is project history; cancel is enough.
- **Optimistic drag UI** — rejected: it can diverge from the server's authorization and workflow.
- **A drag-and-drop library / SPA board** — rejected: native events suffice and the server stays
  authoritative.

## Consequences
- One more table and two columns' worth of persistence (`sprint_ticket_snapshots`,
  `sprints.cancelled_at`), all additive; existing sprints/tickets stay valid.
- Snapshots capture *all* members at completion; visibility is applied only when read, so a caller's
  counts never include tickets they cannot open.
- A ticket carried forward appears in two sprints' histories by design (snapshot of the first, live
  membership of the second) — that is the point.
- Board moves from Backlog to In progress compose existing steps and therefore write several audit
  events (assign, start work, sprint-backlog change) rather than one new event type.
- Deferred: bulk selection/actions, drag on touch devices, keyboard drag, and REST endpoints (no
  consumer).

- Phase 28B wording note: the board's first column is *labelled* "Planned" (in the sprint, not yet moved onto the board); the internal column key, flag, and events keep the `Backlog` names. "Carried from Sprint N" is derived from the completion snapshots, not stored separately.
