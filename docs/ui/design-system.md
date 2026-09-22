# FlowOps Design System

**Status:** Applied app-wide (Phase 24A) — every authenticated page, Login/Register/Accept
Invitation/Pending Approval, and both error surfaces (Access Denied, Error) share the one system
described here. Earlier drafts of this document listed Create Ticket/Admin/Error as "not yet
applied" and described a top nav bar with no sidebar — both were true once and are corrected below;
if you find another page/pattern this document doesn't match, that's drift to fix, not a second
system to document.

FlowOps is an IT/service-desk operations platform (ASP.NET Core Razor Pages). This document is the
authoritative description of how it looks and why. It is written to be pasted into a fresh context:
everything needed to implement or critique the system is here, including the reasoning, because the
reasoning is what stops the design drifting back toward a generic dashboard.

**The interface has two themes over one token set: Dark (the baseline) and Light.** A user chooses
System, Light or Dark in Profile & Settings (see "Themes and appearance" at the end). Everything below
describes the structure and the Dark values; Light changes only token values, never structure.

---

## 1. Principles

Five rules that settle arguments. A proposed change violating one of these is wrong unless the rule
is being deliberately retired.

1. **Teal means resolution — nothing else.** `#5FD3C4` is the brand colour and carries exactly one
   product meaning: work that reached its end. It appears on the brand mark, the active nav item,
   the single primary action, and the rail's resolved stop. Spending it decoratively is what made
   earlier drafts read as generic.
2. **Colour only where it changes behaviour.** Critical is red and High is amber because they
   redirect attention. Medium and Low are neutral grey. Every colour spent on something that
   doesn't require a decision dilutes the ones that do.
3. **Not everything is a card.** Border, fill and radius each say "separate object." Default to
   hairline dividers and negative space. A container earns a border only when it is genuinely a
   distinct object.
4. **The rail travels with the ticket.** Wherever a ticket appears — queue row, attention row,
   detail header — its position in the workflow is drawn the same way at the same scale. Learned
   once, read everywhere.
5. **Colour is never the only signal.** Every status, priority and SLA state is accompanied by its
   word. Shape and position reinforce; they never substitute. (CLAUDE.md §22.)

---

## 2. Colour

### Ground and lines

| Token | Hex | Role |
|---|---|---|
| `--fo-bg` | `#081012` | Application ground. Every page sits on this. |
| `--fo-surface` | `#0D1719` | Nav bar, table bodies. One step up, barely. |
| `--fo-surface-2` | `#132124` | Inputs, table heads, avatar fill. |
| `--fo-line-row` | `#1A2729` | Row dividers **only** — the quietest line. Keeps dense tables from looking ruled. |
| `--fo-line` | `#243638` | Structural edges: section rules, container borders, rail segments ahead of the ticket. |
| `--fo-line-hi` | `#34494B` | Rail geometry, ghost-button borders, severity bars for neutral ranks. |

### Text

Primary text is deliberately **off pure white**. Light text on a dark ground optically thickens
(irradiation); `#FFFFFF` at body size blooms and tires the eye over an eight-hour shift.

| Token | Hex | Role |
|---|---|---|
| `--fo-text-hi` | `#F6FAFA` | Headings, figures, ticket titles. Emphasis only. |
| `--fo-text` | `#E9EFEF` | Default body and row text. |
| `--fo-text-2` | `#A7B5B7` | Supporting detail, descriptions, secondary values. |
| `--fo-text-3` | `#718285` | Labels, timestamps, "Unassigned", de-emphasised state. |

### Identity

| Token | Hex | Role |
|---|---|---|
| `--fo-teal` | `#5FD3C4` | Brand, active nav, primary action, **resolved** rail stop. Nothing else. |
| `--fo-teal-dim` | `#0F766E` | The mark's middle node. Quiet teal where full strength would shout. |
| `--fo-teal-ink` | `#06181A` | Text on a filled teal surface (`.btn-primary`'s own label colour). |

### Operational semantics

Independent of the brand. These four never carry identity meaning, and teal never carries
operational meaning — the separation is the point.

| Token | Hex | Role |
|---|---|---|
| `--fo-ok` | `#4CC38A` | SLA met, favourable trend, within target. |
| `--fo-info` | `#63A7E8` | In-flight and healthy. Current rail stop under no time pressure; SLA state "Paused." |
| `--fo-warn` | `#E8B45C` | At risk. SLA approaching breach; High severity; `.btn-caution`'s outline. |
| `--fo-danger` | `#E56B6F` | Breached, overdue; Critical severity; `.btn-danger`'s outline. |

One additional restrained hue, `--fo-violet` (`#8D85C9`), is reserved for the Viewer role badge
alone — a muted slate-violet, dim enough not to compete with teal as a second identity colour.

---

## 3. Typography

**Geist** for everything read as language. **Geist Mono** for everything read as a code or a clock
value. They are siblings, so a reference sitting beside a title shares one skeleton.

Both are **self-hosted** as variable WOFF2 files (`wwwroot/fonts/`) — CLAUDE.md §11.1 admits no CDN
of any kind, including a font host, so `@font-face` points at the app's own static files:

```css
@font-face {
  font-family: "Geist";
  font-style: normal;
  font-weight: 400 600;
  font-display: swap;
  src: url("/fonts/geist-latin.woff2") format("woff2");
}
@font-face {
  font-family: "Geist Mono";
  font-style: normal;
  font-weight: 400 500;
  font-display: swap;
  src: url("/fonts/geist-mono-latin.woff2") format("woff2");
}
```

**Three weights only:** 400 body, 500 emphasis, 600 for the wordmark. Nothing heavier — weight
blooms on dark grounds.

### The mono rule

Mono is for **identifiers and time**: `FO-004821`, `14:30`, `02:14`. It is **not** for quantities.
Figures like `247` or `94.7%` are set in Geist with `font-variant-numeric: tabular-nums`, which is
smoother at display size and still column-aligns.

### Scale

| Size | Weight | Tracking | Line-height | Use |
|---|---|---|---|---|
| 26px | 500 | −0.025em | 1.2 | Page title |
| 30px | 500 | −0.03em | 1 | Figures (tabular) |
| 14.5px | 400 | −0.006em | — | Row title (ticket subject) |
| 14px | 400 | — | 1.65 | Body copy, ticket descriptions |
| 13px | 500 | −0.005em | — | Severity value |
| 13.5px | 400 | — | — | Nav links (500 when active) |
| 12.5px | 400 | — | — | Secondary metadata, assignee, team |
| 10.5px | 500 | +0.09em | — | Section label, UPPERCASE |
| Mono 12.5px | 400 | — | — | References, clock values (tabular) |
| 11px | 500 | +0.06em | — | Chip text, UPPERCASE |

**Uppercase is reserved for labels.** A data value is never set in caps — that was the tell that
made severity read as a field name rather than a value.

---

## 4. Spacing and layout

One scale: **4, 8, 12, 16, 24, 32, 48**. 8/16/24 do most of the work; 32 separates page regions;
48 separates columns on Ticket Detail.

| Step | Used for |
|---|---|
| 4px | Icon gaps, mark internals |
| 8px | Label-to-value, chip gaps, severity mark gap |
| 12px | Label-to-block, timeline gutter |
| 16px | Column gaps inside a row |
| 24px | Block separation, stat-strip padding |
| 32px | Page padding, region separation, nav gap |
| 48px | Ticket Detail column gap and side padding |

| Element | Value | Note |
|---|---|---|
| Nav height | 56px | Surface fill, 1px bottom line |
| Page padding | 32px | 48px horizontal on Ticket Detail |
| Row padding | 13px vertical | Divider is `--fo-line-row` |
| Table head | 12px bottom padding | Then a `--fo-line` rule |
| Sidebar width | 232px | Fixed, persistent on desktop — see §8's Navigation entry |
| Content width | 1680px max | Left-aligned beside the sidebar, not centred — a narrower centred column left a dead gutter beside the sidebar on wide viewports; the cap only keeps line length sane on very wide monitors |

---

## 5. Shape

| Property | Value | Applies to |
|---|---|---|
| Radius | 3px | Chips |
| Radius | 4px | Containers, buttons, inputs |
| Radius | 50% | Rail stops, avatar |
| Border | 1px | All containers and dividers |
| Rail stroke | 1.5px | Connectors and ring stops |
| Shadows | none | No elevation anywhere |
| Glow | `0 0 0 3–5px` at 15–18% alpha | **Only** the rail's current stop. This is a signal, not elevation. |

---

## 6. The rail — signature component

Six stops matching the workflow state machine exactly: **Open · Assigned · In Progress · Pending ·
Resolved · Closed**. Drawn at two sizes and never any other.

- **Compact** (rows): passed/ahead stops 6px, current stop 14px, connectors 1.5px. Fixed 116px column.
- **Expanded** (Ticket Detail): passed/ahead stops 9px, current stop 24px with a 2px ground-coloured
  ring so it reads as lifted off the line.

### Stop states

| State | Rendering | Rule |
|---|---|---|
| Passed | 6px solid `--fo-line-hi` | Connector behind it is also `--fo-line-hi` |
| Ahead | 6px ring, 1.5px `--fo-line-hi`, no fill | Connector is the dimmer `--fo-line` |
| Current, healthy | `conic-gradient(--fo-info X%, #1B2A31 0)` | Conic fill = share of SLA target consumed |
| Current, at risk | `conic-gradient(--fo-warn 85%, #22302E 0)` + glow | Fill shows how close to breach |
| Current, breached | Solid `--fo-danger` + glow | Past target, so no remainder to show |
| Current, paused | `repeating-linear-gradient(45deg,#718285,#718285 2px,#22302E 2px,#22302E 4px)` | Pending stops the SLA clock — no semantic colour, no glow |
| Resolved | Solid `--fo-teal` | The only place teal appears in data |

**The rail is never the only status signal.** A status word sits beside it in queue rows and beneath
it on Ticket Detail.

---

## 7. Severity

A 2px vertical stroke whose **height encodes rank**, plus the word in **sentence case**. The rail
owns the horizontal axis (progress through time), so magnitude takes the vertical one. Height makes
rank legible without decoding colour — amber versus blue tells you nothing about order.

Bar: 2px wide, 1px radius, 8px gap to the word. Word: 13px / 500 / −0.005em.

| Rank | Bar height | Bar colour | Word colour | Reasoning |
|---|---|---|---|---|
| Critical | 14px | `--fo-danger` | `--fo-danger` | Redirects attention now |
| High | 11px | `--fo-warn` | `--fo-warn` | Redirects attention soon |
| Medium | 8.5px | `--fo-line-hi` | `--fo-text-2` | Doesn't ask for a decision, so doesn't spend a colour |
| Low | 6px | `--fo-line-hi` | `--fo-text-3` | Recedes furthest |

```html
<div class="sev crit"><i></i><span>Critical</span></div>
```

---

## 8. Components

**The header system — `.section-head`.** The one FlowOps primitive for "a new operational area
begins here": a title (`h1` or `h2`), marked with a single 2px teal rule to its left, inset 3px top
and bottom so it spans exactly that block's own height — a marker, not a panel, and never a
background/border box. Two variants:

- **`.section-head--plain`** (bare, no rule, no eyebrow) is the one used almost everywhere now — a
  page's own `<h1>` in `.page-header`, and every section heading inside a page, rendered through
  the shared `_SectionHeading` partial (`Pages/Shared/_SectionHeading.cshtml` + a
  `SectionHeadingViewModel(Title, Subtitle?)`). No icon, no uppercase eyebrow, ever — that pattern
  (icon square + `.fo-label` eyebrow repeating the page's own context above every section) was tried
  early, applied inconsistently page-by-page, and retired everywhere in Phase 24A. The one
  deliberate exception is a section that needs an actual warning signal ahead of its heading (e.g.
  Account Settings' "Danger zone") — that keeps its own `.fo-label` eyebrow because the colour is
  carrying real meaning, not because it was missed.
- **`.section-head`** (with the teal rule) is reserved for the small number of places a section
  genuinely wants that marker — currently the Dashboard's own zones.

`_SectionHeading` is a partial, not a convention to reimplement per page — if a new page needs a
section heading, call the partial; don't hand-write `.panel__head`/`.fo-label`/`<h2>` again.

**`.page-header`** wraps a `.section-head` (title + context), an optional metric on the same row
(see **Header metric**, below), and `.page-actions` beneath, closed by one hairline `border-bottom`
before whatever content follows.

**Header metric — `.page-header__metric`.** Where a page's own count is a genuinely operational
signal (Work Queue's "302 tickets," At-Risk's "7 need attention") rather than passive scope
metadata (Admin's "1 member," a page's own ticket reference), it renders as
`.page-header__metric-value` (22px/600/`--fo-text-hi`) beside `.page-header__metric-label` (13px
muted) — noticeably larger and brighter than the surrounding metadata, but deliberately smaller
than the Dashboard's own 30px KPI figures: a signal in the header, not a KPI card. The older,
plainer `.page-header__count` (13px mono, `--fo-text-3`) stays exactly as it was for the passive
case — both exist on purpose, for two different things a page-header count can mean.

**Stat strip.** Figures divided by hairlines, never four bordered tiles. No icon accompanies a
number — the number is the visual element. One line of context beneath each figure, coloured only
when the trend is genuinely good or bad. `.kpi-strip` (Platform Administration, Organization
Detail, Users) and `.stat-strip`/`.stat` (Dashboard) are the same idea in two CSS implementations —
the Dashboard's own KPI row additionally links its values to filtered Work Queue views and tints
Overdue red conditionally, which the plainer `_StatStrip` partial (used everywhere else) doesn't
need to support.

**Rows.** Every list (queue, attention, at-risk) is a CSS grid with fixed column widths shared
across pages so columns align between screens. Dividers are `--fo-line-row`; the last row has none.
No zebra striping. Below 900px, columns wrap onto their own lines rather than disappearing —
priority and workflow position are operationally meaningful on a tablet or phone too, not
desktop-only decoration.

**Status badge — `.status-badge`.** A soft tinted-and-bordered pill, one tone per status
(`StatusBadgeDisplay.Tone`/`CssClass`): Open — muted grey, Assigned — info blue, In Progress —
warn amber, Pending — violet, Resolved — teal (matching the workflow rail's own resolved-stop
colour), Closed — ok green. A deliberate reversal of an earlier "deliberately colourless" pass, at
the product owner's explicit request that the six labels read as visibly distinct rather than
collapsed onto one neutral shape. Danger (red) is never one of the six — it stays reserved for
real Priority/SLA urgency alone (Principle 1/2), so a status label can never be mistaken for an
alert. Used at Ticket Detail's own header, and inline (`.status-badge--compact`, tighter padding)
within Work Queue/At-Risk row metadata.

**Priority — never a badge, always `.severity`.** A height-encoded bar (`.severity__bar`, 3px
wide) plus the word (`.severity__label`, 14px/500) beside it — see §7. Used wherever a ticket's own
Priority renders (Ticket Detail header, Work Queue rows). Deliberately distinct from
attention-signal severity (Dashboard's "what needs attention," At-Risk's ranked urgency), which
keeps its own separate `.sev` mark — the same height-encoded shape, a different meaning, never
conflated in one row (see AtRisk.cshtml's own comment on why: sharing one style there produced
rows that read "LOW ... CRITICAL," genuinely confusing since the two numbers mean different things).

**SLA state — plain coloured text, never a badge.** `.sla-state--met/within/paused/at-risk/breached`
set colour only (`--fo-ok`/`#8A9A9C`/`--fo-info`/`--fo-warn`/`--fo-danger`) on a bare `<span>`, used
at Ticket Detail's own "SLA status" fact and Work Queue/At-Risk's SLA column (`.q-sla__state`).

**Notice — `.notice`.** "An action occurred / information needs attention" — distinct from
`.empty-state` ("there is no data"), which several pages had misused to render success
confirmations inside the same dashed box that everywhere else means emptiness. A tinted surface at
low alpha with a 3px semantic-coloured left rule, no icon, no dashed border. Four tones
(`.notice--success/info/warning/danger`), rendered through the shared `_Notice` partial +
`NoticeViewModel(Message, Tone, Urgent)` — `role="status"` for an ordinary confirmation (the
default), `role="alert"` only when a caller sets `Urgent: true` for something that actually demands
immediate attention.

**Destructive-action confirmation — `.confirm`.** Every deactivate/reject/remove action requires a
second, deliberate step before it fires — no destructive mutation happens on a single click
anymore. A checkbox-driven inline reveal, the same zero-JavaScript "checkbox hack"
`.fo-nav-toggle-input` already uses for the mobile nav panel: a visually-hidden-but-keyboard-operable
checkbox, a visible `.confirm-trigger` `<label>` (caution/danger-styled) that opens the panel, and a
second `<label>` inside the revealed `.confirm-panel` pointing at the same checkbox id as Cancel
(a label click always toggles its checkbox, so Cancel just closes the panel the same way
re-clicking the trigger would). `:has()` reveals the panel and rings the trigger from one wrapping
`.confirm` container, since the checkbox and panel sit at different visual positions rather than as
literal DOM siblings. Every panel states what's changing, what happens, and whether it's
reversible — see `Pages/Platform/Index.cshtml`'s Reject confirmation for the fullest example.

**Buttons.** Exactly **one teal button per screen** where practical, and it is the action that
advances work toward resolution. `.btn` (ghost: transparent, `--fo-line-hi` border, `--fo-text-2`
text, weight 400) is the default tier. `.btn-primary` (teal fill, `#06181A` text, weight 500) is
reserved for that one constructive action. `.btn-caution` (outlined `--fo-warn`, never filled) is
for consequential-but-reversible lifecycle actions — deactivate, reject, remove — one tier below
`.btn-danger` (outlined `--fo-danger`, fills only on hover/press), reserved for the single true
destructive delete in the product (Account Settings' "Delete my account"). All four share one
geometry: 13.5px, 9px/18px padding, 4px radius, 38px height. Every button carries one of these
classes explicitly — never styled purely by the bare `button` element selector, which supplies only
the shared geometry, not which tier a control belongs to.

**Timeline.** A 1.5px gutter line with 8px dots (comments render as a smaller hollow ring instead
of a filled dot, so audit events and human content stay visually distinct even though AUDIT-RULE-06
renders them merged). The dot's tone is `TimelinePresentation.Tone` — a presentation-only mapping
over the entry's own `Kind`/`EventType` (never the free-text `Note`/`Body`/`Field`, since nothing
else on a `TicketEvent` reliably says "this transition moved the ticket into breach"): `Resolved`/
`Closed` → teal (the same meaning the rail's own resolved stop carries), `Reopened` → amber,
`PutOnHold` → the rail's paused grey, `Assigned`/`Reassigned`/`Resumed`/`StatusChanged` → info blue,
everything else → `--fo-line-hi`. Time in
mono 11px, event name 13.5px/500, detail beneath in `--fo-text-2`.

**Navigation.** A persistent **232px sidebar** (`.fo-sidebar`), not a top bar — the top-bar-only
layout described in earlier drafts of this document was replaced once the number of destinations
(tenant nav, Admin, Platform Admin, the account area) outgrew four items. Active item is teal at
weight 500 with a left rule and a tinted background; inactive is `--fo-text-2` at 400. Each nav
link carries one custom line icon at ~20px. Below 900px the sidebar becomes an off-canvas panel via
a checkbox-driven toggle (`#fo-nav-toggle` + label, `transform: translateX(-100%)`, 200ms) — no
JavaScript. The account area (avatar, email, role as plain dim text, a chevron) sits at the bottom
of the sidebar as one link to Profile & Settings; Sign out is a separate control beneath it.

**Icons.** A much larger drawn family than the original five — roughly fifty, spanning navigation,
status/priority/SLA/role glyphs, and utility icons — all built to the same spec rather than a
library import:

| Icon | Drawing |
|---|---|
| Dashboard | Circle outline with a filled centre dot (focus ring) |
| Work Queue | Three stacked horizontal lines, last one shorter |
| At-Risk | A horizontal line broken by a gap, with a dot above the gap — flow interrupted |
| Create | A plain cross, no enclosing circle |
| Search | Minimal magnifier |

(the table above is illustrative, not exhaustive — see `FlowOps.Web.Icons` for the full family.)

Spec: 24×24 viewBox, 1.8px stroke, rounded caps, monochrome, teal only when active. No filled/solid
icons, no second icon family, no emoji.

---

## 9. Density by page

| Page | Target | What that means |
|---|---|---|
| Dashboard | Moderate | Headline is the operational fact ("3 tickets need your attention"), not a greeting. Four figures, three attention rows, generous vertical rhythm. |
| Work Queue | High | Seven columns, 13px row padding. Scanning beats breathing room. |
| At-Risk | High | Same density, plus a second line per row explaining *why* the signal fired. |
| Ticket Detail | Moderate, grouped | Two columns, 48px gutter. Five zones: identity, stage/SLA, ownership, description, activity. |
| Login | Low | Centered 420px shell: brand mark + wordmark, one bordered card holding the form, demo personas as a stacked card per persona below a hairline rule — a table read as wide enough to force horizontal scroll at this shell width, so it isn't one. |

---

## 10. Never introduce

Each was either tried and rejected during exploration, or would flatten what makes the system
recognisable.

- Tinted, bordered chip badges for priority or SLA state — tried, then retired app-wide in favour
  of `.severity` (height-encoded bar) and plain coloured text, respectively (§8). The single most
  cloned dashboard pattern, and the system's own past. Status *does* use a tinted/bordered pill
  again (`.status-badge--*`, §8) — a later, deliberate reversal specifically for status, not a
  return to chips generally; priority and SLA state keep their own non-chip treatments unchanged.
- Cards with a coloured left-border accent stripe
- A grid of bordered KPI tiles, especially with an icon beside each number
- A generic percentage-filled progress bar for SLA — the rail's current stop carries that
- Drop shadows or elevation of any kind
- Decorative gradients. The only gradients are functional: conic SLA fills and the paused hatch
- All-caps data values
- Teal on anything that isn't brand, active state, or resolution
- A Kanban-column metaphor for workflow
- An icon library import, or a second icon style alongside the drawn family
- Emoji anywhere in the interface
- Inline `style="..."` attributes for anything CSS can express as a class — the CSP's
  `style-src 'self'` (no `'unsafe-inline'`) blocks the `style` attribute outright; a data-driven
  proportion becomes one of 101 pre-generated `.w-pct-0`..`.w-pct-100` (or `.rail-fill-0`..`-100`)
  classes instead (`FlowOps.Web.CssWidthClass`), never a loosened policy

---

## 11. Not yet specified

The honest gaps. Review effort pays best here — everything below genuinely does not exist yet, or
is a real, open trade-off rather than settled. (Several items this section used to list — focus
rings, forms, empty states, responsive strategy, a numeric contrast check — are resolved now; see
the sections above and the interaction-state/notice/confirmation entries in §8.)

- **Analytics.** Whether trend/workload visualisation ever gets a real charting library, or stays
  hand-built SVG/CSS bars with a `<details>` data-table fallback (the current answer, and the one
  CLAUDE.md §11.1's "no unnecessary JavaScript" favours).
- **Light theme.** Decided and built in Phase 29C — see "Themes and appearance". Open only: whether anonymous pages should ever follow the OS (today they are always Dark).
- **Motion.** Durations exist now (120–200ms `ease`, colour/background/border/transform only, no
  keyframes) and a global `prefers-reduced-motion: reduce` guard removes them for users who ask (Phase 29B).
- **Mark and favicon.** The three-node mark works at nav size; untested at 16–24px favicon size and
  in monochrome.
- **A second contrast pass.** The severity/placeholder failures this phase found and fixed were
  caught by an actual computed check, not by inspection — the rest of the palette should get the
  same treatment rather than being assumed clean by association.
- **Icon cleanup.** Roughly a dozen drawn icon constants (`FlowOps.Web.Icons`) are no longer
  referenced from any page after recent consolidation passes — real, documented design work, kept
  rather than deleted unilaterally, but a candidate for a deliberate prune.

---

## 12. Tokens

```css
/* FlowOps design tokens — dark, single theme */
:root {
  /* ground */
  --fo-bg:        #081012;
  --fo-surface:   #0D1719;
  --fo-surface-2: #132124;

  /* lines, by role */
  --fo-line-row:  #1A2729;
  --fo-line:      #243638;
  --fo-line-hi:   #34494B;

  /* text */
  --fo-text-hi:   #F6FAFA;
  --fo-text:      #E9EFEF;
  --fo-text-2:    #A7B5B7;
  --fo-text-3:    #718285;

  /* identity — teal means resolution */
  --fo-teal:      #5FD3C4;
  --fo-teal-dim:  #0F766E;
  --fo-teal-ink:  #06181A;

  /* one additional restrained hue — reserved for the Viewer role badge alone */
  --fo-violet:    #8D85C9;

  /* operational semantics */
  --fo-ok:        #4CC38A;
  --fo-info:      #63A7E8;
  --fo-warn:      #E8B45C;
  --fo-danger:    #E56B6F;

  /* spacing */
  --fo-s1: 4px;  --fo-s2: 8px;  --fo-s3: 12px; --fo-s4: 16px;
  --fo-s5: 24px; --fo-s6: 32px; --fo-s7: 48px;

  /* shape */
  --fo-radius-chip: 3px;
  --fo-radius:      4px;

  /* type */
  --fo-font: "Geist", system-ui, -apple-system, sans-serif;
  --fo-mono: "Geist Mono", ui-monospace, SFMono-Regular, monospace;
}
```

---

## Appendix — key markup patterns

**Rail, compact (breached, current stop = In Progress):**

```html
<div class="rail">
  <b class="past"></b><i class="done"></i>
  <b class="past"></b><i class="done"></i>
  <b class="now" style="background:var(--fo-danger); box-shadow:0 0 0 3px rgba(229,107,111,.18);"></b>
  <i></i><b class="next"></b>
  <i></i><b class="next"></b>
  <i></i><b class="next"></b>
</div>
```

```css
.rail { display:flex; align-items:center; }
.rail i { flex:1; height:1.5px; background:var(--fo-line); }
.rail i.done { background:var(--fo-line-hi); }
.rail b { border-radius:50%; flex-shrink:0; }
.rail b.past { width:6px; height:6px; background:var(--fo-line-hi); }
.rail b.next { width:6px; height:6px; border:1.5px solid var(--fo-line-hi); }
.rail b.now  { width:14px; height:14px; }
.rail.lg b.past,
.rail.lg b.next { width:9px; height:9px; }
.rail.lg b.now  { width:24px; height:24px; border:2px solid var(--fo-bg); }
```

**Severity:**

```css
.sev { display:flex; align-items:center; gap:8px; }
.sev i { width:2px; border-radius:1px; flex-shrink:0; }
.sev span { font-size:13px; font-weight:500; letter-spacing:-.005em; }
.sev.crit i { height:14px;  background:var(--fo-danger); }  .sev.crit span { color:var(--fo-danger); }
.sev.high i { height:11px;  background:var(--fo-warn); }    .sev.high span { color:var(--fo-warn); }
.sev.med  i { height:8.5px; background:var(--fo-line-hi); } .sev.med  span { color:var(--fo-text-2); }
.sev.low  i { height:6px;   background:var(--fo-line-hi); } .sev.low  span { color:var(--fo-text-3); }
```

**Brand mark** (a ring with one wave crossing it — one continuous current held inside a boundary).
Defined once, as `Icons.BrandMark(int size = 24)` in `src/FlowOps.Web/Icons.cs`, never duplicated
inline; every call site (sidebar, mobile top bar, Login/Register/Accept Invitation/Pending
Approval/Reset Password) renders this exact markup, scaled only by the `width`/`height`
attributes — the `viewBox` and path geometry never change:

```html
<svg class="brand-mark" width="24" height="24" viewBox="0 0 24 24" aria-hidden="true">
  <circle class="brand-mark__ring" cx="12" cy="12" r="9.5" fill="none"></circle>
  <path class="brand-mark__wave" d="M4.5,12 Q8.25,5 12,12 T19.5,12" fill="none"></path>
</svg>
```

```css
.brand-mark { flex-shrink: 0; }
.brand-mark__ring { stroke: var(--fo-teal); stroke-width: 1.3; }
.brand-mark__wave { stroke: var(--fo-teal); stroke-width: 1.9; stroke-linecap: round; }
```

Colour comes from `--fo-teal`, never a hardcoded hex — this is a CSS-driven mark like everything
else in the app, so it re-themes if the token ever does.

**Favicon** (`wwwroot/favicon.svg`) is a deliberately separate pass, not a scaled copy of the mark
above. At true 16–32px render size, the 1.3/1.9px strokes above are close to sub-pixel and blur;
the favicon uses the same geometry at thicker, hand-tuned weights instead:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
  <circle cx="12" cy="12" r="9.3" stroke="#5FD3C4" stroke-width="2" fill="none"/>
  <path d="M4.5,12 Q8.25,5 12,12 T19.5,12" stroke="#5FD3C4" stroke-width="2.6" fill="none" stroke-linecap="round"/>
</svg>
```

Its `#5FD3C4` is a **deliberate, documented exception** to "colour comes from a token, never a
hardcoded hex": this file is a standalone static asset the browser loads directly via
`<link rel="icon">`, with no access to the app's CSS custom properties at all — a literal value is
the only value it *can* hold. `apple-touch-icon.png` (180×180, iOS home-screen — no SVG support)
and `favicon-32.png` (32×32, pre-SVG-favicon browsers) are one-time raster exports of this same
geometry, regenerated by hand if the mark ever changes rather than an automated build step.

## Project planning surfaces (ADR-0029, ADR-0030)

Overview / Board / Tickets / Sprints share one header (`_ProjectHeader`: breadcrumb, title, `.proj-tabs` links —
real pages, no JS).

**Board.** Five columns in an auto-fit grid (`.board-col`, they wrap, never scroll sideways): **Planned**,
Open, In Progress, Pending, Done. *Planned* means "in this sprint, not yet moved onto the board"; it is a
planning position (dashed border), not a ticket status, and those tickets keep their real status. Assigned
shows inside Open. A card (`.board-card`) has a priority stripe — Critical danger, High warning, Medium
neutral, Low muted — plus the priority word (never colour alone), the assignee, and deadline flags.

**Moving tickets.** Every move is an explicit action in the shared `.row-menu` list
(`_PlanningTicketMenu`): *Assign to me*, *Move to Open*, *Move back to planned*, *Move to <sprint>*,
*Move out of sprint*. Drag-and-drop is a mouse enhancement of the same actions: a dragged card dims,
valid columns get a teal border, invalid ones dim, and nothing changes in the browser until the server
has re-rendered. Hold / resolve / reopen ask for their required text in a native `<dialog>`. The menu opens
to the left of its "…" button, keeps itself inside the viewport, and only one is open at a time
(`row-menu.js`); the button is 28px with a ~40px tap area.

**Sprints.** Status is Planned, Active, Completed or Cancelled (`status-badge--muted/--teal/--ok/--muted`, always
with the word). Complete and Cancel both ask first with the zero-JS `.confirm` panel; Cancel is a quiet
danger button (neutral border, danger text). A completed sprint's numbers are frozen (completion snapshots);
"Carried from Sprint N" is plain muted text (`.carried-note`) on the Board card, the Tickets row and Ticket
Detail. Ticket Detail shows the sprint as a read-only line ("Sprint 2 - Pilot devices · Current sprint · dates",
or "No sprint").

**Tables.** `.plan-table` reflows to stacked label/value rows instead of hiding columns (`--tickets` and
`--wide` below 1360px, the small tables below 900px); a wrapper contains its own overflow.

## Deadline wording and treatment

Users see plain language; the domain keeps its technical names. *Service deadline* (panel heading), states
**On track / Deadline soon / Deadline missed / Deadline met / Paused** (`SlaDisplay.StatusLabel`), KPI
**Deadlines met**, and **Past due** for the separate planning due date. "SLA" stays only on Admin
configuration screens. A deadline state is **plain coloured text** everywhere (Work Queue, At-Risk,
Board, Ticket Detail): danger for missed and past due, warning for soon, muted otherwise; the words carry the
meaning and the colour only reinforces it. Tinted chips are reserved for ticket *status*.

## Dashboard composition

Order: filter bar → **Summary** (four figures, one panel) → **What needs attention** (top three, "View all N
at-risk items") → a single two-column analytical grid (`.dashboard-charts`, equal `minmax(0, 1fr)` columns, rows
share a height): Ticket volume | Tickets by status, Deadline status | Workload by team → Average resolution
time → Current workload. It is one column below 900px. Layout owns its spacing: grids set `gap`, a plain
vertical stack of panels uses `.panel-stack`; there is no global sibling-panel margin.

## Work Queue

Opens on unfinished work (Open, Assigned, In Progress, Pending). A "Show finished" switch adds Resolved and
Closed; a KPI filter or a search already defines its population and includes them.

## Themes and appearance

**One token set, two value sets.** `:root` holds the Dark values (unchanged from the original system).
`:root[data-theme="light"]` restates only the tokens whose value differs; components read tokens and are
never themed individually (no `.light-panel`). `data-theme="system"` applies the same Light block inside
`@media (prefers-color-scheme: light)`, so it follows the OS live. A test keeps the Light and System
blocks identical and complete. `color-scheme` is set per theme so native controls follow.

**Preference.** Per user (`AspNetUsers.appearance`: `Dark` | `Light` | `System`, text with a check
constraint), default **Dark** for everyone, set in Profile & Settings → Appearance (a compact segmented
radio group; selected = filled marker + stronger label, options ≥44px). `System` is stored as `System`,
never as the resolved value. The server renders `<html data-theme="…">` from the saved value, so the
first byte is already correct: no flash, no boot script, nothing for the CSP (`script-src 'self'`) to
object to. `appearance-preview.js` (external, ~30 lines) only previews a radio choice before saving; it is
not the source of truth and an unsaved preview is discarded on leaving or via back/forward cache.
**Anonymous pages (login, register, invitation, reset) are always Dark**; demo personas may change theirs.

**Light character.** Warm off-white ground (`#F6F4EE`), white surfaces, charcoal text, neutral warm lines,
the same restrained teal. No gradients, no new shadows. Values that must differ by theme are tokens:
`--fo-teal-text` (teal used as text/outline/stroke — a deeper teal in Light; teal *fills* keep `--fo-teal`),
the semantic colours and their channels (`--fo-danger-rgb` etc., so tinted backgrounds/borders/glows follow
the theme: `rgb(var(--fo-danger-rgb) / .18)`), `--fo-track`, the scrims and the popup shadow. Saturated
fills carry `--fo-on-fill` text in both themes.

**Semantics are unchanged by theme**: danger = missed/past due/critical, warning = soon/high, ok = met/
done, info = assigned/informational, teal = identity/active/resolution, violet = pending/viewer. State is
always also carried by words, markers or shape — never colour alone.

**Accessibility.** Measured on the rendered app: text ≥4.5:1 (≥3:1 large) across the tested pages in Light
(zero failures); Light semantic text colours are ≥6.9:1 on white and stay ≥4.5:1 on their own tinted badge
backgrounds. Focus ring is 2px `--fo-teal-text` (≈7.7:1 on white). Known, shared by both themes: control and
line borders are around 2:1, below WCAG 1.4.11's 3:1 for controls — recorded, not changed here.

## Show inactive (Admin lists)

Teams, a team's categories, Projects and Members show **active records only** by default; a "Show
inactive" switch (`?showInactive=true`, a GET form, bookmarkable, never stored) adds the inactive ones,
and a muted "N inactive … hidden" note explains what is hidden. It is a view choice only: no lifecycle
change, no reactivation, no change to authorization, and inactive records still cannot be used for new
tickets.
