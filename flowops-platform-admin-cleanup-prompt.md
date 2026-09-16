# FlowOps — Platform Administration page cleanup

Presentation-layer change only. No domain logic, no application services, no business rules, no
migrations. Lives in whatever `.cshtml`/`.cs` renders the Platform Administration area plus
`wwwroot/flowops.css`.

This page currently repeats a heavy header pattern four times and puts icons on every KPI number,
which directly contradicts the written design system (`docs/ui/theme-spec.md` or wherever the dark
design doc lives — confirm the path during inspection). This prompt makes it consistent with that
system, not a new direction.

---

## Step 0 — Inspect first

1. Find and read whatever renders `/Platform` (or the admin area's actual route — confirm the
   name). Read `PageModel`/`.cs`, `.cshtml`, and any partials.
2. Read the current `flowops.css` classes in use here: the icon-square + eyebrow + heading pattern,
   the stat tiles, the table rows, the account control at the sidebar bottom.
3. Check whether the **Work Queue two-line row refinement** and the **page-header simplification**
   (teal accent bar / eyebrow removal) from the earlier UI pass have already landed. If they have,
   reuse those exact classes rather than inventing new ones for this page — this page should look
   like it belongs to the same system, not a parallel one.
4. Report back: current class names, whether the earlier refinement pass is in place yet, and the
   real route/file names, before editing anything.

---

## 1. Remove the repeated section-header pattern

Currently every section — Overview, Pending approvals, Organizations, Users — renders an icon
square, the word "PLATFORM" (identical, unhelpful, four times), then the actual heading. Delete the
icon and the eyebrow. Keep only a plain heading:

```html
<h2 class="section-head">Pending approvals</h2>
<p class="section-sub">New accounts waiting for platform approval</p>
```

```css
.section-head { margin: 0 0 4px; font-size: 15px; font-weight: 500; color: var(--fo-text-hi); }
.section-sub  { margin: 0 0 14px; font-size: 12.5px; color: var(--fo-text-3); }
```

**Do not introduce new icons for these sections.** The four icons currently used here (a bullseye,
a dash-in-circle, another bullseye, a people glyph) are not part of the existing five-icon nav
family and directly violate the "no second icon style alongside the drawn family" rule. Delete
them; do not replace them with anything.

If a `.section-head`-equivalent class already exists elsewhere in the app from the earlier UI pass,
use that instead of creating a new one.

---

## 2. Stat strip — hairlines, not icon tiles

Current stat blocks each carry an icon above the number. This is an explicit violation of the
design doc's stat-strip rule ("No icon accompanies a number — the number is the visual element")
and the "Never introduce" list ("A grid of bordered KPI tiles, especially with an icon beside each
number"). Remove every icon from this strip and replace the layout with hairline-divided figures:

```html
<div class="stat-strip">
  <div class="stat-strip__item">
    <div class="stat-strip__label">Organizations</div>
    <div class="stat-strip__value">26</div>
    <div class="stat-strip__caption">26 active · 0 inactive</div>
  </div>
  <div class="stat-strip__item">
    <div class="stat-strip__label">Users</div>
    <div class="stat-strip__value">54</div>
    <div class="stat-strip__caption">52 active · 1 pending · 1 inactive</div>
  </div>
  <div class="stat-strip__item">
    <div class="stat-strip__label">Tickets</div>
    <div class="stat-strip__value">636</div>
    <div class="stat-strip__caption">370 in last 30 days</div>
  </div>
  <div class="stat-strip__item">
    <div class="stat-strip__label">Platform health</div>
    <div class="stat-strip__value stat-strip__value--teal">Healthy</div>
    <div class="stat-strip__caption">Database connected</div>
  </div>
</div>
```

```css
.stat-strip {
  display: flex;
  border-top: 1px solid var(--fo-line-row);
  border-bottom: 1px solid var(--fo-line-row);
  margin-bottom: 36px;
}
.stat-strip__item {
  flex: 1;
  padding: 16px 20px;
  border-left: 1px solid var(--fo-line-row);
}
.stat-strip__item:first-child { padding-left: 0; border-left: none; }
.stat-strip__item:last-child  { padding-right: 0; }

.stat-strip__label {
  font-size: 10.5px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--fo-text-3);
  margin-bottom: 7px;
}
.stat-strip__value {
  font-size: 30px;
  font-weight: 500;
  color: var(--fo-text-hi);
  letter-spacing: -0.03em;
  line-height: 1;
  font-variant-numeric: tabular-nums;
}
.stat-strip__value--teal { color: var(--fo-teal); }  /* "Healthy" — a word, not a number, still earns the identity colour since it's a genuinely good status */
.stat-strip__caption {
  font-size: 11.5px;
  color: #5a6b6e;
  margin-top: 6px;
}
```

If the app already has a `.kpi-card`/stat-strip pattern from the dashboard work, check whether it
already matches this shape (hairline-divided, no icons). If so, reuse it exactly rather than
creating a second, near-identical version — same failure mode as the three-card-patterns issue
found earlier in the audit.

---

## 3. Pending approvals — reuse the existing urgency-bar pattern, don't invent a new one

FlowOps already has a left-edge accent bar convention for "this needs attention" (used on at-risk
ticket rows). Apply the exact same convention here rather than a page-specific alert style:

```html
<section class="pending-approvals" data-has-pending="true">
  <h2 class="section-head">Pending approvals</h2>
  <p class="section-sub">New accounts waiting for platform approval</p>

  <div class="approval-row">
    <div class="approval-row__main">
      <div class="approval-row__org">Live Pending Org</div>
      <div class="approval-row__meta">Live Pending User · pending_1789461316@livecheck24a.local · registered 2026-09-15</div>
    </div>
    <span class="approval-row__status">Pending</span>
    <div class="approval-row__actions">
      <button class="btn btn-ghost">Review</button>
      <button class="btn btn-primary">Approve</button>
    </div>
  </div>
</section>
```

```css
.pending-approvals { margin-bottom: 36px; }

.approval-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 13px 0 13px 16px;
  border-left: 2px solid var(--fo-warn);
}
.approval-row__main { flex: 1; min-width: 0; }
.approval-row__org  { font-size: 13.5px; color: var(--fo-text); margin-bottom: 3px; }
.approval-row__meta { font-size: 11.5px; color: var(--fo-text-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.approval-row__status { font-size: 12px; color: var(--fo-warn); flex-shrink: 0; }
.approval-row__actions { display: flex; gap: 8px; flex-shrink: 0; }
```

**The warn-coloured left border only renders when there is at least one pending approval.** When
the list is empty, remove the section entirely (or render a plain "No accounts awaiting approval"
line with no border, no colour) — matching how an at-risk queue with no signals shows no accent bar
either. Do not leave a bare/empty warn-bordered container.

`Approve` is `.btn-primary`, `Review` is `.btn-ghost` — this is the one primary action on the page;
confirm nothing else on Platform Administration is also styled `.btn-primary`.

---

## 4. Organizations / Users tables — clean up in place, don't restructure

These are homogeneous record lists someone scans and compares, not a triage queue — a strict column
grid is the right call here, unlike the Work Queue's two-line rows. Keep the `<table>`, fix these
specifics:

- **Remove the underline from every "View →" link.** Underline on hover only:
  ```css
  .table-action-link { color: var(--fo-teal); text-decoration: none; }
  .table-action-link:hover { text-decoration: underline; }
  ```
- **"X of Y shown" captions must be dim, not primary-brightness text.** `font-size: 12px; color:
  var(--fo-text-3)` (or `#5a6b6e` if that's the exact secondary tone already in use elsewhere).
- **Status column stays plain text**, no chip/badge — `Active` in `var(--fo-text-2)`, matching the
  no-pill-badges rule already established for Work Queue.
- **Member/ticket counts get `font-variant-numeric: tabular-nums`** for column alignment, same as
  every other numeric column in the app.
- Leave the Users table's avatar-initial circles as they are — do not add matching avatars to the
  Organizations table and do not remove the ones on Users. This is a deliberate distinction
  (individual identity vs. abstract entity), not an inconsistency to fix.

---

## 5. Account control at the sidebar bottom

Currently a bordered box containing only an email address — no avatar, no chevron, nothing
indicating it opens a menu. It reads as a disabled input field.

```html
<button class="account-control" aria-haspopup="menu" aria-expanded="false">
  <span class="account-control__avatar">DM</span>
  <span class="account-control__email">dexmantuna@gmail.com</span>
  <svg class="account-control__chevron" width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
    <path d="M3 4.5l3 3 3-3" stroke="currentColor" stroke-width="1.5" fill="none" stroke-linecap="round"/>
  </svg>
</button>
```

```css
.account-control {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  background: transparent;
  border: 1px solid var(--fo-line);
  border-radius: var(--fo-radius);
  padding: 8px 10px;
  color: var(--fo-text-2);
  cursor: pointer;
  font-family: inherit;
  font-size: 12.5px;
}
.account-control:hover { background: var(--fo-surface-2); }
.account-control:focus-visible { outline: 2px solid var(--fo-text-hi); outline-offset: 2px; }

.account-control__avatar {
  width: 22px; height: 22px;
  border-radius: 50%;
  background: var(--fo-surface-2);
  border: 1px solid var(--fo-line-hi);
  display: flex; align-items: center; justify-content: center;
  font-size: 10px; font-weight: 500; color: var(--fo-text-2);
  flex-shrink: 0;
}
.account-control__email { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: left; }
.account-control__chevron { flex-shrink: 0; color: var(--fo-text-3); }
```

If clicking this is meant to open a menu with Profile & Settings / Sign out, wire that up as
progressive enhancement (vanilla JS toggling `aria-expanded` + a menu panel); if JS is disabled,
the button should still be a real link to Profile & Settings so nothing breaks. **Confirm with me
whether Profile & Settings / Sign out should move into this menu, or stay as separate sidebar links
below it** — that changes the markup meaningfully and I don't want you guessing on it.

---

## 6. Header/subtitle copy

```html
<h1>Platform Administration</h1>
<p class="page-head__sub">Signed in as Platform Admin — scoped to the whole platform, not one organization.</p>
```

Replaces "Manage organizations, users, and platform operations. Signed in as Platform Admin —
platform authority, independent of any organization membership." Same fact, shorter, and drops the
"platform authority, independent of..." phrasing that reads like an internal comment rather than
UI copy. If the app already adopted the simplified `page-head` pattern (h1 + single-line subtitle,
no eyebrow, no teal accent bar) from the earlier UI refinement pass, use that class structure here
verbatim rather than a page-specific variant.

---

## 7. Verification

- [ ] No icon appears anywhere in the stat strip.
- [ ] No section on this page has more than a plain `<h2>` + optional one-line `<p>` above it — no
      icon squares, no "PLATFORM" eyebrow, anywhere.
- [ ] Pending approvals shows the warn left-bar only when the list is non-empty; confirm the
      empty-list rendering explicitly (don't just assume it).
- [ ] Exactly one `.btn-primary` on the page (Approve).
- [ ] No underlined links at rest anywhere in the tables; hover-only underline confirmed.
- [ ] Account control has visible focus and hover states and reads as a button, not a field —
      verify with a screen reader label or `aria-label` if the icon-only chevron needs one.
- [ ] Tab through the whole page keyboard-only; every control reachable, all focus rings identical
      (`2px solid var(--fo-text-hi)`).
- [ ] `dotnet test` passes — no C# business logic should be touched by this change.
- [ ] Grep for the now-unused icon-square/eyebrow CSS and any dead stat-tile rules; remove them
      rather than leaving orphaned classes.
