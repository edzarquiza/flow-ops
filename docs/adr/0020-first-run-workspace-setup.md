# 0020. First-run workspace setup panel — derived state, not persisted

## Status
Accepted

## Context
A newly registered organization has an Admin and nothing else — `AccountService.RegisterAsync`
creates only an `Organization` and the registering user's `OrganizationMembership`; it seeds no
team, no category, and no ticket. The dashboard's existing empty states already handle this
honestly (zero KPIs, "no data yet" text), but nothing on the page told a brand-new Admin what to do
next to reach a usable workspace.

Two design questions needed a real decision: whether checklist progress should be persisted at
all, and what "invite your team" actually means given how invitations work today.

## Decision

**Derived state, not persisted state.** The checklist has no table, no entity, and no per-user
flag. `AnalyticsQueryService.GetWorkspaceSetupStatusAsync` answers three bounded existence
questions — does the organization have a team, does it have more than one active member, does it
have a ticket — freshly, on every dashboard load, scoped to the caller's current organization
exactly like every other analytics query (`ApplyAnalyticsScope`'s org-first pattern). There is
nothing to get out of sync, nothing to migrate, and switching organizations changes the answer
automatically because it is never cached anywhere. "Organization created" is not a stored fact
either — it renders as always-true because an authenticated caller is, by construction, inside one.

**"Invite your team" completes on a second *active membership*, not on sending an invitation.**
Inspecting `InvitationService` confirmed `CreateInvitationAsync` only ever writes an `Invitation`
row; the only place `OrganizationMemberships` gains a row is `AcceptCoreAsync`, reached solely
through `AcceptForCurrentUserAsync`/`AcceptForNewUserAsync` when the invited person actually
accepts. So this checklist item is genuinely dependent on someone joining, not on the Admin having
clicked "Invite" — that is correct product behavior (an unaccepted invitation has not yet grown the
team), not a bug to route around by counting pending invitations instead.

**No team-creation page exists yet, so "set up your first team" links to the existing `/Admin`
placeholder rather than a new page.** Inspection found no Team/Category CRUD anywhere in the
codebase — `Pages/Admin/Index.cshtml` is a literal placeholder ("team, category, and SLA management
belong to later phases"). Building real team management is a materially larger feature than this
onboarding panel and was explicitly out of scope for it (confirmed with the user rather than
assumed); the checklist item still renders honestly (an incomplete step with a link), it simply
does not yet lead somewhere actionable — the same honesty the rest of this feature insists on for
empty dashboard data applies here too.

**Reuses the workflow rail's marker vocabulary instead of inventing checkmark glyphs.** A solid
`--fo-teal` dot means done, a hollow `--fo-line-hi` ring means not done — the same shape/colour
pairing `.rail`'s own stops already use for "passed" vs. "ahead," so the app does not carry two
different visual languages for the same "this stage is done" concept. Shape carries the state
(never colour alone); each row also carries explicit text.

**Exactly one `.btn-primary`, on the first incomplete step.** The dashboard's own one-primary-
button rule is applied here rather than exempted: every other action button is the plain `.btn`
(ghost) style already used everywhere else in the app.

**Short-circuited for the demo organization and for non-Admins, before any query runs.** The demo
organization is always fully seeded and would fail every checklist item on its own merits — but the
demo dashboard is also the single most-trafficked page in this deployment, so
`Pages/Index.cshtml.cs` checks `DemoOptions.Enabled && OrganizationName == DemoDataSeeder.OrganizationName`
and skips calling `GetWorkspaceSetupStatusAsync` entirely rather than running three queries whose
answer is thrown away. Only an Admin can act on any setup item, so a non-Admin gets the same
short-circuit.

## Alternatives considered
- **A persisted `Onboarding`/checklist-progress entity or per-user dismissal flag**: rejected.
  Existing domain state already answers every checklist question; a second, persisted copy of
  "have they done X yet" could drift from the real answer (e.g. a team later deleted, a member
  later removed) and would need its own migration, authorization, and organization-isolation story
  for no benefit over just asking the real data.
- **A product-tour / wizard / modal with Next-Back and a completion celebration**: rejected outright
  by this feature's own scope — FlowOps teaches itself through good empty states, not a guided tour.
- **Building real Team/Category creation now so "set up your first team" has somewhere real to
  go**: rejected as out of scope for this panel; a materially larger feature, deferred by explicit
  choice rather than by oversight.
- **Counting pending invitations toward "invite your team"**: rejected — would let the checklist
  claim a team is staffed before anyone has actually joined it, which is the specific "quietly
  redefine the completion rule" this ADR's own inspection was written to avoid.

## Consequences
- No database migration. `GetWorkspaceSetupStatusAsync` reads only existing tables (`teams`,
  `tickets` joined to `teams` for the organization boundary, `organization_memberships` joined to
  `users` for the active-member check), each capped to what the question actually needs (the
  member check is `Take(2)` before `CountAsync` — the caller only needs to know "more than one,"
  never the true count).
- `AnalyticsQueryService` gains one new method; no new module, no `IOnboardingService`, no generic
  onboarding abstraction.
- `Pages/Index.cshtml.cs` gains a `DemoOptions` dependency (already registered) and a
  `WorkspaceSetupStatus?` property; every other dashboard section is unchanged.
- The "set up your first team" step is currently a dead end in practice (`/Admin` is a placeholder)
  until team management is actually built — a known, accepted gap, not a hidden one.

> **Superseded in part by [ADR-0026](0026-onboarding-skip-and-next-step-navigation.md).** Once
> real Team/Project management existed, the product owner explicitly reversed this ADR's
> "invitation must be accepted, not merely sent" rule and added persisted per-organization skip
> state for the invite/project steps, plus contextual "next step" links on the relevant pages.
> This ADR's original reasoning is left as-written above for the historical record.
