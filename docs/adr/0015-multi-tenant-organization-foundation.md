# 0015. Multi-Tenant Organization Foundation

## Status
Accepted (foundation only — see Consequences for what is explicitly deferred).

## Context
FlowOps was single-tenant by construction: `TicketAccessPolicy`'s team/role scoping (AUTH-RULE-02)
assumed every user, team, category, project, and ticket belonged to the same implicit organization.
Phase 16 requires FlowOps to host multiple unrelated organizations in one deployment, with data
never leaking across them, while a user may belong to more than one organization with a
**different role in each** — which immediately rules out keeping role on `ApplicationUser`, since
role would then no longer be a single fact about a user.

Inspection of the existing model (pre-Phase-16) found:
- `TicketAccessPolicy.CanView`/`CanTransition`/`CanClose`/`CanReopen`/`GetAnalyticsScope` all treat
  `UserRole.Admin` as an **unconditional, global bypass** of every team check — there was no concept
  an Admin's reach could be bounded by.
- `Team` and `Project` carried no tenant/scope concept at all (`Project.Name` even had a single
  global unique index); `Category` was already team-scoped (inherits scope transitively);
  `SlaConfiguration` was, and remains, genuinely global reference data.
- `Ticket` carries `TeamId` but no scope column of its own.
- `CurrentUserAccessor` derived role from ASP.NET Core Identity's own per-user role assignment
  (`UserManager.GetRolesAsync`) — a per-user, not per-relationship, fact.
- `TicketService.MutateAsync` loads a ticket by id with **no scoping filter at all** before handing
  it to `TicketAccessPolicy` — the single load point every workflow transition goes through.

## Decision
**Organization and OrganizationMembership are Domain entities**, in `FlowOps.Domain.Organizations`,
following the exact shape of the existing `Team`/`TeamMember` pair (plain reference data, no
aggregate behavior, no dependency on Identity or EF). `Organization` is the outer authorization
boundary; `OrganizationMembership` is the join `ApplicationUser —1:N→ OrganizationMembership —N:1→
Organization`, carrying `UserRole Role` — the single authoritative source of a user's role, now
**per organization** rather than per user. ASP.NET Core Identity's own role assignment is left in
place only as an inert mirror (no `[Authorize(Roles=...)]` attribute exists anywhere in this
codebase to serve today, so nothing currently reads it for authorization); `CurrentUserAccessor` no
longer reads it at all.

**Authorization chain** (Part D): `CURRENT USER → CURRENT ORGANIZATION MEMBERSHIP → ROLE / EXISTING
TEAM RULES → TICKET`. Concretely: `CurrentUserAccessor` resolves the caller's `OrganizationMembership`
(never trusting a client-supplied organization id — there is no client-supplied one at all in this
phase, since there is no org-switcher UI yet) and narrows `MemberTeamIds`/`ManagedTeamIds` to only
that organization's own teams, so `TicketAccessPolicy` itself needed **zero changes** — it already
only ever consults `MemberTeamIds`/`ManagedTeamIds`/`Role`, which are now organization-bounded by
construction. The one place `TicketAccessPolicy` could never have enforced this itself is Admin's
unconditional bypass, which by design ignores team membership entirely — so the organization filter
is applied **explicitly, first, unconditionally** (before any role-based narrowing) at every query
and mutation entry point: `TicketQueryService.ApplyViewScope`, `AttentionQueryService.ApplyViewScope`,
`AnalyticsQueryService.ApplyAnalyticsScope`, and `TicketService.MutateAsync`'s ticket load. This
mirrors the existing `ApplyViewScope`/`CanView` "restated as SQL, kept honest by tests" pattern
already used for team scoping — organization scoping gets the identical treatment, not a new,
different mechanism (e.g. no EF Core global query filters, which would apply the same rule
implicitly and less auditable than an explicit predicate at each entry point).

**Current organization representation** (Part C): there is no organization-switcher UI in this
phase, so "current organization" is resolved deterministically — the caller's membership with the
lowest `OrganizationId` — inside `CurrentUserAccessor`, which already re-derives the whole
`CurrentUser` from the database on every request (never trusted from a cookie claim). Every seeded
account has exactly one membership today, so this is a no-op in practice; a genuinely
multi-membership user is a real future case this rule already handles safely, since membership
itself — never a client-supplied value — is the only source of truth for which organizations a user
may act in.

**Data ownership** (Part B):

| Entity | Classification | Reasoning |
|---|---|---|
| Organization | GLOBAL (the boundary itself) | n/a |
| OrganizationMembership | ORGANIZATION-SCOPED | join row, `OrganizationId` direct FK |
| Team | ORGANIZATION-SCOPED | new direct `OrganizationId` FK |
| TeamMember | ORGANIZATION-SCOPED (derived via Team) | unchanged; scope inherited transitively |
| Category | ORGANIZATION-SCOPED (derived via Team) | unchanged; already team-scoped |
| Project | ORGANIZATION-SCOPED | new direct `OrganizationId` FK — previously entirely global with no team relation to inherit scope from |
| SlaConfiguration | GLOBAL | deliberate — shared policy reference data, not sensitive, no requirement yet for per-org SLA customization; revisit only if that requirement becomes real |
| Ticket | ORGANIZATION-SCOPED (derived via Team, no own column) | adding a denormalized `OrganizationId` was considered and rejected — see Alternatives |
| TicketComment / TicketEvent | TICKET-SCOPED (derived) | unchanged, inherit via `TicketId` |
| Analytics / Attention (at-risk) results | DERIVED | computed from organization-scoped tickets, never persisted |
| ApplicationUser | USER-SCOPED (cross-organization) | deliberately NOT organization-scoped — a user can belong to more than one organization via separate `OrganizationMembership` rows |

**Admin's role**: Admin remains globally unscoped **within its own organization** (unchanged
behavior for every existing team), but the organization boundary now sits above that — there is no
cross-organization super-admin concept, and none was requested. This is a direct, minimal extension
of Admin's existing "ignore team scoping" behavior, not a new concept.

**"An organization must have an administrator"** (Part I): represented as nothing more than "having
an `OrganizationMembership` row with `Role = Admin` in that organization" — no separate
Owner/ownership field was added. `OrganizationMembership.Role` already carries full authority
semantics for `Admin` everywhere else in the system; a second ownership concept would duplicate that
without adding information. Enforcing "at least one Admin membership per organization" is an
application-level invariant (relevant to a future organization-creation flow, not expressible as a
simple database `CHECK` constraint), not built in this phase since organization creation itself is
out of scope.

## Alternatives considered
- **A denormalized `OrganizationId` column directly on `Ticket`**: rejected for this foundation.
  `Ticket.TeamId` already determines organization transitively and unambiguously (a ticket's team
  never changes organization — team-to-organization is a fixed 1:1, and no code path lets a ticket's
  team change to a team in a different organization); adding a second, redundant column risks the
  two disagreeing after some future edit and would touch the heavily-tested `Ticket` aggregate for
  no behavioral gain. If a real performance need for pre-filtering on `Ticket.OrganizationId`
  emerges at a scale this project does not target today, it can be added as a pure, backfillable
  denormalization later without changing the ownership model.
- **EF Core global query filters** (`HasQueryFilter`) for organization scoping: rejected in favor of
  explicit predicates at each of the four entry points (`TicketQueryService`, `AttentionQueryService`,
  `AnalyticsQueryService`, `TicketService.MutateAsync`). A global filter needs an ambient
  "current organization" resolved from something injected into the `DbContext` itself — implicit,
  harder to audit per query, and in tension with Part D's explicit requirement that authorization
  never rely on anything hidden. The existing codebase already established the "restate the scope
  as an explicit, tested SQL predicate" pattern for team scoping (`ApplyViewScope`); organization
  scoping follows the same, already-reviewed pattern instead of introducing a second mechanism.
- **Role kept on `ApplicationUser`, with a separate lightweight "organization tag"**: rejected
  outright by the requirement itself — a user with different roles in different organizations cannot
  be represented by a single per-user role field no matter how the organization association is
  modeled.
- **A generic `IOrganizationService`/`OrganizationRepository`/tenant-context-manager abstraction**:
  rejected — explicitly out of scope per this phase's own constraints, and no real architectural
  need surfaced during implementation to justify one; `CurrentUserAccessor`, three query services,
  and `TicketService` needed only small, local, explicit additions.
- **Making `Team.Name`/`Project.Name` globally unique still**: rejected — two unrelated
  organizations must be able to independently name a team "Service Desk"; uniqueness is now
  `(OrganizationId, Name)`.

## Consequences
- Every pre-existing team/project row and every pre-existing user with an Identity role assignment
  is backfilled, by the migration itself, into one "Demo Organization" — no manual data migration
  step, no hard-coded demo bypass in application code. A fresh, never-seeded database is unaffected
  (the backfill is a no-op when `teams`/`projects` are both empty); `DemoDataSeeder` creates its own
  "Demo Organization" there instead, the first time it runs.
- `Team.Name` and `Project.Name` uniqueness moved from a single global unique index to a composite
  `(OrganizationId, Name)` unique index.
- `CurrentUser` gained `OrganizationId`; `CurrentUserAccessor` no longer reads ASP.NET Core
  Identity's per-user role assignment at all.
- This phase deliberately does **not** include: organization registration, invitations, an
  organization switcher UI, email, profile/account-deletion changes, or any dashboard visualization
  work — all explicitly out of scope and not started.
- The `ticket_reference_seq` Postgres sequence remains a single global counter across all
  organizations (unchanged, out of scope for this phase) — noted here because per-organization
  reference numbering, if ever requested, would need a materially different mechanism than a single
  sequence and should not be assumed compatible with the current one.
