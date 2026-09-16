# 0023. Platform administration: a boundary above the tenant, never merged with it

## Status
Accepted

## Context
Every authorization decision in FlowOps up to this phase has been either "who am I within
`AspNetUsers`" (Identity's authentication layer) or "what is my role within this one organization"
(`OrganizationMembership.Role`, the sole authority since Phase 16/ADR-0021). Nothing in the product
has ever needed to reason about the platform as a whole — inspecting or managing organizations and
users across the tenant boundary itself.

Phase 24 introduces exactly that: a platform operator's console (`/Platform`) to list/inspect
organizations and users, and to deactivate/reactivate either. This raises four questions with no
existing precedent to reuse directly:

1. How is "this user may administer the platform" represented, given it is emphatically *not* an
   organization-scoped fact?
2. How is that authority bootstrapped without a chicken-and-egg tenant-facing UI (nothing can grant
   it before it exists, and nothing should be able to grant it from inside an authenticated session
   either — CLAUDE.md's security posture treats this as a high-value boundary)?
3. What does "deactivate an organization" actually mean operationally, given `Team`/`Category`/
   `Project` deactivation (the immediately preceding phase, ADR-0022) already established a
   "soft, non-cascading" pattern — does the same shape apply one level up?
4. What does "deactivate a user" mean here, given `AccountService.DeleteAccountAsync` (Phase 17)
   already established a deactivation recipe for self-service account deletion — is it the same
   operation, or a different one that happens to share a mechanism?

## Decision

### 1. Platform authority: a dedicated flag on `ApplicationUser`, never a role value
`ApplicationUser.IsPlatformAdmin` (a plain `bool`, defaulting `false`) — the same shape as the
already-existing `IsActive`/`IsDemoProtected` flags on that same class, not a new value added to
`OrganizationMembership.Role`. This was the one constraint stated explicitly and non-negotiably for
this phase: `OrganizationMembership.Role` stays exactly `Admin`/`Manager`/`Agent`/`Viewer`. Mixing a
platform-wide concept into a per-organization enum would let a role check in one organization
accidentally answer a question about the whole platform, or vice versa — precisely the "two
authorization scopes in one field" failure mode ADR-0021 already spent a full phase fixing for a
different pair of concepts (Identity roles vs. organization roles).

Reusing ASP.NET Core Identity's own role system (`ApplicationRole`/`WellKnownRoles`) was considered
and rejected for the same reason ADR-0021 already rejected it as an authorization source: it exists
today only as an inert mirror, read by nothing, kept only for `DemoDataSeeder`'s historical seeding.
Reviving it as live authority for a *second* concept, right after establishing that it must never be
live authority for the first, would reintroduce exactly the drift ADR-0021 eliminated.

### 2. The one authoritative check: `PlatformUserAccessor`, not a Domain policy class
`PlatformUserAccessor.GetCurrentPlatformAdminAsync` is the single place that answers "is this
caller a Platform Admin" — re-resolved from the database on every call (never trusted from a claim,
same staleness reasoning ADR-0008 already established for organization role), returning a
`PlatformAdminIdentity?` that every `/Platform` PageModel checks exactly once, `Forbid()`-ing on
`null`. This deliberately mirrors `CurrentUserAccessor`'s own shape rather than introducing a
separate `PlatformAccessPolicy` Domain class: there is no further scoping decision to make once
platform authority is established (unlike `TicketAccessPolicy`, which has to reason about team
scope, assignment, status), so a policy class here would be a wrapper around one boolean with no
business rule of its own — an abstraction CLAUDE.md's own review checklist would flag as
unjustified. `CurrentUserAccessor` and `PlatformUserAccessor` are deliberately independent classes
with no shared code: platform authority never touches `OrganizationMembership`, and organization
resolution never touches `IsPlatformAdmin`.

### 3. Bootstrap: a startup CLI command, never an HTTP endpoint
`Program.cs` gains `grant-platform-admin <email>` / `revoke-platform-admin <email>`, dispatched from
`args` exactly like the existing `init-database` command (ADR-0014) — run via `dotnet FlowOps.Web.dll
grant-platform-admin <email>` locally, `docker compose exec app ...` in Compose, or a Render shell in
production. This requires the same trust tier as running a database migration or a deployment
command; no authenticated HTTP session, however privileged within an organization, can ever reach
it. This satisfies the explicit requirement that an Organization Admin can never become a Platform
Admin merely by virtue of their organization role, and that granting platform authority is never
reachable from ordinary tenant-facing UI. `revoke-platform-admin` exists so a mistaken grant is never
a one-way door recoverable only through raw SQL.

### 4. Organization lifecycle: soft, bidirectional, non-cascading — one step beyond ADR-0022
`Organization` gains `IsActive`/`Deactivate()`/`Reactivate()`. Unlike `Team`/`Category`/`Project`
(ADR-0022: terminal, deactivation never reverses), an Organization can be reactivated — a platform
operator may need to restore access after a billing pause or a mistaken deactivation, and there is
no analogous need to prevent "un-deactivating" a team. Deactivation never touches any other row: no
membership, team, category, project, ticket, comment, or event is altered, deleted, or
reconstructed on reactivation, since none was ever removed.

The operational effect — "inaccessible for active operation, not deleted" — comes from exactly one
change: `CurrentUserAccessor.GetCurrentUserAsync` (and its `TrySwitchOrganizationAsync`/
`GetAvailableOrganizationsAsync` siblings) now join every membership query against
`Organizations.Where(o => o.IsActive)`. A membership in a deactivated organization is simply never
resolvable into a `CurrentUser` — the same organization, with the same members, teams, tickets, and
history, but with no live path for `TicketAccessPolicy`/`DirectoryAccessPolicy`/
`CatalogAccessPolicy`/`OrganizationAccessPolicy` to ever be consulted on its behalf, because nothing
ever produces a `CurrentUser` pointing at it. This was the one change to a tenant-scoped class this
phase required, and it is a narrowing (an additional filter), never a broadening — no existing
tenant authorization decision becomes more permissive.

Platform Admin itself is unaffected by this filter: `PlatformOrganizationService`/
`PlatformUserService` query `Organizations`/`Users`/`OrganizationMemberships` directly, with no
dependency on `CurrentUserAccessor` at all — the one place in the application allowed to see and
reason about a deactivated organization, by design.

**Rejected alternative**: filtering deactivated organizations out of `TicketAccessPolicy` or the
Application-layer ticket/team/category query services directly. This would duplicate the boundary
in multiple places (the exact "two sources of truth" pattern earlier phases' audits have repeatedly
flagged) and risk an inconsistent subset of pages respecting deactivation. Gating it once, at the
single point every tenant page already depends on for its own identity resolution, is the smallest
correct change.

### 5. User lifecycle: the same recipe as Phase 17's self-deletion, minus the membership removal
`PlatformUserService.DeactivateUserAsync`/`ReactivateUserAsync` reuse
`AccountService.DeleteAccountAsync`'s exact three-mechanism lockout recipe (`IsActive = false`,
`LockoutEnabled = true` + `LockoutEnd = DateTimeOffset.MaxValue`, and a fresh security stamp) — the
same reasoning applies unchanged: `IsActive` makes `CurrentUserAccessor` treat the user as
unauthenticatable for every tenant decision, the lockout makes `SignInManager.PasswordSignInAsync`
refuse credentials outright, and the security-stamp bump invalidates any other already-signed-in
session. The one deliberate difference: platform deactivation **never removes any
`OrganizationMembership` row**. Self-account-deletion is a choice the account holder makes about
their own access; platform deactivation is an operational action taken *about* someone, and their
organization history must remain exactly as coherent as it was — same roles, same teams, fully
restorable by reactivation with nothing reconstructed.

Platform user deactivation reuses `SoleAdminGuard.OrganizationsThatWouldLoseTheirLastAdminAsync`
(the same ORG-RULE-12 check `AccountService.DeleteAccountAsync` already enforces) and is **blocked**
if the target is the sole Admin of any organization. This was reasoned through explicitly rather
than assumed: allowing it would silently strand an organization with zero Admins, exactly the
outcome ORG-RULE-12 exists to prevent everywhere else in the product; a platform operator hitting
this block can still reactivate a different Admin, add one, or (if truly necessary) deactivate the
whole organization instead — all of which leave a clear, intentional trail rather than an accidental
one.

### 6. Auditability: a small, dedicated table — never forced into `TicketEvent`
`PlatformAuditEvent` (`platform_audit_events`) records the four lifecycle mutations Phase 24
introduces (`OrganizationDeactivated`/`OrganizationReactivated`/`UserDeactivated`/`UserReactivated`)
with an actor, a target (exactly one of organization/user, enforced by a check constraint), and a
timestamp. `TicketEvent` was inspected and rejected as a host for this: it carries a required
`TicketId` foreign key and is structurally a per-ticket audit trail — forcing a ticket-less platform
action into it would mean either a fake ticket reference or reshaping a schema Phase 6 designed for
a different, narrower purpose. This is not a general audit framework: it has no free-text note
field, no extensibility beyond the four events this phase needs, and a fifth platform event type
would add one enum member here, not a new table.

## Alternatives considered
- **A `GlobalAdmin` value on `OrganizationMembership.Role`**: rejected outright — this was the one
  explicitly forbidden approach; see Decision §1.
- **Reusing `ApplicationRole`/Identity roles as live platform authority**: rejected — see Decision §1.
- **A tenant-facing "promote to Platform Admin" UI, gated behind an existing Admin role**: rejected —
  this is exactly the escalation path the phase's own constraints forbid ("Organization Admin cannot
  promote another user to Platform Admin"); any UI reachable from an authenticated session is reachable
  by definition.
- **Filtering deactivated organizations inside `TicketAccessPolicy`/each Application query service
  individually**: rejected — see Decision §4's rejected alternative.
- **Hard-deleting an organization or user**: explicitly out of scope for this phase — deletion was
  never requested, and every foreign key from `Ticket`/`TicketComment`/`TicketEvent` to `Team`/
  `Category`/`Project`/`AspNetUsers` is already `RESTRICT`, so a literal delete would fail at the
  database the moment any historical record exists regardless.
- **Impersonation ("view as user") for platform troubleshooting**: explicitly out of scope —
  session takeover is a materially different security design this phase does not attempt.

## Consequences
- `ApplicationUser` gains `IsPlatformAdmin`; `Organization` gains `IsActive`/`Deactivate`/
  `Reactivate`; a new `PlatformAuditEvent` table. One migration (`AddPlatformAdministration`),
  additive only — every existing row defaults to active/non-platform-admin.
- `CurrentUserAccessor.GetCurrentUserAsync`/`TrySwitchOrganizationAsync`/
  `GetAvailableOrganizationsAsync` each gained one additional join against
  `Organizations.Where(o => o.IsActive)` — the only change to any existing tenant-scoped class this
  phase required.
- Two new, deliberately separate Application services (`PlatformOrganizationService`,
  `PlatformUserService`) and one accessor (`PlatformUserAccessor`) — no tenant-scoped service was
  merged with or extended to cover platform concerns.
- `/Platform` is a new, small Razor Pages area, shown in the sidebar only to a resolved Platform
  Admin — never gated on `OrganizationMembership`, never sharing a page with `/Admin`.
- Permanent deletion and impersonation remain explicitly out of scope, unchanged from every prior
  phase's own non-goals.
