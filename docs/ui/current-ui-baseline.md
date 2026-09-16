# FlowOps Current UI Baseline

Captured by inspection only. Every claim below is sourced from the files listed in the
"Files inspected" section of the final report — nothing here is inferred or assumed.

---

## 1. Executive Summary

FlowOps's UI is a deliberately minimal, semantic-HTML-first Razor Pages application: one
layout, one CSS file (401 lines, `wwwroot/flowops.css`), no client-side framework, no JavaScript
file anywhere in `wwwroot`, no Bootstrap despite CLAUDE.md §11.1 describing "locally hosted
Bootstrap 5 + flowops.css" — the repository has moved past that plan and now ships a single
hand-written stylesheet only (per CLAUDE.md §0, the repository is authoritative over the
document). The design is functional, accessible-by-default (real `<table>`s, `<dl>` detail
grids, labelled inputs, a skip link, visible focus rings), and visually restrained to the point
of being nearly unstyled: one accent color, four semantic badge tones, no shadows, 3px border
radius, a single 768px breakpoint. It reads as an engineering prototype that took accessibility
and information architecture seriously but has not yet had a visual design pass — which matches
where the project says it is (Phase 15 done, UI refinement not yet started).

## 2. Current Visual Identity

There is effectively no visual identity beyond "restrained data application." No logo mark, no
brand color beyond a single link/accent blue (`#0b4a8f`), no illustration, no imagery anywhere.
The wordmark "FlowOps" appears as plain bold text in the header brand link and in `<title>`
tags; it does not appear at all on the Login page. Personality, to the extent one exists, comes
entirely from typographic restraint and dense tabular data — closer to an internal ops console
than a branded product.

## 3. Page Inventory

| Route | Page | Purpose | Persona | Primary action | Secondary actions | Nav entry point |
|---|---|---|---|---|---|---|
| `/Account/Login` | Sign in | Authenticate | Anyone | Sign in | — | Reached only when unauthenticated (redirect) |
| `/Account/Logout` | Sign-out fallback | GET-only informational stub; real sign-out is a POST from the header | Any authenticated user | None (POST-only endpoint) | Link back implied | Header "Sign out" button (POST form, not this page) |
| `/Account/AccessDenied` | Access Denied | Authorization failure landing page | Any | None | None | Not linked; reached via `[Authorize]` redirect |
| `/Index` | Dashboard | "What needs my attention" home | All roles | View at-risk ticket / KPI drill-through | View all at-risk work | Header brand link + "Dashboard" nav item |
| `/Tickets/Index` | Work Queue | Browse/paginate visible tickets, optionally pre-filtered | Agent, Manager, Admin, Viewer (read) | Open a ticket | New ticket, At-risk work, clear filter | Header "Work Queue" nav item |
| `/Tickets/AtRisk` | At-Risk work | Ranked list of tickets needing attention | Manager, Agent, Admin | Open a ticket | Pagination | Header "At-Risk" nav item |
| `/Tickets/Create` | New ticket | Submit a new ticket | Agent, Manager, Admin (not Viewer) | Create ticket | Back to work queue | Header "Create Ticket" nav item |
| `/Tickets/Details/{id}` | Ticket detail | View full ticket + take workflow actions + comment + audit history | Role- and relationship-dependent | Context-dependent workflow action (Assign/Start/Resolve/etc.) | Add comment | Linked from queue/at-risk/dashboard rows only — no direct nav entry |
| `/Admin` | Admin | **Placeholder only** — proves the AdminOnly gate exists; no admin functionality is implemented | Admin | None | None | Not linked from the header nav at all |
| `/Error` | Error | Generic unhandled-error page | Any | Back to home | — | Not linked; reached via exception handler |

No dedicated Analytics or Workload page exists — analytics (KPIs, workload table) live inside the
Dashboard (`/Index`) itself, not a separate route. No dedicated Comments/Timeline page — comments
and audit history are both rendered inline at the bottom of Ticket Detail. No Ticket Edit page
exists as a distinct route — editing happens via the workflow-action forms on Ticket Detail.

**Note on Admin:** `Pages/Admin/Index.cshtml` is a literal placeholder ("Admin area placeholder —
user, team, category, and SLA management belong to later phases"). It is not a page worth a design
pass yet — it has no content to redesign.

## 4. Global Layout

Single shared layout, `Pages/_Layout.cshtml`: `<!DOCTYPE html>` → `<head>` with one stylesheet
link (`~/flowops.css`, no CDN, no fonts loaded) → `<body>` containing, in order: a visually-hidden
skip link, a conditional site-wide demo banner (`.empty-state` styled, `role="status"`), a
conditional `<header class="app-header">` (only when authenticated) containing the brand link,
primary nav, and a sign-out form, then `<main id="main">@RenderBody()</main>`. There is **no
footer anywhere in the application**. Page width is capped at `max-width: 72rem` on both the
header bar and `<main>`, centered, with `padding: 1.5rem 1rem 2rem` on `<main>`.

Every content page follows the same shallow structure: `<h1>` page title → zero or more `<h2>`
sections → tables/forms/lists — there are no cards-as-page-sections, no sidebars, no tabs, no
accordions anywhere in the codebase.

## 5. Navigation

Primary nav is a single flat row of four links, identical for every authenticated role
(Dashboard, Work Queue, At-Risk, Create Ticket) — it does not vary by role even though `Create
Ticket` 403s server-side for a Viewer who clicks it (server-side authorization is correctly
enforced; the nav link itself is simply not hidden for a role that cannot use it). `/Admin` has
no nav entry point at all despite being a real route — reachable only by typing the URL. Ticket
Detail has no nav entry point either — only reachable by clicking through from a queue/list row.
There are no breadcrumbs anywhere in the application. Every page provides its own manual
"back" link (e.g., "Back to work queue", "Back to home") as plain inline text rather than a
structural breadcrumb trail. Pagination navigation uses `<nav aria-label="...">` with
`Previous page`/`Next page` text links (no page-number links, no jump-to-page).

## 6. Typography

- **Font family:** `system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif`
  — no custom/web font, no Google Fonts.
- **Base size:** `16px`, line-height `1.5`, applied to `<body>`.
- **Heading hierarchy:** `h1` = `1.5rem` (24px), `h2` = `1.15rem` (~18.4px) with a bottom border
  and bottom padding acting as a section divider — no `h3` global style except the KPI card
  heading, which is styled locally (`0.85rem`, uppercase, `letter-spacing: 0.03em`,
  `font-weight` inherited/normal, muted color) rather than as a generic `h3` rule. There is no
  `h4`+ anywhere in the CSS.
- **Weights used:** `700` (brand, KPI value, attention-list ticket title), `600` (`dt` labels,
  form `<label>`s), otherwise default body weight.
- **Uppercase + letter-spacing** is used exactly once — the KPI card heading — nowhere else.
- No defined type scale beyond the three sizes above; body text, table cells, and captions all
  render at or near the 16px base with `0.85–0.9rem` used ad hoc for secondary/muted text
  (captions, KPI card headings, attention-list reason text, account email in header).

## 7. Color System

All colors are CSS custom properties on `:root` in `flowops.css`, light-mode only
(`html { color-scheme: light }` — there is no dark mode).

| Token | Value | Use |
|---|---|---|
| `--fo-text` | `#1a1d21` | Primary text |
| `--fo-text-muted` | `#565c63` | Secondary/caption/muted text |
| `--fo-bg` | `#ffffff` | Page background |
| `--fo-bg-subtle` | `#f4f5f6` | Header background, button background |
| `--fo-border` | `#cdd2d8` | Default hairline borders |
| `--fo-border-strong` | `#8a9099` | Input borders, `thead` bottom border |
| `--fo-link` / `--fo-focus` | `#0b4a8f` | Links, focus ring, primary button, KPI card link |
| `--fo-neutral-bg/text/border` | `#eceef0` / `#33383e` / `#b7bcc2` | Default badge tone (status, priority, most SLA states) |
| `--fo-info-bg/text/border` | `#e4edf7` / `#0b4a8f` / `#9fbfe0` | Defined, **not observed in use** in any inspected `.cshtml` |
| `--fo-warning-bg/text/border` | `#fbeed6` / `#7a4a05` / `#e6bd7a` | Defined, **not observed in use** in any inspected `.cshtml` |
| `--fo-danger-bg/text/border` | `#f8dfdd` / `#8a2420` / `#e3a29e` | At-risk list left border, at-risk SLA badge, attention-signal badges |

There is exactly **one** accent color in the entire system (`#0b4a8f`, used for links, focus
rings, and the primary button) — no secondary brand color exists. The `info` and `warning` badge
tones are fully defined in CSS but not applied anywhere in the pages inspected — every badge in
Work Queue, At-Risk, and Ticket Detail renders `badge-neutral` regardless of actual priority or
status value, and every SLA/severity badge on the At-Risk page renders `badge-danger`
unconditionally. This is documented further in §18.

## 8. Spacing

A five-step scale, all in `rem`: `--fo-space-1: 0.25rem` through `--fo-space-5: 2rem`. Used
consistently for padding and gaps (header bar padding, `<main>` padding, card padding, table
cell padding `0.5rem`, form field bottom margin `1rem`, button padding `0.5rem 1rem`). There is
no distinct "section spacing" token — `h2`'s own top margin (`--fo-space-4`, 1.5rem) is what
separates page sections. No dedicated table-row height token — row height is a function of
cell padding + line-height only.

## 9. Shape

- **Border radius:** a single value, `--fo-radius: 3px`, applied to KPI cards, badges, buttons,
  inputs, the attention-list items, and empty-state boxes. Nothing uses a larger or "pill" radius.
- **Borders:** `1px solid` is the only border weight used for containers; the attention-list item
  additionally uses a `4px solid` left border in the danger tone as its only accent treatment.
- **Shadows:** none anywhere in the stylesheet — zero `box-shadow` declarations.
- **Cards:** exactly one card pattern (`.kpi-card`: bordered box, no shadow, `1rem` padding) plus
  a visually similar but differently-named `.attention-list li` treatment (bordered box + left
  accent border) and `.action-group form` (bordered box wrapping each workflow-action form on
  Ticket Detail). These three are not the same CSS class, though they look nearly identical.

## 10. Components

Complete inventory, this is the entire set — there is nothing beyond what's listed here:

- **Badges** (`.badge` + one of `-neutral/-info/-warning/-danger`) — status, priority, SLA
  status, attention severity.
- **KPI card** (`.kpi-card`) — dashboard summary tiles only.
- **Attention list item** (`.attention-list li`) — dashboard at-risk preview only.
- **Detail grid** (`dl.detail-grid`) — Ticket Detail's metadata block only.
- **Tables** (plain semantic `<table>`, `.table-responsive` wrapper for horizontal scroll) — Work
  Queue, At-Risk, dashboard workload, Ticket Detail history, Login's demo-persona table.
- **Forms / fields** (`.field`, labelled inputs, `.field-validation-error`,
  `.validation-summary-errors`) — Login, Create Ticket, all Ticket Detail workflow-action forms.
- **Buttons** (`button`/`.btn`, `.btn-primary` variant) — every submit action; no secondary/
  outline/ghost/icon button variants exist.
- **Action group** (`.action-group`) — the row of workflow-action forms on Ticket Detail only.
- **Empty state** (`.empty-state`) — reused for "no data" messages, the demo banner, and the
  demo-credentials box on Login (three different semantic purposes sharing one visual style —
  see §18).
- **Pagination nav** (`<nav aria-label>` + prev/next text links) — Work Queue, At-Risk.
- **Skip link** (`.skip-link`).
- No modals, no toasts/alerts-as-overlay, no dropdown menus, no tabs, no accordions, no tooltips
  beyond the native `title` attribute, no loading spinners/skeletons anywhere (every page is a
  full server-rendered response — there is no async/partial loading state to design for).

## 11. Dashboard Audit

**Route:** `/Index` — **Purpose:** "what needs my attention," the landing page after login.

**Hierarchy (top to bottom):** `<h1>FlowOps</h1>` → "Signed in as {email} ({role})" line → `<h2>
What needs attention</h2>` (the at-risk preview list, or an empty state) → a "View all at-risk
work" link → `<h2>Summary</h2>` (the four-KPI grid) → `<h2>Current workload</h2>` (a plain table).

**KPI presentation:** exactly four cards in a responsive auto-fit grid (`minmax(14rem, 1fr)`,
collapsing to one column under 768px): Open Work, Overdue, SLA Compliance, Average Resolution
Time. Each KPI title is itself the clickable link (uppercase, muted, small); the value is large
and bold underneath. Two of the four KPIs (SLA Compliance and Average Resolution Time) both link
to the same underlying filter (`ResolvedRecently`) despite representing different metrics — see
§18.

**Attention/at-risk presentation:** a bordered list, each item with a red left accent border
(`--fo-danger-border`), showing the ticket reference/title as a link, the top signal's severity
badge + headline, a "(+N more)" count if multiple signals fired, the SLA status, and remaining
time. This is the strongest visual moment on the page — it is genuinely the first thing a reader's
eye lands on, ahead of the KPI grid below it, matching CLAUDE.md §1/§22's intent.

**Workload visualization:** a plain three-column table (Team, Assignee, Open tickets) — no bar
chart, no visual proportion, numbers only. This is a literal reading of the data, not a
"visualization" in any graphical sense.

**Navigation hierarchy:** the page correctly leads with the ranked at-risk list before the KPI
counters (a Phase 11 correction noted in an inline comment), which is the single most important
information-architecture decision on the page and it is right.

**Information density:** low-to-moderate — this is the least dense page in the app. The most
important information (at-risk work) is visually distinguished by the red left border and is
positioned first; it is visually obvious, though it competes with no other bordered element on
the page for attention (nothing else on the dashboard uses a similar accent treatment), so the
signal reads clearly.

## 12. Work Queue Audit

**Route:** `/Tickets/Index`.

**Filter layout:** there is no interactive filter UI at all — no dropdowns, no search box, no
form. The only filtering mechanism is a single closed enum (`TicketQueueFilter`: None, OpenWork,
Overdue, ResolvedRecently) applied via a query-string route value, arrived at exclusively by
clicking a dashboard KPI link. Once on the filtered queue, the only affordance is a text sentence
("Filtered to: Open Work — clear filter") — there is no way to change the filter without
returning to the dashboard or clearing it and reapplying manually via URL.

**Table hierarchy:** nine columns (Reference, Title, Priority, Status, Team, Category, Assignee,
SLA, Created) in one un-collapsed table, wrapped only in a horizontal-scroll container on narrow
viewports — no column is ever hidden or reflowed. Reference is a `<th scope="row">` link;
everything else is plain data.

**Status indicators:** every Status badge renders `badge-neutral` — no distinction between Open/
Assigned/InProgress/Pending/Resolved/Closed by color, only by the text inside an identically
styled gray badge.

**SLA indicators:** rendered the same way — `badge-neutral` regardless of whether the computed
`SlaStatus` is Within, AtRisk, Breached, or Paused — with the remaining/overdue time appended as
plain text after the badge, and the exact due timestamp in a `title` attribute. Severity is
present only in the text, never in the badge color, on this page.

**Pagination:** simple Previous/Next text links inside a labelled `<nav>`; no page-number list, no
"jump to page," no configurable page size shown to the user (page size is fixed server-side).

**Row actions:** none inline — the only action per row is navigating to Ticket Detail via the
reference link. No inline assign/resolve/quick-action affordance exists in the table itself.

**Information density:** high — nine columns of largely equal visual weight, no row striping, no
grouping, no visual separation between priority tiers or teams; a technician scanning this table
has to read every cell, since nothing is pre-sorted or pre-highlighted by urgency (the queue is
not attention-ranked; that ranking lives only on the separate At-Risk page).

## 13. Ticket Detail Audit

**Route:** `/Tickets/Details/{id}`.

**Header:** `<h1>{Reference} — {Title}</h1>` — plain text, no status/priority chip inline with
the title itself (those appear further down in the detail grid).

**Status / Priority / SLA:** all three appear as the first three rows of a 17-row `<dl
class="detail-grid">` (two-column definition list: label left, value right, collapsing to
stacked single-column under 768px). Status and Priority both render `badge-neutral`; SLA status
also renders `badge-neutral` with remaining time as trailing text. SLA due date, SLA target
minutes, and paused minutes are each their own separate `dt`/`dd` row — three distinct rows for
what is conceptually one "SLA" concept.

**Metadata:** Work type, Team, Category, Project (or "None"), Requester, Assignee (or
"Unassigned"), Created, Last updated, Due date (or "Not set") — all in the same flat detail-grid,
no visual grouping/sectioning between "workflow" fields (status/priority), "SLA" fields, and
"ownership" fields (requester/assignee/team) — all 17 rows have equal visual weight in one
undifferentiated list.

**Comments / Timeline:** merged into a single "History" table (heading text literally still says
"History," per an inline comment noting it was kept from Phase 6) — `TicketEvent` audit rows and
`TicketComment` rows interleave, newest first, in one four-column table (When, Type, By, Detail).
Internal-comment visibility is enforced server-side before this list is built (never hidden
client-side).

**Actions:** a flexible-wrapping row of independent bordered forms (`.action-group`), one per
available workflow action (Assign to me, Start work, Unassign, Put on hold, Resume, Resolve,
Close, Reopen), each only rendered when `TicketAccessPolicy`/the current status permits it — no
disabled-but-visible buttons, an action simply does not appear if unavailable. The comment form is
a separate, unrelated form below this group, under its own "Add a comment" heading.

**Information grouping:** weak — Details (flat 17-row list), Description (a paragraph), Actions
(a form row), Add a comment (a form), History (a table) are five sequential, equally-weighted
`<h2>` sections with no visual hierarchy differentiating "read this first" from "reference data."
This is the densest, longest page in the application and currently the one most likely to benefit
from restructuring into visually distinct zones (identity/status header, SLA panel, actions,
activity feed).

## 14. At-Risk / Attention Audit

**Route:** `/Tickets/AtRisk`.

Same nine-ish-column table pattern as Work Queue, with an eighth column, "Why it needs
attention," containing a nested `<ul>` of every fired signal for that ticket, each rendered as a
severity badge + headline. Critically, **every** signal badge on this page renders `badge-danger`
regardless of whether the signal's actual severity is Critical, High, or Medium (§9.1 of
CLAUDE.md defines four severities including Medium for `Aging`/`Stalled`/`Churn`/`Reopened`) — so
a Critical `SlaBreached` signal and a Medium `Churn` signal render in an identical red badge, with
severity distinguishable only by reading the word inside it. The SLA column is likewise always
`badge-danger` here. This is the single clearest instance in the app of a semantic-tone system
that exists in CSS (`info`, `warning`, `danger`) being collapsed down to one tone (`danger`) in
markup, on the one page whose entire purpose is severity triage.

## 15. Analytics Audit

There is no dedicated Analytics page/route. All analytics content (KPI grid, workload table) is
folded into the Dashboard (§11 above) — this section is documented separately per the requested
outline structure, but there is nothing additional to audit beyond §11: no trend chart, no
resolution-time-over-time visualization, no per-category or per-project breakdown anywhere in the
UI, despite `AnalyticsQueryService` existing as an application-layer concept. If deeper analytics
exist server-side, they are not currently surfaced in any Razor page found in this inspection.

## 16. Login Audit

**Route:** `/Account/Login`. The plainest page in the app: `<h1>Sign in</h1>`, a two-field form
(Email, Password) with a primary-styled submit button, and, only when demo mode is enabled, a
second section below showing the four demo personas in a table with their email, role, and what
they demonstrate, plus the shared demo password in a `<code>` element. There is no branding, no
illustration, no "About FlowOps" copy, no link to any marketing/portfolio context — a person
landing here cold has no visual cue this is a portfolio piece until they see the demo-credentials
table (and even that is copy-driven, not visually distinct from a plain content section beyond
the reused `.empty-state` border treatment).

## 17. Responsive Audit

There is exactly **one** media query in the entire stylesheet: `@media (max-width: 768px)`. At
that breakpoint: the header bar switches from a horizontal flex row to a column (brand stacked
above nav, nav left-aligned instead of pushed right); the KPI grid and the ticket-detail
`dl.detail-grid` both collapse to a single column. **Nothing else changes.** Specifically:

- **Tables** (Work Queue's 9 columns, At-Risk's 8, dashboard workload's 3, history's 4) never
  reflow, stack, or hide columns at any width — they rely solely on `.table-responsive`'s
  `overflow-x: auto` for a horizontal scrollbar. On a phone-width viewport, the 9-column Work
  Queue table is very likely to require horizontal scrolling to read every column, which works but
  is not an optimized mobile table pattern.
- **Forms** are already single-column by default (`.field { max-width: 32rem }`), so they do not
  need a breakpoint-specific rule and behave acceptably narrow.
- **Buttons** have no responsive sizing rule; they wrap naturally via `.action-group`'s
  `flex-wrap: wrap`.
- **Typography** has no responsive scaling — `h1`/`h2`/base font sizes are fixed `rem` values at
  every viewport width (acceptable, since `rem` still respects user browser zoom/text-size
  settings).
- There is no dedicated "mobile navigation" pattern (no hamburger menu) — the four-item nav simply
  wraps onto a second line if needed (`flex-wrap: wrap` on `.app-nav`), which is a reasonable,
  low-complexity choice for only four links.

**Likely-to-break flag:** the At-Risk table's "Why it needs attention" column contains a nested
`<ul>` of variable-length signal text inside one `<td>` of an already-wide table — on a
horizontally-scrolled narrow viewport this is the column most likely to force awkward scrolling
or an unreadably narrow cell if a browser tries to auto-size columns.

## 18. Accessibility Audit

Strong baseline, consistent with CLAUDE.md §22's explicit requirements, verified directly against
markup (not assumed from the contract):

- **Semantic HTML:** real `<table>` elements throughout, with `<th scope="col">` on every header
  row and `<th scope="row">` on the row-identifying cell (ticket reference) in every ticket table.
  `<dl>`/`<dt>`/`<dd>` used correctly for the Ticket Detail metadata grid.
- **Heading hierarchy:** each page has exactly one `<h1>`, followed by `<h2>` section headers with
  no level-skipping observed in any inspected page.
- **Labels:** every `<input>`/`<select>`/`<textarea>` in Login, Create Ticket, and every Ticket
  Detail action form uses `asp-for` with an explicit `<label>` immediately preceding it —
  including the internal-comment checkbox.
- **ARIA:** validation messages use `aria-describedby` linking the input to its
  `field-validation-error` span (Login, Create, all workflow-action forms with input); the demo
  banner uses `role="status"`; the validation summary uses `role="alert"`; pagination `<nav>`s
  carry `aria-label`.
- **Keyboard/focus:** `a:focus-visible, button:focus-visible, input:focus-visible,
  select:focus-visible, textarea:focus-visible` all get a `3px solid` focus outline with offset —
  never removed, never `outline: none` anywhere in the stylesheet. A skip link
  (`.skip-link`) is present, visually hidden until focused.
- **Contrast:** all text/background pairs in the token table (§7) are dark-on-light with
  substantial contrast margins by inspection (e.g., `#1a1d21` on `#ffffff`, badge text colors are
  all dark saturated tones on light tints) — no automated contrast measurement was run in this
  inspection; a proper WCAG AA numeric check is a documented gap, not a pass/fail claim made here.
- **Form error handling:** every validation error is both visually shown (`.field-validation-error`,
  red text) and programmatically associated via `aria-describedby`; the page-level
  `validation-summary-errors` uses `role="alert"`.
- **Table accessibility:** correct scope attributes throughout, as above; captions
  (`<caption>`) are present on every data table describing what it shows and, where paginated, the
  current page state in text.
- **Status communication:** status/priority/SLA/severity are **always** paired with text inside
  the badge (never a bare color swatch) — this specific CLAUDE.md §22 requirement is met
  everywhere, even though (per §12/§14 above) the *severity distinction between values* is
  frequently lost by all values sharing one badge color.
- **Relative time / absolute time:** every `<time>` element carries both a machine-readable
  `datetime` (ISO 8601) and a human `title` attribute with the absolute UTC timestamp — consistent
  across Work Queue, At-Risk, and Ticket Detail.
- **Zoom/text sizing:** no fixed pixel widths block text reflow; base sizing is `rem`/relative, so
  browser text-size and zoom should scale normally (not independently verified with an actual
  browser in this inspection — Docker was unavailable, see §22).

No accessibility violation was found in the markup inspected. The most notable *residual* concern
is not markup-level but design-level: relying on badge text alone to distinguish six status
values, four priority values, and four SLA states — all sharing one visual badge style — puts a
heavier reading burden on a low-vision or fast-scanning user than color-differentiated (but still
text-paired) badges would.

## 19. Visual Consistency Issues

Documented, not fixed, per instructions:

1. **Badge color is not actually used to distinguish severity/status/priority anywhere except the
   dashboard's at-risk preview.** Work Queue and Ticket Detail render every Status/Priority/SLA
   badge as `badge-neutral`; At-Risk renders every SLA/signal badge as `badge-danger` regardless of
   real severity. The `info` and `warning` tones defined in CSS are never applied. This is the
   single largest inconsistency in the app relative to CLAUDE.md §22's own stated intent ("urgency
   conveyed by consistent semantic tokens").
2. **Three near-identical bordered-box patterns** (`.kpi-card`, `.attention-list li`,
   `.action-group form`) share the same border/radius/padding language but are three separate,
   unconsolidated CSS rules rather than one reusable card primitive.
2b. **`.empty-state` is reused for three unrelated purposes** — genuine "no data" messaging (e.g.
   "No work is at risk right now"), the persistent site-wide demo-mode banner, and the
   demo-credentials disclosure box on Login — all sharing one dashed-border visual treatment
   despite being semantically different (an absence, a persistent site status, and a content
   section).
3. **Two dashboard KPIs link to the same filtered view.** "SLA Compliance" and "Average Resolution
   Time" both route to `Tickets/Index?filter=ResolvedRecently` — clicking either lands on an
   identical queue with no visual distinction of which KPI's data it's meant to represent.
4. **The Work Queue and At-Risk pages duplicate almost the entire same table markup** (same eight
   shared columns, same badge classes, same time formatting) as two independent `.cshtml` files
   rather than a shared partial/component — a maintenance-consistency risk more than a visual one,
   but any future visual change to "how a ticket row looks" has two places to edit today.
5. **Primary-action button styling is inconsistent in scope**: `Create Ticket`'s submit uses
   `btn-primary`; every Ticket Detail workflow-action button (Assign, Resolve, Close, etc.) and
   the comment-submit button use the plain unstyled `button`/`.btn` default — so the page with the
   most consequential actions in the app (changing a ticket's real status) has no visual
   "primary action" distinction between, say, "Resolve" and "Unassign."
6. **Project selection is a raw numeric input** (`<input type="number">`) on Create Ticket, while
   every other relational field (Category) is a proper labelled `<select>` — an inconsistency in
   how the same kind of "pick a related record" interaction is presented to the user.
7. **No footer anywhere** — not a strict inconsistency, but notably absent from a layout that
   otherwise treats header chrome carefully (skip link, demo banner, nav); there's no place for a
   version/build identifier, portfolio link, or copyright line.
8. **Heading text drift**: the Ticket Detail "History" section heading was deliberately kept from
   an earlier phase per its own inline comment, despite the underlying feature now being a merged
   audit-event + comment timeline — the heading text no longer fully describes what's shown.

## 20. UX Strengths

- The dashboard correctly leads with ranked at-risk work before KPI vanity counters — the single
  most important information-architecture decision in the app, and it's right (§11).
- Every KPI is a link straight into the exact filtered queue it summarizes — no dead-end metrics.
- Server-rendered, no-JS-required workflow actions on Ticket Detail appear/disappear based on
  real authorization state, never a disabled-but-visible button implying a false possibility.
- Genuinely strong accessibility fundamentals baked in from the start rather than retrofitted:
  semantic tables, universal labels, consistent `aria-describedby`, a real skip link, focus rings
  never removed (§17).
- Absolute timestamps are never lost — every relative/computed time carries the exact UTC instant
  in a `title` attribute, satisfying a specific, easy-to-skip CLAUDE.md requirement.
- Empty states are worded usefully ("No work is at risk right now") rather than left blank.
- The demo-credentials disclosure on Login is transparent and copy-pasteable, a real portfolio
  courtesy.
- The whole system is small enough (one CSS file, one layout, ~10 pages) that a visual redesign
  has very little surface area to cover — this is a genuine structural strength for the next
  phase, not a weakness being described charitably.

## 21. UX Weaknesses

- Severity/status/priority is overwhelmingly conveyed by text alone in practice, despite a color
  system existing to reinforce it (§19.1) — the visual system currently underperforms its own
  design tokens.
- Work Queue offers no interactive filtering, sorting, or search — only a single closed enum
  reachable exclusively from the dashboard.
- Ticket Detail's 17-row flat metadata list has no visual grouping between "what state is this
  ticket in," "what's the SLA situation," and "who owns it" — a reader must scan the whole list
  serially.
- No inline row actions anywhere in list views — every action requires a full navigation to Ticket
  Detail first, even a single-click one like "Assign to me."
- Nine- and eight-column tables have no responsive collapse strategy beyond horizontal scroll.
- No visual distinction of primary vs. secondary actions on the page where it matters most
  (Ticket Detail's workflow buttons).

## 22. Highest-Value UI Improvements

Ranked by design/UX impact only — no implementation detail, per instructions.

**P0 — Critical**
- Make the existing badge color system (`info`/`warning`/`danger`/`neutral`) actually reflect
  status/priority/SLA/severity distinctions wherever a badge appears, instead of defaulting to one
  tone per page. This is the single highest-leverage change: the tokens and the accessibility
  pattern (text + color) already exist; they are simply not connected to the real data everywhere.
- Establish a clear visual hierarchy on Ticket Detail so identity/status, SLA state, ownership,
  and actions read as distinct zones rather than one long flat list.

**P1 — High-value**
- Give the Work Queue and At-Risk tables a design treatment for information scanning at their
  current density (row-level visual weight variation by urgency, not just cell text) — most
  valuable if paired with the P0 badge fix above rather than instead of it.
- Establish a single, reusable "card" visual language to replace the three near-identical but
  separately defined bordered-box patterns (§19.2), so future components inherit one consistent
  treatment.
- Design a resolved visual/interaction difference between primary and secondary actions,
  especially on Ticket Detail's action row.
- Decide what a mobile-width ticket table should actually look like (a reflowed card-per-row
  pattern is one option among several) rather than relying on horizontal scroll as the only
  strategy.

**P2 — Nice-to-have**
- A minimal visual identity pass for Login (something to make it recognizably "FlowOps" rather
  than an unbranded form) — low urgency since Login is not a page a returning user lingers on.
- A footer, if there's ever content that belongs in one (build/version info, portfolio link).
- Resolve the two-KPIs-one-destination overlap on the dashboard (§19.3) — minor, since both
  destinations are individually correct, just not distinguished from each other.
- Reconsider whether Analytics deserves its own page as more metrics are added — not urgent while
  the dashboard's four-KPI budget (§22 of CLAUDE.md) is still respected.

## 23. Design Questions for Next Phase

1. Should severity/urgency color differentiation (the P0 finding above) extend to a fuller
   priority-tier palette (e.g., visually distinct Critical vs. High vs. Medium vs. Low), or should
   it stay to the existing four semantic tones (neutral/info/warning/danger) mapped more
   consistently onto existing states?
2. Is a single-page "everything in one flat list" pattern (as used throughout) the intended
   long-term density model, or should Ticket Detail specifically move toward zoned/sectioned
   layout (e.g., a header band with status/priority/SLA at a glance, separate from the full
   metadata list)?
3. Should Work Queue gain interactive filtering/sorting/search, and if so, does that change the
   page's current "server round-trip only" interaction model (CLAUDE.md §11.1 requires JS-off
   compatibility for every page — any new filter UI needs a non-JS fallback)?
4. Is a light "FlowOps" visual identity (a wordmark treatment, one additional accent color) in
   scope for this phase, or does the "operational tool, not a branded product" positioning mean
   visual identity work should stay minimal by design?
5. Does the mobile table strategy get solved generically (one pattern reused across Work Queue,
   At-Risk, and History) or per-page, given each table's column set and purpose differ?
6. Should the redesign introduce a light card/section visual language broadly (dashboard, ticket
   detail, forms), or keep the current mostly-flat, table/list-first aesthetic and confine new
   visual treatment to the specific weak spots identified above?

---

*Written 2026-09-12 as a read-only baseline capture. No application file was modified to produce
this document.*
