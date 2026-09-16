# 0019. Dashboard filter bar, date-range semantics, and server-rendered charts

## Status
Accepted

## Context
Phase 10 gave the dashboard exactly four capped KPIs (Open Work, Overdue, SLA Compliance, Average
Resolution Time) plus a current workload table, deliberately deferring any further visualization
(CLAUDE.md §22's KPI cap was written for that one stat strip, not as a ban on the dashboard ever
growing). Phase 20 adds five further operational sections — ticket volume over time, tickets by
status, workload by team, SLA performance, and resolution time by work type — plus a small filter
bar (date range / team / work type) that narrows them. None of this replaces the four KPIs or their
existing definitions; it is new surface area layered on top of `AnalyticsQueryService`, the same
class Phase 10 already established as analytics' one home.

Three design questions needed a real decision, not an improvised one per section: how a
client-supplied filter can coexist with the organization/role authorization scope without ever
becoming a second authorization mechanism; what date column each new section actually means by "in
range" (they are not all the same question); and how to draw five charts without a JS framework,
a charting library, or breaking with JavaScript disabled.

## Decision

**Filter is data, authorization is not — enforced by ordering, not convention.**
`AnalyticsQueryService.ApplyDashboardScope` is always applied strictly *after*
`ApplyAnalyticsScope` (the existing org-then-role scope from Phase 16, AUTH-RULE-02 "Team
analytics"), never before and never in place of it. A `DashboardFilter.TeamId` naming a team outside the caller's own
scope — wrong organization, or a real team the caller's role cannot see — simply intersects with an
already-narrowed query and yields zero rows. There is no separate "is this id valid" check whose
answer could itself leak whether a team exists; the two states ("no such team" and "a real team you
can't see") are deliberately indistinguishable, the same anti-enumeration shape ADR-0018 already
uses for organization switching. The filter bar's own team dropdown
(`GetFilterTeamOptionsAsync`) is populated only from the caller's own accessible teams, so the
control itself cannot be used to enumerate other teams' names either.

**Three different date semantics, not one, and each is named where it's used — never a single
generic "date range" applied uniformly.**
- **Demand-side (`CreatedAt`)**: ticket volume trend, status breakdown, and SLA performance all
  answer "of the work that arrived in this window, what does it look like" — filtered by
  `CreatedAt`.
- **Outcome-side (`ResolvedAt`)**: resolution time by work type generalizes the existing KPI's
  fixed-90-day `ResolvedAt`-based average to the user-selected range, keeping the exact same
  `ResolvedAt − SlaStartedAt` duration definition — never `CreatedAt`, for the same reason the
  original KPI doesn't use it (a ticket resolved quickly after a long-ago creation should not
  inflate the average).
- **Present-tense (no date filter)**: workload by team, like the existing workload-by-team-and-
  assignee KPI table, is "who is carrying the work right now" — a creation-date window cannot
  answer that question, so `GetTeamWorkloadBreakdownAsync` and the four original KPIs
  (`GetDashboardSummaryAsync`) deliberately ignore `DashboardFilter.RangeDays` entirely and are
  scoped only by team/work-type. This was caught as a real bug during this phase's own query
  review: an early version of the shared `ApplyDashboardScope` helper defaulted to applying the
  `CreatedAt` filter, which would have silently narrowed Open Work/Overdue/current workload by the
  filter bar's date range the moment any filter was passed. The fix removed the default entirely —
  `includeDateRange` has no default value, forcing every call site to state which semantics it
  means — and a regression test
  (`GetDashboardSummaryAsync_OpenWorkAndWorkload_AreUnaffectedByFilterRangeDays`) pins the correct
  behavior.

**SLA performance reports all five real `SlaStatus` values, never a lossy collapse to three.**
`SlaStatus` is `Within, AtRisk, Paused, Breached, Met` — five real, meaningfully different states
(confirmed by reading the enum itself rather than assuming the intuitive "within/at-risk/breached"
split some example text suggested). Met (resolved on time) and Within (still open, on track) are
different facts; Paused is neither good nor bad. `GetSlaBreakdownAsync` classifies using
`SlaPolicy.GetStatus` — the sole existing authority — never a second SLA calculation, over a
already-scoped, already-filtered set of raw SLA columns loaded once, then classified in memory (the
same "server-side prefilter, Domain policy classifies in memory" shape `AttentionQueryService` and
`TicketQueryService` already use for the same reason: `SlaPolicy` needs the current instant and a
live-read risk threshold that cannot be pushed into SQL without duplicating the policy there).

**No charting library, no JavaScript requirement.** The ticket-volume trend is one inline SVG
`<polyline>`, its points computed server-side by a small presentation-only helper
(`DashboardCharts`, following the existing `WorkflowRail`/`TimelinePresentation` static-helper
convention — no domain logic, only geometry for an already-decided value). Every other chart
(status, team workload, SLA breakdown, resolution time) is a plain CSS bar — a labelled row, a
track `<div>`, a filled `<div>` whose width is a server-computed percentage. Every chart has a
`role="img"` with a summarizing `aria-label`, plus a full, always-present `<table>` (behind a
native `<details>` disclosure for the trend chart, inline for the bar charts) carrying the same
data as real markup — not an aria-only duplicate, so it's available to a screen reader, a keyboard
user, or simply someone who prefers numbers, with zero JavaScript.

**Date-bucketing avoids a provider-specific package.** `GetTicketVolumeTrendAsync` groups by
`t.CreatedAt.Date` — a plain, portable EF Core translation — rather than
`EF.Functions.DateTrunc`, which requires a `Npgsql.EntityFrameworkCore.PostgreSQL` package
reference that `FlowOps.Application` does not otherwise carry (ADR-0002's layering keeps
Application on generic EF Core packages, injecting `FlowOpsDbContext` from Infrastructure without
needing the provider's own extension methods). The aggregate query returns at most one row per day
in the selected range (≤180), so bucketing those already-aggregated day-counts into weekly buckets
for longer ranges is a cheap in-memory pass over rows, not raw tickets — consistent with CLAUDE.md
§16's "no loading tickets to aggregate in C#," since what's held in memory here is the *aggregate*,
never ticket rows.

**Filter bar is a plain GET form, not a JS-driven control.** Date range / team / work type are
bound via `[BindProperty(SupportsGet = true)]` (the same convention `AcceptInvitationModel.Token`
already uses), making every filtered view a normal, bookmarkable, shareable URL. An `onchange`
auto-submit is included as pure progressive enhancement; the explicit "Apply" button is the real,
always-working affordance with JavaScript disabled.

## Alternatives considered
- **A second, more general analytics/reporting abstraction** (a query-builder, a generic filter
  object spanning arbitrary fields): rejected — `DashboardFilter` is a closed, three-field record
  with a validating factory, not a general query mechanism, matching CLAUDE.md's "no framework for
  a problem you have one instance of."
- **Collapsing the five `SlaStatus` values into three for the chart**: rejected — see above; it
  would silently invent a second, lossier SLA taxonomy alongside the real one.
- **A charting library (Chart.js, ApexCharts, etc.) via CDN**: rejected outright by CLAUDE.md's
  explicit ban on JS charting frameworks; an inline SVG polyline and CSS bars fully cover this
  phase's five chart shapes (a line and four bar comparisons) without one.
- **Adding an index for the new `CreatedAt`/`ResolvedAt`/`TeamId`/`WorkType`-filtered queries**:
  considered and rejected for now. Every new query is a single round trip already (confirmed by
  inspecting emitted SQL), and this project's existing ticket-table indexes are already narrow and
  purpose-built (e.g. `ix_tickets_open_team_priority` is a partial index only for non-terminal
  tickets). At this project's data scale, a sequential scan bounded by the dashboard's own date
  range is not a measured problem; CLAUDE.md §16's "do not add indexes speculatively" applies
  directly. If a real deployment's ticket volume later makes one of these five queries slow, the
  fix is a targeted, justified migration then — not a preemptive one now.

## Consequences
- No database migration. Every new query reads existing columns (`created_at`, `resolved_at`,
  `team_id`, `work_type`, `status`, the existing SLA columns).
- `AnalyticsQueryService.ApplyDashboardScope`'s `includeDateRange` parameter has no default value —
  a deliberate API choice forcing every call site to state its own date semantics rather than
  silently inheriting one that might be wrong for it (see the caught bug above).
- A new Web-layer presentation helper, `DashboardCharts`, alongside the existing
  `WorkflowRail`/`TimelinePresentation`/`Pagination` static helpers.
- `AnalyticsQueryService.GetDashboardSummaryAsync` gained an optional `DashboardFilter?` parameter
  (default `null`), so every pre-Phase-20 caller keeps compiling and behaving identically.
