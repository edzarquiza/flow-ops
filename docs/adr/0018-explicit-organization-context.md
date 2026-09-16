# 0018. Explicit organization context and switching

## Status
Accepted

## Context
Phase 16 resolved "current organization" deterministically — the caller's membership with the
lowest `OrganizationId` — because no switcher existed yet and every seeded account had exactly one
membership. Phase 18 made that assumption false in practice: an existing user can now accept an
invitation into a second organization, and if that organization happens to have a lower id than
their first, it silently becomes "current" on their very next request, with no action by the user.
Phase 19 replaces the deterministic pick with an explicit, user-chosen, persisted selection —
without ever letting that selection substitute for the real authorization check
(`OrganizationMembership`) it has always required.

## Decision
**Storage: a small, separate, Data-Protection-protected cookie** (`FlowOps.CurrentOrganization`),
read and written entirely inside `CurrentUserAccessor` — the one class that already owns
organization-context resolution. Concretely:
- **Not a claim in the ASP.NET Core Identity authentication cookie.** A claim only changes when the
  authentication ticket is reissued (`SignInManager.RefreshSignInAsync`), which would make every
  switch as heavyweight as a password change; a separate cookie is written and read independently,
  with no interaction with the sign-in ticket at all.
- **Not a new column on `ApplicationUser`.** A user's current organization is not a fact about their
  identity — it is a fact about their *session*, and a single "current org" column could not
  represent per-browser or per-session context even if it were otherwise appropriate (and Step 25
  explicitly rejected this for the same reason: a user can belong to many organizations, so
  "current" is not permanent identity).
- **Not server-side session state.** ASP.NET Core session middleware was never enabled in this
  project and adding it (server-side session storage, a session-id cookie, an eviction policy) is a
  materially larger piece of infrastructure than one small client cookie for one integer value —
  disproportionate to the problem (Step 2's own instruction to prefer the smallest correct
  mechanism).
- **Not a generic `ITenantContext`/tenant-resolution framework.** `CurrentUserAccessor` already is
  the single place organization context is resolved; adding a second, more abstract layer on top of
  it would be exactly the kind of unnecessary abstraction CLAUDE.md prohibits, for a need this one
  class already meets.

**Why this is safe despite being entirely client-controlled:** the cookie's value is data — a
*claimed* organization id — never an authorization grant. Every read
(`CurrentUserAccessor.GetCurrentUserAsync`) re-validates it against a real, current
`OrganizationMembership` row for the authenticated user before trusting it for anything; a value
that does not match any real membership (tampered, forged, stale, or simply a garbage string) is
discarded exactly as if no cookie were present at all, and a deterministic safe fallback (lowest
`OrganizationId` among the caller's real memberships) applies instead. This is true regardless of
whether the cookie is protected — Data Protection here is **defense-in-depth against casual
tampering and cross-key-ring reuse, not the security boundary**, which remains, as it always has,
"does a real membership row exist." A forged or foreign-signed cookie value can never grant access
to an organization the caller does not belong to; it can, at most, cause `ReadSelectedOrganizationId`
to discard it and fall back — never to trust it.

**Fallback / no-context behavior (Step 4):** exactly one membership is always selected automatically
(there is nothing else it could mean). Multiple memberships with no valid stored selection use the
same lowest-`OrganizationId` deterministic default Phase 16 always used — but now, the moment that
default is computed, it is written back into the cookie, so the *next* request reads the same
choice back rather than recomputing it. This is what makes an explicit later switch durable: nothing
in the resolution path ever recomputes "lowest id" once a real selection (explicit or
default-then-persisted) exists and remains valid.

**Stale-membership recovery (Step 7):** if the selected organization's membership has been removed
(by an Admin, or by the user's own choice) since the cookie was set, the next request's validation
simply fails to find a match, falls back exactly as if no cookie existed, and self-heals by writing
the recovered choice back. If no memberships remain at all, `GetCurrentUserAsync` returns `null` —
never a fabricated organization — exactly Phase 16's original behavior for a user with zero
memberships.

**Logout (Step 9):** `LogoutModel` now also calls `CurrentUserAccessor.ClearSelectedOrganization()`
alongside `SignInManager.SignOutAsync()`. This is the one browser-security-critical case: without
it, a different account signing in on the same browser could have its very first request's
organization-resolution cookie read still carry the previous account's chosen organization id —
harmless in isolation (it would simply fail the new account's own membership check and fall back
correctly, per the "never trusted" property above), but clearing it removes even that
possibly-confusing transient state rather than relying on validation alone to paper over it.

**Multi-tab (Step 16):** one cookie per browser, not per tab — switching organization in one tab
changes what every other tab of the same browser sees on its next request. This is a deliberate,
documented trade-off (Step 16 itself: "consistency and security are more important than per-tab
convenience"), not an oversight; implementing true per-tab context would require carrying the
organization id in every URL or a client-side state layer, both far larger than this problem
warrants.

**Switch operation and anti-enumeration (Step 5/6/29):** `CurrentUserAccessor.TrySwitchOrganizationAsync`
takes only a user id and an organization id — never a role, never a membership id — verifies a real
membership exists, and returns a plain `bool`. `Pages/Organization/Switch.cshtml.cs` (POST-only)
never inspects that result: whether the switch succeeded or the caller was never a member (or the
id does not exist at all — the two are indistinguishable), the response is identical, a redirect to
`/`, which then simply reflects whichever organization actually ended up current. This closes the
organization-enumeration channel a distinguishing error message or status code would otherwise open.

**Open-redirect (Step 13):** the switch endpoint accepts no return-url parameter of any kind — it
always redirects to the fixed, local `/`. This removes the open-redirect surface entirely rather
than validating a caller-supplied path.

## Alternatives considered
- **Organization id as a route/query-string parameter on every organization-scoped page**: rejected
  — every existing Ticket/Member page would need to carry and re-validate it on every link, a much
  larger change than Step 12 ("the user must never need to manually add an organization ID to
  URLs") allows, and it would reintroduce exactly the "trust the client-supplied id" risk this ADR
  exists to avoid if any single page forgot to re-check it.
- **Encrypting/signing the cookie as a substitute for membership validation** (i.e., trusting an
  unexpired, correctly-signed cookie without a database check): rejected outright — Step 29 is
  explicit that client-controlled context must never bypass membership authorization "even if"
  protected; Data Protection here only prevents casual tampering from producing a plausible-looking
  value, it is never treated as proof of membership.
- **A per-request database round trip to re-derive "lowest OrganizationId" instead of persisting
  the fallback**: rejected — Step 4 explicitly requires that an explicit (or default-then-adopted)
  selection survive across requests rather than being recomputed every time, which a
  never-persisted fallback could not do once a user has more than one membership and switches away
  from the lowest id.

## Consequences
- `CurrentUserAccessor` gained two new optional constructor dependencies
  (`IDataProtectionProvider?`, `IHttpContextAccessor?`, both defaulting to `null`) so every existing
  2-argument construction in Application.Tests keeps compiling and behaving exactly as before — with
  no `HttpContext`, organization context resolution is the deterministic fallback alone, with
  nothing ever written back (there is nowhere to write it to).
- `FlowOps.Application` now carries a `FrameworkReference` to `Microsoft.AspNetCore.App` (the
  standard way for a class library to use ASP.NET Core's shared-framework APIs — `IHttpContextAccessor`,
  `IDataProtectionProvider` — without becoming a web project itself), the same shape
  `FlowOps.Infrastructure` already uses for Identity.
- No database migration: the mechanism is entirely a cookie plus existing `OrganizationMembership`
  rows: exactly what Step 25 expected for a mechanism that does not require one.
- A user's organization selection is shared across every tab of one browser, not per-tab — a
  deliberate, documented trade-off, not a defect.
