# FlowOps — First-Run Workspace Setup

Approved scope addition. Build a compact, state-derived setup panel on the Dashboard that helps a
newly registered Admin reach a usable workspace. Follow CLAUDE.md and the existing architecture.

**Not a product tour.** No wizard, no modal, no overlay, no spotlight, no "Next/Back", no carousel,
no progress percentage, no gamification, no badges, no confetti, no celebratory completion state, no
marketing hero. FlowOps teaches itself through good empty states. This single paragraph is the whole
prohibition — it is not repeated below, and it is absolute.

---

## 0. Sequencing — read this before starting

This feature adds UI to the Dashboard, which is also the subject of the in-flight **UI refinement
pass** (page-header pattern, container discipline, two-line rows, one-primary-button rule).

**Check whether that refinement has landed before you build.** If it has not:

- Build the setup panel against the **target** design described in §5 below, not against whatever is
  currently in `flowops.css`.
- Tell me which state you found, so I know whether this panel will need a second pass.

Do not build onboarding UI against the old header/panel styles. It will be reworked immediately and
that's wasted effort.

---

## 1. Product intent

Help a new Admin answer: *"My organization exists. What now?"*

The panel guides toward four practical states — organization exists, first team, at least one other
member, first ticket. It does **not** explain SLA, attention signals, analytics, or workflow. Those
are learned by using the product.

---

## 2. Derived state, not persisted state

**Inspect first and report back before writing code.** Determine whether existing data already
answers the checklist: `Organization`, `OrganizationMembership`, `Team`, `Ticket`, current-organization
resolution, current user/role, the registration flow, the Members/invitation flow, and the dashboard's
existing view models and services.

The workspace is the source of truth. **Do not create an Onboarding entity or table** to remember
checklist completion if existing domain state is sufficient. If you conclude persistence is genuinely
necessary, **stop and explain why** before introducing it.

### Checklist semantics

| Item | Complete when | Action when incomplete |
|---|---|---|
| Organization created | Always (the user is inside one) | — |
| Set up your first team | Current org has ≥ 1 team | Link to existing team setup |
| Invite your team | Current org has > 1 active membership | Link to existing Members/invite flow |
| Create your first ticket | Current org has ≥ 1 ticket | Link to existing Create Ticket |

**Open question to resolve during inspection:** the "invite" item completes on a *second active
membership*, but email delivery is out of scope. Check how the existing Members flow actually works —
if it creates a pending invitation rather than an active member, this item may be uncompletable in
practice. Report what you find before implementing; do not quietly redefine the completion rule to
make it work.

**All counts scoped to the current organization.** Never read another org's data. Never accept an
organization ID from a form, query string, or hidden field — the existing organization-context
mechanism stays authoritative. Never query all organizations and filter in memory.

---

## 3. Who sees it

- **Admin of a real organization** with incomplete items → sees the panel.
- **All items complete** → panel is gone. No completion message, no "nice work", nothing. The normal
  dashboard simply takes over.
- **Non-admin** → does not see Admin-only setup actions. Server-side authorization, not CSS hiding.
- **Demo organization** → never sees it. Inspect `Demo:Enabled` and the existing demo protection. The
  seeded demo org has teams, members, and hundreds of tickets, so it should fail the checklist
  naturally — but verify that, don't assume. Do not modify seed volumes to accommodate this feature.

Dashboard only. Not on Work Queue, At-Risk, Ticket Detail, Members, or Settings.

---

## 4. Dismissal — default to none

The panel removes itself as real work gets done. That is the dismissal mechanism.

**Do not build a dismiss button that requires persistence.** Do not create a preferences framework
for one boolean. If you find an existing, clean per-user-per-organization preference store during
inspection, mention it — but the default answer is no dismissal at all.

**Consequence for copy:** drop the line *"You can skip these steps and come back later."* If there's
no skip affordance, that sentence is false. The panel is compact enough to ignore, and the steps are
things the Admin needs to do anyway.

---

## 5. UI — align to the existing design system

This is the section that matters most for consistency. The panel must read as part of FlowOps, not a
bolted-on onboarding widget.

### Reuse the rail's vocabulary instead of inventing checkmark glyphs

FlowOps already has a visual language for "this stage is done / this stage is ahead": the rail's
stops. A solid dot means passed, a 1.5px ring means ahead, and teal means resolved. **Use that same
vocabulary for checklist markers** rather than introducing `✓` and `○` glyphs, which would be a
second, competing iconography for the same concept.

- Complete → 10px solid dot in `--fo-teal`
- Incomplete → 10px ring, 1.5px `--fo-line-hi`, no fill

Shape carries the state, not colour — which satisfies the never-colour-alone rule — and each row
also carries explicit text (`Done`, or an action button), so nothing depends on the marker.

### One primary button, and it's the next incomplete step

The dashboard rule is one teal button per screen: the action that advances work. Apply it here —
**the first incomplete item gets `.btn-primary`; every other action is `.btn-ghost`.** Three teal
buttons stacked down the panel would break the rule and flatten the hierarchy the rule exists to
create.

Verify visually that teal completed-markers plus one teal button doesn't over-spend the accent. If it
reads as too much teal, make the primary button ghost too and let the panel have no primary — that's
an acceptable outcome for a setup panel.

### Markup and styling

```html
<section class="setup" aria-labelledby="setup-heading">
  <h2 id="setup-heading" class="setup__title">Welcome to @Model.OrganizationName</h2>
  <p class="setup__sub">Your workspace is ready. A few steps will get your team running.</p>

  <ul class="setup__list">
    <li class="setup__item is-done">
      <span class="setup__marker" aria-hidden="true"></span>
      <span class="setup__label">Organization created</span>
      <span class="setup__state">Done</span>
    </li>
    <li class="setup__item">
      <span class="setup__marker" aria-hidden="true"></span>
      <span class="setup__label">Set up your first team</span>
      <a class="btn btn-primary btn-sm" href="/Teams/Create">Set up</a>
    </li>
    <li class="setup__item">
      <span class="setup__marker" aria-hidden="true"></span>
      <span class="setup__label">Invite your team</span>
      <a class="btn btn-ghost btn-sm" href="/Members">Invite</a>
    </li>
    <li class="setup__item">
      <span class="setup__marker" aria-hidden="true"></span>
      <span class="setup__label">Create your first ticket</span>
      <a class="btn btn-ghost btn-sm" href="/Tickets/Create">Create</a>
    </li>
  </ul>
</section>
```

```css
.setup {
  border: 1px solid var(--fo-line);   /* earns a border: it IS a distinct object */
  border-radius: var(--fo-radius);
  padding: 20px 22px;
  margin-bottom: 32px;                /* region separation, per the spacing scale */
  max-width: 640px;                   /* a checklist doesn't need 1400px */
}
.setup__title {
  margin: 0 0 5px;
  font-size: 16px;                    /* h2 — must not compete with the page h1 at 24px */
  font-weight: 500;
  color: var(--fo-text-hi);
  letter-spacing: -0.01em;
}
.setup__sub { margin: 0 0 18px; font-size: 13px; color: var(--fo-text-3); }

.setup__list { list-style: none; margin: 0; padding: 0; }
.setup__item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 9px 0;
  border-bottom: 1px solid #141F21;
}
.setup__item:last-child { border-bottom: none; }

.setup__marker {
  width: 10px; height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
  border: 1.5px solid var(--fo-line-hi);   /* incomplete: ring */
}
.setup__item.is-done .setup__marker {
  background: var(--fo-teal);               /* complete: solid, matching the rail's resolved stop */
  border-color: var(--fo-teal);
}

.setup__label { flex: 1; min-width: 0; font-size: 13.5px; color: var(--fo-text); }
.setup__item.is-done .setup__label { color: var(--fo-text-3); }
.setup__state { font-size: 12px; color: var(--fo-text-3); }

.btn-sm { padding: 6px 13px; font-size: 12.5px; }
```

### Heading hierarchy

The page `<h1>` stays "Operations dashboard". The panel is an `<h2>`. Do not promote "Welcome to
{Org}" to `<h1>` — it would break the heading order and make the dashboard title inconsistent
between new and established organizations.

### Also

- No eyebrow label above the panel heading, no teal accent bar, no icon-in-a-rounded-square. Those
  are being removed elsewhere in the app; don't reintroduce them here.
- Focus rings: `2px solid var(--fo-text-hi)`, `outline-offset: 2px` — identical to every other
  interactive element. Never vary focus colour by component.
- Works fully with JavaScript disabled. Every action is a plain link to an existing page.
- No horizontal overflow at tablet width; the panel stacks naturally at its 640px cap.

---

## 6. Empty dashboard — the honesty trap

A brand-new organization has no tickets, so the existing analytics must not fabricate anything. This
is the part most likely to go wrong quietly:

- **Open Work / Overdue → `0`.** Truthful, fine as-is.
- **SLA Compliance → `—`, not `100%` and not `0%`.** With zero resolved tickets the figure is
  undefined, and `100%` is a lie that flatters. Show a dash with "No resolved work yet" beneath.
- **Average Resolution Time → `—`**, same reasoning.
- **Attention panel → "Nothing needs attention yet."**
- **Workload table → "No open work assigned."**

Never seed a real organization automatically. Never reuse demo data for a real organization.

---

## 7. Architecture

`Web → Application → Infrastructure → Domain`. PageModel stays thin: call an application service,
receive a view model, render.

The setup state is trivial query composition, so put it in the existing dashboard application service
as an additional projection — **do not create an `IOnboardingService`**, a generic onboarding
abstraction, or a new module. No MediatR, no repository, no unit of work, no event dispatcher, no
background job, no cache, no new NuGet package, no JavaScript framework.

**Queries:** three bounded scalar existence checks scoped to the current organization —
`AnyAsync()` for team and ticket, a `CountAsync()` capped at 2 for active memberships (you only need
to know whether it exceeds 1, not the true count). No loading collections to count them in memory. No
N+1. Review the generated SQL and report it. Add an index only if the actual query shape demands one
that doesn't exist — no speculative indexes.

**Short-circuit:** skip the setup queries entirely when the org is the demo org, or when the user
isn't an Admin. Don't run three queries per dashboard load for users who will never see the panel.

---

## 8. Tests

1. New org (no team, one member, no tickets) → all applicable items incomplete.
2. Org with a team → team item complete.
3. Org with a second active member → invite item complete.
4. Org with a ticket → ticket item complete.
5. Fully configured org → panel not returned/rendered at all.
6. **Cross-org isolation** — Org A has team/member/ticket, Org B has none; a user in Org B sees Org
   B's state.
7. Organization switching → state follows the selected organization.
8. Demo org with `Demo:Enabled` → panel does not appear.
9. Non-admin → cannot receive or invoke Admin-only setup actions via onboarding.
10. Registration, invitation, ticket creation, and dashboard analytics behaviour all unchanged.
11. Empty-org dashboard renders `—` for SLA compliance and average resolution time, not `100%`.

If persisted dismissal ends up being introduced (it shouldn't be), add tests for persistence,
authorization, organization isolation, and account-lifecycle behaviour.

**Account lifecycle:** onboarding state must never become user-global when it is organization-specific.
Do not alter existing account deletion/deactivation behaviour.

---

## 9. ADR

Warranted — this is a product-scope decision under CLAUDE.md §20. Add the next ADR:

- **Context:** new organizations need a clear first-run path.
- **Decision:** compact, dashboard-based, state-derived workspace checklist.
- **Alternatives:** multi-step product tour; persisted onboarding progress entity; blocking setup
  wizard.
- **Consequences:** simpler, truthful, organization-scoped, self-completing as real work happens, no
  duplicate state to drift.

Update CLAUDE.md only where the product contract actually changed. No empty documentation.

---

## 10. Validation gate

Run and **paste actual output** — never claim a pass without it:

```
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
```

Plus an EF pending-model-changes check if anything touched the schema (it shouldn't have).

**Live UI verification** for: brand-new org · org with a team · org with an invited member · org with
a ticket · fully configured org · switching between two orgs · demo persona · non-admin user.

Screenshots if the environment supports it. If Docker or a headless browser isn't available, say so
plainly and describe what you verified and how — do not silently skip this step or imply screenshots
exist when they don't.

---

## 11. Final self-check

1. Did we persist onboarding state unnecessarily?
2. Did we duplicate an existing business rule?
3. Can one organization see another's setup state?
4. Can a client-supplied organization ID influence the result?
5. Did demo, registration, invitation, ticket authorization, or dashboard analytics behaviour change?
6. Any new dependency or unnecessary abstraction?
7. Does the panel use the rail's marker vocabulary rather than a second icon language?
8. Is there exactly one primary button on the dashboard?
9. Does the panel disappear cleanly — with no completion message — once the workspace is operational?
10. Does it work with JavaScript disabled?
11. Would a service desk technician understand it immediately?

---

## Final output

What you inspected · the design decision (especially: derived vs persisted, and what you found about
the invite-completion question in §2) · files changed · whether schema changed · whether an ADR was
added · tests added · actual command output · live verification results · PASS/FAIL · carry-forward
risks.
