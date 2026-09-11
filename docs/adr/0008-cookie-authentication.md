# 0008. Cookie authentication via ASP.NET Core Identity, not JWT

## Status
Accepted

## Context
Phase 4 needed a concrete authentication mechanism before `TicketAccessPolicy` could be wired to
a real signed-in user. CLAUDE.md §11.2 already named the decision ("cookie authentication...
No JWT — there is no third-party consumer and no separate client, so token lifecycle management
would be pure cost") and reserved this ADR number, but the decision had not yet been formally
recorded as its own document, and Phase 4 is where it is actually exercised: `Program.cs` now
wires `AddIdentity<ApplicationUser, ApplicationRole>()` with `IdentityDbContext` (CLAUDE.md §7.1)
and `ConfigureApplicationCookie` per §12's exact settings (HttpOnly, Secure, SameSite=Strict,
8-hour sliding expiration, `/Account/Login` path).

## Decision
FlowOps authenticates with ASP.NET Core Identity's own cookie authentication scheme
(`SignInManager<ApplicationUser>.PasswordSignInAsync`), not JWT bearer tokens. The authenticated
principal carries only what Identity puts there by default (name, id, role claims for the coarse
`[Authorize(Roles=...)]` gate) — no ticket-specific or team-membership claims are added, since
those are mutable and must be re-resolved from the database on every request
(`CurrentUserAccessor`, AUTH-RULE-04) rather than trusted from a signed cookie between logins.

## Alternatives considered
- **JWT bearer tokens:** rejected, per the existing CLAUDE.md §11.2 rationale — FlowOps is a
  same-origin Razor Pages application with no separate client and no third-party API consumer, so
  JWT would add token issuance, refresh, and revocation machinery to solve a problem that does not
  exist here. Revisiting this is explicitly deferred to Project 8 (a genuine API product).
- **An external identity provider / OAuth / OIDC:** rejected — no such requirement exists in the
  contract, and it would add an external dependency and a second source of truth for user
  identity where CLAUDE.md already specifies ASP.NET Core Identity as the intended implementation
  (§4.2, §7.1, and the explainability table in §24: "How does authentication work? | ASP.NET Core
  Identity, cookies").
- **Encoding role/team-membership claims directly in the cookie** (avoiding a database read per
  request): rejected — role and team-manager status are both mutable and can change between a
  user's logins (CLAUDE.md §6.2's privilege-escalation guards), so trusting a stale claim would
  let a demoted Manager or a removed team member retain access until their session cookie expired.
  `CurrentUserAccessor` resolves both fresh from the database on every call instead.

## Consequences
- No token refresh/revocation infrastructure is needed; sign-out is a single
  `SignInManager.SignOutAsync()` call that immediately invalidates the local cookie.
- Every authorization decision that depends on role or team membership costs one extra database
  read per request (via `CurrentUserAccessor`) — acceptable at FlowOps's target scale (CLAUDE.md
  §16: "a few thousand tickets, a handful of concurrent users") and consistent with the two-layer
  authorization model already specified in §6.2.
- Because cookies are opaque to any future non-browser client, an actual API product (Project 8)
  would need its own token-based authentication story — this ADR does not need to anticipate that;
  it would be a new decision when that project exists.
- `PersistKeysToDbContext` (CLAUDE.md §12) is required for this to work correctly across process
  restarts — without it, every restart invalidates every outstanding cookie.
