# FlowOps — Consolidate UI components and apply app-wide

Presentation-layer only. No domain logic, no business rules, no migrations.

## Why this prompt exists

The last two rounds of UI fixes (page-header pattern, stat strip, button hierarchy, link
underlines) were applied to specific pages — Dashboard, Work Queue, Platform Administration. Two
pages I hadn't looked at yet, **User Detail** and **Organization Detail**, still have the *exact
same* problems those fixes were meant to solve: icon-square + eyebrow + heading chrome, icons above
KPI numbers, always-on underlined links. The fixes clearly landed per-instance, not as a shared
component — which means every future page reintroduces the same violations until someone happens
to screenshot it.

**This pass is about closing that gap permanently, not fixing two more pages.** Every pattern below
must become one shared partial or one shared CSS class, then get applied to every page in the app
that matches it — not just the ones named here.

---

## Step 0 — Inspect and enumerate

1. List **every route** in `FlowOps.Web/Pages/**` — not just the ones mentioned in this prompt.
   Report the full list before changing anything.
2. For each pattern in §1–5 below, `grep` the whole `Pages/` tree for its current markup/CSS
   (icon-square section headers, per-stat icons, unstyled `<a>` underlines, etc.) and report every
   file where it appears. Do not assume it's confined to the pages you've seen screenshots of.
3. Confirm whether any of the earlier fixes (page-head pattern, `.stat-strip`, `.q-row`,
   `.btn-primary`/`.btn-ghost`/`.btn-caution`) already exist as shared partials/classes, or whether
   they were written inline per-page. **If inline, that's the root cause — consolidate first.**

---

## 1. Section heading — one partial, used everywhere

Confirmed on: Platform Administration, User Detail ("Organization memberships", "Recent activity",
"Lifecycle"), Organization Detail ("Overview", "Rename", "Pending invitations", "Recent activity",
"Lifecycle"). Almost certainly elsewhere too.

Create one partial — `_SectionHeading.cshtml` or equivalent — and replace every icon-square +
uppercase-eyebrow + heading instance in the app with it:

```html
<!-- _SectionHeading.cshtml -->
@model (string Title, string? Subtitle)
<h2 class="section-head">@Model.Title</h2>
@if (Model.Subtitle is not null)
{
    <p class="section-sub">@Model.Subtitle</p>
}
```

```css
.section-head { margin: 0 0 4px; font-size: 15px; font-weight: 500; color: var(--fo-text-hi); }
.section-sub  { margin: 0 0 14px; font-size: 12.5px; color: var(--fo-text-3); }
```

No icon, ever, on a section heading. No eyebrow label repeating the enclosing page's context
("PLATFORM" on every single section of every admin page adds nothing — delete it wherever it
appears, not just where already flagged). Delete every ad hoc icon currently used for this purpose
— none of them belong to the five-icon nav family, and this pattern should never need an icon at
all going forward.

**Grep for every remaining eyebrow label** (`PLATFORM`, `WORK`, `ATTENTION`, `ACCOUNT`,
`ORGANIZATION`, `MEMBERS`, or any other all-caps label sitting above a heading) and remove it,
using this partial in its place.

---

## 2. Stat strip — one shared component, no icons, anywhere

Confirmed on: Platform Administration overview, Organization Detail overview (Members/Teams/
Projects/Tickets). Check Dashboard's KPI grid too — it should be the same component, not a fourth
near-identical implementation.

```html
<!-- _StatStrip.cshtml -->
@model IEnumerable<(string Label, string Value, string? Caption, bool Highlight)>
<div class="stat-strip">
  @foreach (var s in Model)
  {
      <div class="stat-strip__item">
        <div class="stat-strip__label">@s.Label</div>
        <div class="stat-strip__value @(s.Highlight ? "stat-strip__value--teal" : "")">@s.Value</div>
        @if (s.Caption is not null) { <div class="stat-strip__caption">@s.Caption</div> }
      </div>
  }
</div>
```

```css
.stat-strip { display: flex; border-top: 1px solid var(--fo-line-row); border-bottom: 1px solid var(--fo-line-row); margin-bottom: 36px; }
.stat-strip__item { flex: 1; padding: 16px 20px; border-left: 1px solid var(--fo-line-row); }
.stat-strip__item:first-child { padding-left: 0; border-left: none; }
.stat-strip__item:last-child  { padding-right: 0; }
.stat-strip__label { font-size: 10.5px; letter-spacing: 0.08em; text-transform: uppercase; color: var(--fo-text-3); margin-bottom: 7px; }
.stat-strip__value { font-size: 30px; font-weight: 500; color: var(--fo-text-hi); letter-spacing: -0.03em; line-height: 1; font-variant-numeric: tabular-nums; }
.stat-strip__value--teal { color: var(--fo-teal); }
.stat-strip__caption { font-size: 11.5px; color: #5a6b6e; margin-top: 6px; }
```

Every KPI/count group in the app — org-level, platform-level, dashboard — renders through this one
partial. No icons above any number, anywhere, full stop. If you find a fourth or fifth ad hoc stat
block anywhere else in the app, consolidate it into this too and report where it was.

---

## 3. Links — underline on hover only, everywhere, no exceptions

Confirmed still always-on at rest: organization name links (list and detail), the org-membership
link on User Detail, and likely every teal text link in the app that hasn't been touched yet.

```css
a.text-link, .table a { color: var(--fo-teal); text-decoration: none; }
a.text-link:hover, .table a:hover { text-decoration: underline; }
```

Apply this as a **global rule for teal inline links inside content**, not a per-page override —
that's exactly the kind of fix that keeps getting reapplied one page at a time. If there's a
reason a specific link needs a different treatment, name it explicitly; otherwise every teal link
in the app follows this one rule.

---

## 4. Destructive/lifecycle actions get the caution treatment

Confirmed: "Deactivate user" and (from the org detail page) "Deactivate organization" currently
render as plain `.btn-ghost` — identical to routine actions like "Save name". These are
consequential, hard-to-reverse account-lifecycle actions and should read that way:

```html
<button class="btn btn-caution">Deactivate user</button>
```

`.btn-caution` already exists from the button-system work (warn-toned border/text, no fill). Grep
for every lifecycle/deactivation/removal action in the admin area — user, organization, and any
other entity that can be deactivated or removed — and apply this consistently. None of them should
be plain ghost buttons; none of them need a true danger/red treatment either, per the existing
"nothing here is a destructive delete" rule — caution is the right and only tier for these.

---

## 5. Readable measure for prose, full width for data

The Lifecycle explanation paragraphs on both User Detail and Organization Detail run the full
content width (~1300px+) at body text size, well past a comfortable reading line length.

```css
.prose { max-width: 720px; line-height: 1.6; color: var(--fo-text-2); font-size: 13.5px; }
```

Apply `.prose` to explanatory paragraph text specifically — the Lifecycle descriptions, and any
similar explanatory copy elsewhere in the admin area. **Do not** apply it to tables, stat strips, or
anything tabular — those should stay full width. This is specifically about long sentences of body
copy, which is a narrow, identifiable category; grep for candidate paragraphs rather than guessing.

---

## 6. Empty-state consistency check

"No platform-administration activity recorded for this user" and "No pending invitations for this
organization" both use a dashed-border empty-state box. Confirm these render through the **same**
existing `.empty-state` component already used elsewhere in the app (dashboard's "no work is at
risk" message, etc.) rather than two independently-styled boxes that happen to look similar. If
they're separate implementations, consolidate them into one.

---

## 7. Everything from the two earlier prompts, applied as components, not instances

The Work Queue row pattern (`.q-row`), the page-header pattern (`.page-head`), and the
primary/ghost/caution button system were specified as CSS/markup in earlier prompts. Confirm each
one is implemented as a single reusable partial/class, then **grep the entire `Pages/` tree and
apply it everywhere the underlying pattern applies** — not just Work Queue, Dashboard, and Platform
Administration. In particular: check every admin sub-page (User Detail, Organization Detail, and
any others not yet reviewed) against the full checklist from both prior prompts, not just the items
called out in this one.

---

## 8. Verification

- [ ] Every route enumerated in Step 0 has been checked against §1–5, not just the pages named in
      this prompt.
- [ ] Zero icon-square + eyebrow section headers remain anywhere in the app. Grep confirms it.
- [ ] Zero stat numbers have an icon above them anywhere in the app.
- [ ] Zero always-on-underlined links remain inside table rows or content areas.
- [ ] Every deactivate/remove/lifecycle action uses `.btn-caution`, none use `.btn-ghost` or
      `.btn-primary`.
- [ ] No `.prose` block is wider than 720px; no table or stat strip is constrained by it.
- [ ] `dotnet test` passes.
- [ ] Grep for and delete every now-orphaned CSS rule and unused partial from the pre-consolidation
      versions of these components. Report what was removed.

## Final output

The full route list from Step 0 · every file changed, grouped by which shared component it now
uses · what was consolidated vs. newly created · anything found that didn't fit cleanly into one of
these patterns, flagged rather than forced in.
