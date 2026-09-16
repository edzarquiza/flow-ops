# FlowOps — UI refinement pass (Phase 11)

This is a **presentation-layer change only**. No domain logic, no application services, no business
rules, no migrations. Everything here lives in `FlowOps.Web/Pages/**` and `wwwroot/**`.

The CSS below is written against the dark token set already in use in the deployed build. **I have
not read the current `flowops.css` directly** — class names like `.kpi-card`, `.attention-list`,
`.action-group` are taken from an earlier audit and may have drifted. Treat the CSS as the target
state and map it onto whatever the real class names are. Do not blindly append it and leave dead
rules behind.

Work through the sections in order. Sections 1–3 are structural and make 4–8 easier; do not skip ahead.

---

## Step 0 — Inspect first, and report back

Before changing anything:

1. Read `wwwroot/flowops.css` in full. List the class names actually in use for: the sidebar, the
   top bar, page headers, table rows, badges/chips, buttons, panels.
2. Read `Pages/_Layout.cshtml` and confirm how sidebar + top bar + `<main>` are structured, and what
   sets content width.
3. Read `Pages/Index.cshtml`, `Pages/Tickets/Index.cshtml`, `Pages/Tickets/AtRisk.cshtml`,
   `Pages/Members/Index.cshtml` (or wherever Members lives), `Pages/Tickets/Create.cshtml`, and the
   Profile/Settings page.
4. Tell me the mapping from my class names below → your real ones before you start editing. If a
   pattern below doesn't exist in the codebase at all, say so rather than inventing it.

---

## 1. Fix the layout container (do this first — several other problems are symptoms of it)

Currently the content column starts well right of the sidebar, leaving a large dead gutter, and the
content is capped far below the available width. This is what forces table cells to wrap.

```css
.app-shell {
  display: flex;
  min-height: 100vh;
}

.app-sidebar {
  width: 220px;
  flex-shrink: 0;
  height: 100vh;
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--fo-line);
  background: var(--fo-surface);
}

.app-sidebar__nav { flex: 1; overflow-y: auto; }
.app-sidebar__footer { flex-shrink: 0; padding-bottom: 16px; }  /* fixes "Sign out" being cut off */

.app-content {
  flex: 1;
  min-width: 0;              /* critical — without this, wide tables force overflow instead of shrinking */
  padding: 28px 36px 56px;
}

.app-content > * { max-width: 1400px; }
```

**The sidebar being cut off at the viewport bottom is a real bug** — `Profile & Settings` and
`Sign out` are sliced in half in the current build. The `height: 100vh` + flex + `flex-shrink: 0`
footer above is the fix.

Style the scrollbar while you're here — a default light scrollbar against `#081012` is the single
most visible unfinished detail:

```css
* { scrollbar-width: thin; scrollbar-color: var(--fo-line) transparent; }
*::-webkit-scrollbar { width: 10px; height: 10px; }
*::-webkit-scrollbar-thumb { background: var(--fo-line); border-radius: 5px; }
*::-webkit-scrollbar-track { background: transparent; }
```

---

## 2. Page header — replaces the pattern on all six pages

Remove, on every page: the teal left accent bar, the uppercase eyebrow label (`WORK`, `ATTENTION`,
`ACCOUNT`, `OPERATIONS OVERVIEW`, `MEMBERS`, `ORGANIZATION`), the horizontal rule below the header,
and the detached 34px stat block.

The eyebrows restate the title directly beneath them. The teal bar spends the one identity colour on
decoration. The stat is a scope indicator, not a KPI, so it doesn't need display type.

```html
<header class="page-head">
  <div>
    <div class="page-head__title">
      <h1>Work queue</h1>
      <span class="page-head__count">302</span>
    </div>
    <p class="page-head__sub">Your operational workspace</p>
  </div>
  <div class="page-head__actions">
    <a class="btn btn-ghost" href="/Tickets/AtRisk">At-risk work</a>
    <a class="btn btn-primary" href="/Tickets/Create">New ticket</a>
  </div>
</header>
```

```css
.page-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 28px;
}
.page-head__title { display: flex; align-items: baseline; gap: 11px; margin-bottom: 5px; }
.page-head__title h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 500;
  color: var(--fo-text-hi);
  letter-spacing: -0.022em;
}
.page-head__count {
  font-size: 13px;
  color: #5a6b6e;
  font-variant-numeric: tabular-nums;
}
.page-head__sub { margin: 0; font-size: 13px; color: var(--fo-text-3); }
.page-head__actions { display: flex; gap: 8px; }
```

Drop the `+` from "+ New ticket" — the teal fill already means create.

---

## 3. Container discipline

- **Remove the outer bordered panel** wrapping the tables on Work Queue, At-Risk, and Members. The
  table currently sits inside a bordered rounded panel *and* has its own header row and internal
  rules — panel-in-panel. Let the table sit on the page ground.
- **Dashboard attention panel:** remove the icon-in-a-rounded-square, the `ATTENTION` eyebrow, and
  the partial-width underline beneath the `<h2>`. Keep only the `<h2>`. Four introductions for one
  list is three too many, and the partial underline appears nowhere else in the system.
- **Remove "Showing page 1 of 13 (302 tickets)"** from above the table — it restates the header
  count. Keep pagination context next to the pager at the bottom only.

---

## 4. Work queue rows — the main change

Replace the column-grid table with two-line composed rows. Rationale: a strict nine-column grid
forces wrapping and reads as a database viewer; a two-line row with hard-recessed metadata puts the
title first and lets a single colour carry urgency.

```html
<a class="q-row is-breached" href="/Tickets/Details/630">
  <div class="q-main">
    <div class="q-title">Live rollout verification ticket</div>
    <div class="q-meta">
      <span class="q-ref">FO-000630</span><span class="q-dot">·</span>
      <span>Service Desk</span><span class="q-dot">·</span>
      <span>Unassigned</span><span class="q-dot">·</span>
      <span>Open</span><span class="q-dot">·</span>
      <span>2h ago</span>
    </div>
  </div>

  <div class="q-sev q-sev--medium"><i></i><span>Medium</span></div>

  <div class="q-rail" aria-hidden="true"> ... rail markup ... </div>

  <div class="q-sla">
    <span class="q-sla__state">Breached</span>
    <span class="q-sla__time">20h 15m over</span>
  </div>
</a>
```

```css
.q-row {
  position: relative;
  display: flex;
  align-items: center;
  gap: 18px;
  padding: 13px 14px;
  border-bottom: 1px solid #141F21;
  text-decoration: none;
  transition: background 120ms ease;
}
.q-row:hover { background: var(--fo-surface); }
.q-row:focus-visible { outline: 2px solid var(--fo-text-hi); outline-offset: -2px; }
.q-row:last-child { border-bottom: none; }

/* left edge urgency — transparent unless the ticket actually needs attention */
.q-row::before {
  content: '';
  position: absolute;
  left: 0; top: 6px; bottom: 6px;
  width: 2px;
  border-radius: 1px;
  background: transparent;
}
.q-row.is-breached::before { background: var(--fo-danger); }
.q-row.is-at-risk::before  { background: var(--fo-warn); }

.q-main  { flex: 1; min-width: 0; }
.q-title {
  font-size: 14.5px;
  color: var(--fo-text);
  letter-spacing: -0.006em;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  margin-bottom: 5px;
}
.q-meta {
  font-size: 11.5px;
  color: var(--fo-text-3);
  display: flex; align-items: center; gap: 7px;
  white-space: nowrap;
}
.q-ref { font-family: var(--fo-mono); color: #5a6b6e; }
.q-dot { color: #2C3D3F; }

/* severity: height encodes rank, colour only for the two that demand a decision */
.q-sev { display: flex; align-items: center; gap: 7px; width: 74px; flex-shrink: 0; }
.q-sev i { width: 2px; border-radius: 1px; flex-shrink: 0; }
.q-sev span { font-size: 12px; }
.q-sev--critical i { height: 14px;  background: var(--fo-danger); }
.q-sev--critical span { color: var(--fo-danger); }
.q-sev--high     i { height: 11px;  background: var(--fo-warn); }
.q-sev--high     span { color: var(--fo-warn); }
.q-sev--medium   i { height: 8.5px; background: #3E5457; }
.q-sev--medium   span { color: #8A9A9C; }
.q-sev--low      i { height: 6px;   background: #3E5457; }
.q-sev--low      span { color: #63767A; }

.q-rail { display: flex; align-items: center; width: 104px; flex-shrink: 0; }
.q-rail i { flex: 1; height: 1.5px; }
.q-rail b { border-radius: 50%; flex-shrink: 0; }
.q-rail b.stop        { width: 5px; height: 5px; background: #3E5457; }
.q-rail b.stop--ahead { width: 5px; height: 5px; background: none; border: 1.5px solid #3E5457; }
.q-rail b.stop--now   { width: 13px; height: 13px; }
.q-rail b.stop--resolved { background: var(--fo-teal); }
.q-rail i.done   { background: #3E5457; }
.q-rail i.ahead  { background: #1E2E30; }

.q-sla { width: 112px; flex-shrink: 0; text-align: right; }
.q-sla__state { font-size: 12.5px; display: block; margin-bottom: 3px; color: #8A9A9C; }
.q-sla__time  { font-family: var(--fo-mono); font-size: 11px; color: #5a6b6e; font-variant-numeric: tabular-nums; }
.is-breached .q-sla__state { color: var(--fo-danger); }
.is-at-risk  .q-sla__state { color: var(--fo-warn); }
```

**Rules that matter here:**

- **One accent colour per row, maximum.** SLA state is the only coloured text. Priority stays grey
  unless High/Critical. Status is always plain. A healthy row should have zero colour — that's what
  makes the breached row jump.
- **Drop the Category column entirely.** "Service Desk Incidents" on every row of a Service Desk
  queue carries no information. Make it a filter instead.
- **`white-space: nowrap` on references** — `FO-000630` currently breaks across two lines.
- **Remove the underline from reference links.** A column of underlines is noise; underline on
  hover only.
- The rail must sit in a **fixed-width column** so rails align vertically down the page. That
  alignment is what makes them readable as a pattern rather than unrelated dot clusters.
- Keep a real `<a>` wrapping the row so keyboard navigation and middle-click still work. If the
  current markup is a `<table>`, either keep the table and apply this styling to `<td>`s, or move to
  a list of anchors — **tell me which you chose and why**, since it affects the accessibility story
  (a table gives column semantics; a list gives simpler reading order).

**Accessibility:** the rail is decorative given a status word is always present in `.q-meta` —
mark it `aria-hidden="true"`, otherwise a screen reader announces ten meaningless elements per row.

---

## 5. At-Risk page

Same row pattern as §4, plus:

- **"Why it needs attention" is currently stacked chips + wrapped text, blowing rows to ~140px.**
  Render signals as one dim line inside the metadata: `SLA breached 87d · Open 88 days`. Only the
  highest-severity signal gets colour; the rest are `--fo-text-3`.
- **Priority and signal severity currently look identical and sit adjacent**, producing rows that
  read "LOW … CRITICAL" — genuinely confusing, because they're different concepts. Give signal
  severity the height-bar treatment (§4) and leave ticket priority as plain text, or the reverse.
  They must not share a visual treatment.
- **"← Work queue" should be a plain text link**, not a bordered button — it competes with primary
  actions and duplicates the sidebar.

---

## 6. Members

- **Role must not be a live `<select>` on every row.** 25 rows × (select + Update + Remove) is 75
  interactive controls rendered at once, which is why the page feels heavy. Render role as plain
  text; put Update/Remove behind a `⋯` row menu at the right edge, or make role click-to-edit.
- **Remove buttons must not be red.** Nothing in this system uses a danger button. Ghost them.
- **Collapse the triple heading** — `MEMBERS` eyebrow + "Organization members" h2 + "Members of this
  organization" subtitle is the same word three times. Keep one.
- **Locked demo rows currently just have no action buttons**, which reads as broken rendering. Show
  an explicit dim `Locked` label in the actions column instead.
- **Role is a chip in row 1 and a select in rows 2+.** One concept, one presentation.
- **"Active" status** is unstyled plain text amid heavy chrome — give it a small dot indicator or
  push it into the recessed metadata.

---

## 7. Forms — Create Ticket and Profile & Settings

```css
.form-col { max-width: 600px; }          /* one column width, held everywhere */
.form-field { margin-bottom: 18px; }
.form-field label { display: block; font-size: 12.5px; color: var(--fo-text-2); margin-bottom: 6px; }
.form-field input, .form-field select, .form-field textarea {
  width: 100%;
  background: var(--fo-surface-2);
  border: 1px solid var(--fo-line);
  border-radius: var(--fo-radius);
  color: var(--fo-text);
  font-family: inherit;
  font-size: 14px;
  padding: 9px 12px;
}
.form-field input:focus-visible,
.form-field select:focus-visible,
.form-field textarea:focus-visible {
  outline: 2px solid var(--fo-text-hi);
  outline-offset: 1px;
  border-color: transparent;
}
.form-field textarea { min-height: 100px; resize: vertical; }
.form-field select {
  appearance: none;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 12 12'%3E%3Cpath d='M3 5l3 3 3-3' stroke='%23718285' stroke-width='1.5' fill='none' stroke-linecap='round'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right 12px center;
  padding-right: 32px;
}
.form-req { color: var(--fo-warn); margin-left: 3px; }
```

- **Selects currently look identical to text inputs** — the chevron above fixes that.
- **Mark required fields** (`<span class="form-req">*</span>`) — there's currently no indication.
- **Settings repeats every label twice** — an `<h2>Full name</h2>` directly above a
  `<label>Full name</label>`. Drop the label; the heading is the label.
- **Settings has three teal Save buttons**, breaking the one-primary-per-screen rule. Make all three
  ghost — nothing on that page advances work toward resolution.
- **"New email" is pre-filled with the current email**, directly below a line stating the current
  email. Leave it empty with the current address as `placeholder`.
- **Create Ticket's right half is empty.** Consider a live SLA preview panel there — "Priority:
  Medium → 24h target, due Thu 09:12" — computed from the existing `SlaPolicy`. **Ask me before
  building this**; it's a genuine feature addition, not a styling change, and it touches the
  application layer.

---

## 8. Top bar and account control

The top bar is ~60px holding only a right-aligned email and a role chip — ~75% empty on every page,
and it duplicates the sidebar's Profile/Sign out.

Pick one and tell me which you did:

- (a) Remove the top bar entirely; move the account control to the sidebar footer where Profile and
  Sign out already are.
- (b) Keep a slim (44px) top bar with a single account control — avatar + name, opening a menu with
  role, Profile & Settings, Sign out — and remove Profile/Sign out from the sidebar.

Either way: **the role chip must stop being a bordered box.** It's styled like an interactive
control and isn't one. Plain dim text, or inside the account menu.

---

## 9. Craft details

- **Vertical rhythm:** gaps between page regions are currently all roughly equal, so nothing groups.
  Use 16px between related blocks, 32–40px between unrelated regions.
- **Hover states on every interactive row**, 120ms, surface fill — not a border change.
- **Focus states everywhere**, all using `2px solid var(--fo-text-hi)` with offset. Never vary focus
  colour by component — keyboard users scan for one consistent shape.
- **Dashboard:** "121 more tickets need attention." is unstyled text above a button. Fold it into
  the button label ("View all 126 at-risk items") and delete the sentence.
- **Dashboard filter bar:** "Apply" is a bordered button and "RESET" is an uppercase underlined
  link. Make Reset a ghost button at the same size, or a small text button in `--fo-text-3`.
  Uppercase underlined text links look dated.
- **Dashboard attention rows** currently lead with a red severity chip, so the first column is a
  stack of red boxes. Lead with the title; move severity to the left-edge bar per §4.
- **"87d 17h over" appears twice per attention row** — in the subtitle and right-aligned. Keep one.

---

## 10. Verification before calling this done

- [ ] Sidebar renders fully at 768px, 900px, and 1080px viewport heights — "Sign out" is never cut off.
- [ ] No table cell wraps at 1440px viewport width; no reference ever breaks across lines.
- [ ] A healthy (within-SLA, Medium, Assigned) queue row contains **zero** coloured text.
- [ ] Rails align vertically down the queue — check by eye with 10+ rows on screen.
- [ ] Tab through Work Queue, At-Risk, Create Ticket, Members: every interactive element has a
      visible focus ring, all identical.
- [ ] No page has more than one `.btn-primary`.
- [ ] No red/danger-styled button exists anywhere in the app.
- [ ] Every page still works with JavaScript disabled.
- [ ] `dotnet test` passes — this touches no C#; if a test breaks, something leaked into the wrong layer.
- [ ] Grep for now-unused CSS rules (`.kpi-card`, old chip classes, the eyebrow/accent-bar rules) and
      delete them. Do not leave dead CSS behind.
