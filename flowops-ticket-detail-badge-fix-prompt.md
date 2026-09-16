# FlowOps — Ticket Detail cleanup + status/priority badge system

Presentation-layer only. No domain logic, no business rules, no migrations.

This consolidates several related fixes into one pass because they touch the same shared
components (the status/priority badge rendering, the section-heading pattern) — fixing them
separately, page by page, is exactly how earlier passes ended up half-applied. Treat everything in
§1–2 as **one shared component change applied everywhere it's used**, not a Ticket Detail-only fix.

---

## Step 0 — Inspect first

1. Find wherever ticket status and priority currently render as badges/chips — likely a shared
   partial or tag helper, but confirm. It's used on at least: Ticket Detail, Work Queue rows, and
   possibly At-Risk. List every place it appears before changing anything.
2. Confirm whether `_SectionHeading` (or equivalent) from the earlier consolidation pass actually
   exists and is in use, or whether Ticket Detail was missed by that pass — the "JOURNEY",
   "OWNERSHIP", and "CONTENT" eyebrows below still suggest it wasn't touched.
3. Confirm the current markup for the sidebar account control at the bottom of the nav — there's a
   rendering bug (see §5) that suggests two versions of it may be present at once.

---

## 1. Status badge — solid fill, no border, no hue

Replace the current low-contrast bordered/tinted chip with a solid, high-contrast neutral pill.
This applies **only to workflow status** — it remains the one badge-shaped element allowed on
these pages, per the existing "chips reserved for workflow status only" rule.

```css
.status-badge {
  display: inline-block;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  padding: 5px 11px;
  border-radius: 3px;
  background: var(--fo-line-row);
  color: var(--fo-text-hi);
  border: none;
}
```

No color variant by status value — Open, Assigned, In Progress, Pending, Resolved, and Closed all
render with this same neutral fill. Status doesn't need hue to communicate urgency; that's what the
rail and the SLA state are for. This badge's job is presence, not alarm.

---

## 2. Priority — never a chip, always the severity mark

Wherever priority currently renders as a bordered chip (Ticket Detail header, Work Queue rows,
anywhere else found in Step 0), replace it with the height-encoded bar + text mark:

```html
<div class="severity">
  <i class="severity__bar"></i>
  <span class="severity__label">Medium priority</span>
</div>
```

```css
.severity { display: flex; align-items: center; gap: 8px; }
.severity__bar { width: 3px; border-radius: 1px; display: inline-block; }
.severity__label { font-size: 14px; }

.severity--critical .severity__bar { height: 16px; background: var(--fo-danger); }
.severity--critical .severity__label { color: var(--fo-danger); font-weight: 500; }

.severity--high .severity__bar { height: 13px; background: var(--fo-warn); }
.severity--high .severity__label { color: var(--fo-warn); font-weight: 500; }

.severity--medium .severity__bar { height: 12px; background: #5a6b6e; }
.severity--medium .severity__label { color: var(--fo-text); font-weight: 500; }

.severity--low .severity__bar { height: 8px; background: #3E5457; }
.severity--low .severity__label { color: var(--fo-text-3); font-weight: 400; }
```

Medium and Low get the emphasized weight/size treatment agreed on — solid presence, no color.
Only Critical and High spend the system's actual alarm colors. This is the exact same rule as the
status badge: stand out through weight and contrast, not hue, unless the value genuinely demands a
decision.

**Grep every place priority currently renders as a bordered chip and convert all of them in this
pass** — this was found on both Ticket Detail and Work Queue rows already; there are likely more.

---

## 3. SLA state — plain colored text, not a chip

The SLA status ("Breached", "At risk", "Within", "Paused") should never be a bordered badge either
— only workflow status gets chip/pill treatment. Render it as plain text in the appropriate
semantic color:

```css
.sla-state--breached { color: var(--fo-danger); }
.sla-state--at-risk   { color: var(--fo-warn); }
.sla-state--paused    { color: var(--fo-info); }
.sla-state--within    { color: #8A9A9C; }
```

```html
<span class="sla-state sla-state--breached">Breached</span> — 89d 13h over
```

---

## 4. Ticket Detail — zone SLA and Ownership into panels

Currently both render as flat stacks of full-width label/value rows. Group each into a bordered
panel, side by side:

```html
<div class="ticket-zone-grid">
  <div class="panel">
    <h3>SLA</h3>
    <div class="sla-state sla-state--breached" style="font-size:14px;margin-bottom:8px;">Breached — 89d 13h over</div>
    <div class="panel__meta">
      Due <span class="panel__value mono">2026-06-18 11:56</span><br>
      Target <span class="panel__value">1440 minutes</span> · Paused <span class="panel__value">0 minutes</span>
    </div>
  </div>
  <div class="panel">
    <h3>Ownership</h3>
    <div class="panel__meta">
      Requester <span class="panel__value">Emerson Delgado</span> · Assignee <span class="panel__value panel__value--muted">Unassigned</span><br>
      Team <span class="panel__value">Service Desk</span> · Category <span class="panel__value">Service Desk Requests</span><br>
      Project <span class="panel__value">Customer Portal</span>
    </div>
  </div>
</div>
```

```css
.ticket-zone-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 28px; }
@media (max-width: 768px) { .ticket-zone-grid { grid-template-columns: 1fr; } }

.panel { border: 1px solid var(--fo-line); border-radius: var(--fo-radius); padding: 16px; }
.panel h3 { font-size: 10.5px; color: var(--fo-text-3); text-transform: uppercase; letter-spacing: 0.06em; margin: 0 0 10px; font-weight: 500; }
.panel__meta { font-size: 12.5px; color: var(--fo-text-2); line-height: 1.9; }
.panel__value { color: var(--fo-text); }
.panel__value--muted { color: var(--fo-text-3); }
.panel__value.mono { font-family: var(--fo-mono); }
```

If a `.panel` class already exists from earlier work (dashboard cards, etc.), reuse it rather than
creating a second near-identical version — check before adding this.

**Rail width:** contain it rather than letting it stretch full page width — cap at roughly 820px so
the connector lines between stops don't read as sparse on wide viewports. Confirm the current
breached stop has its glow (`box-shadow` at ~15–18% alpha in the danger color) — it should be there
per the rail spec; verify it's actually rendering, not just present in CSS.

---

## 5. Section headings — apply the existing pattern here too

"JOURNEY", "OWNERSHIP", and "CONTENT" eyebrows on Ticket Detail need to go, replaced by the same
plain-heading pattern used elsewhere (`_SectionHeading` or equivalent, from the earlier
consolidation pass). If that partial exists, use it. If it doesn't actually exist yet and the
earlier pages were fixed inline instead, **stop and tell me** — that's the same root-cause problem
as before and needs fixing at the component level, not page by page again.

---

## 6. Sidebar account control — fix the rendering duplication

There's a truncated text label (e.g. "service.desk.m…") rendering directly above the avatar/name
control at the sidebar bottom, and the role is showing as an uppercase bordered button
("SERVICE DESK MANAGER") rather than plain text. This looks like two versions of the account
control are rendering at once — likely a leftover from before the avatar+chevron control was added.
Find and remove the duplicate; the final control should be exactly one element: avatar, name/email,
role as plain dim text, chevron. No bordered role button.

---

## 7. Formatting fix

`ServiceRequest` renders as one unspaced word in the Ticket Detail subtitle — it's the raw enum
value. Humanize it ("Service Request") wherever work-type values are displayed as text. Check
whether this is a display-formatting gap (missing a space-before-capitals humanizer) affecting other
enum-backed labels in the app, not just this one field.

---

## 8. Verification

- [ ] Status badge is solid-fill, no border, identical treatment regardless of status value, on
      every page it appears (Ticket Detail, Work Queue, At-Risk if applicable).
- [ ] Zero bordered priority chips remain anywhere in the app — confirm via grep, not just the two
      pages already found.
- [ ] Zero bordered SLA-state chips remain; SLA state is plain colored text everywhere.
- [ ] SLA and Ownership render as two side-by-side bordered panels on Ticket Detail, collapsing to
      one column at 768px.
- [ ] No eyebrow labels remain on Ticket Detail.
- [ ] Sidebar account control renders as a single element — no duplicate text, no bordered role
      button.
- [ ] Work-type and any other enum-backed display text is humanized (spaced), not raw PascalCase.
- [ ] Tab through Ticket Detail keyboard-only — focus rings present and consistent.
- [ ] `dotnet test` passes.
- [ ] Grep for and remove now-orphaned CSS from the old chip styles for priority and SLA state.

## Final output

Every file changed · confirmation that `_SectionHeading` either already existed or was created now
(and if the latter, note that as a gap in the prior consolidation pass) · everywhere the
priority/SLA chip conversion was applied, listed explicitly · what caused the sidebar account
control duplication.
