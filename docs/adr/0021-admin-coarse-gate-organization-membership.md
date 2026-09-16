# 0021. The Admin-only coarse gate moves from Identity role claims to OrganizationMembership

## Status
Accepted

## Context
Phase 4 introduced `/Admin` as a deliberate demonstration of ASP.NET Core's coarse
`AuthorizeFolder("/Admin", "AdminOnly")` / `RequireRole(WellKnownRoles.Admin)` mechanism — a proof
that the coarse gate worked, not a real feature (its own page comment says so: "this is deliberately
the only role-restricted folder added in Phase 4"). `RequireRole` evaluates the authenticated
principal's ASP.NET Core Identity **role claim**.

Phase 16 later introduced `OrganizationMembership.Role` as the actual, per-organization authority
for "what can this user do" — re-resolved fresh from the database on every request by
`CurrentUserAccessor` (ADR-0008: role is deliberately never trusted from a claim, since it is
mutable and a stale claim could let a demoted or removed member keep acting on an old permission).
Every other role-gated page written since Phase 16 (`Organization/Members`, `Tickets/Create`, the
dashboard's own `IndexModel`) follows that pattern: resolve `CurrentUser` via `CurrentUserAccessor`,
check `CurrentUser.Role` (or a domain policy built on it) in the PageModel, `Forbid()` if it fails.

Nobody went back and migrated `/Admin`'s Phase-4 demo gate when Phase 16 landed. The two role
systems are now genuinely parallel and, for a real user, disconnected: `DemoDataSeeder` and the
Web-test-only `TestUsers` fixture both call `UserManager.AddToRoleAsync` (assigning an Identity role
claim) purely so those specific seeded personas could exercise `/Admin`'s demo — but
`AccountService.RegisterAsync` (real registration) and `InvitationService` (real invitation
acceptance) never call it. The result: a genuine, real-world Admin — Admin via
`OrganizationMembership.Role`, exactly as every other page in the product already recognizes them —
has no Identity role claim and is unconditionally denied `/Admin`, while the coincidentally
dual-seeded demo/test personas pass. This was invisible in the existing test suite because
`Admin_AsAdmin_ReturnsOk` happens to exercise a persona seeded with both systems in lockstep; it
surfaced only once the dashboard's Workspace Setup panel (ADR-0020) linked a real, freshly registered
Admin to `/Admin` and they hit Access Denied.

## Decision
`/Admin`'s authorization moves onto the same architecture every other page already uses:
`Pages/Admin/Index.cshtml.cs` now resolves `CurrentUser` via `CurrentUserAccessor` and checks
`user.Role == UserRole.Admin` itself, returning `Forbid()` otherwise — no `[Authorize(Roles=...)]`,
no ASP.NET Core policy, no Identity role claim anywhere in the check.
`Program.cs`'s `AuthorizeFolder("/Admin", "AdminOnly")` and the `"AdminOnly"` policy registration are
removed; `/Admin` now only inherits the blanket `AuthorizeFolder("/")` authentication requirement
(unauthenticated → redirected to login exactly as before), and the actual role decision lives with
the page, in step with the codebase's own established shape.

`OrganizationMembership` remains the one authority. This is not a second authorization system next
to the existing one — it is deleting the one Identity-role-based holdout and replacing it with the
mechanism that was already everywhere else. Nothing about `TicketAccessPolicy`,
`OrganizationAccessPolicy`, `CurrentUserAccessor`, cross-organization scoping, or the demo/test
Identity-role seeding itself changes — `DemoDataSeeder`/`TestUsers` still assign an Identity role
claim (harmless, now simply unused for authorization), since removing that seeding is a separate,
unrelated cleanup with no bearing on this fix.

## Alternatives considered
- **Write a custom `IAuthorizationHandler` that queries `OrganizationMembership` for the policy
  check**: rejected — it would reintroduce policy-based authorization as a second mechanism
  alongside the PageModel-level checks every other page already uses, for no benefit; a handler
  still has to call the same `CurrentUserAccessor`-shaped resolution, just from a different layer.
- **Also grant `/Admin` to Identity role claims as a fallback (support both systems)**: rejected —
  it would keep two parallel, driftable sources of "is this user an Admin" indefinitely instead of
  retiring the stale one, the opposite of "OrganizationMembership remains authoritative."
- **Leave `/Admin` denied and just remove the onboarding link instead**: rejected — this fixes only
  the one caller (Workspace Setup) and leaves a real authorization bug in place for anyone else who
  ever reaches `/Admin`; the coarse gate itself needed to be correct regardless of who links to it.
- **Build real Team/Category CRUD now that Admin can reach `/Admin`**: explicitly out of scope —
  `/Admin`'s content is unchanged, still a placeholder; only its authorization was wrong.

## Consequences
- No schema change, no new authorization primitive. One PageModel added
  (`Pages/Admin/Index.cshtml.cs`), a few lines removed from `Program.cs`.
- A real, freshly registered organization's Admin can now actually reach `/Admin` (still a
  placeholder page) — the Workspace Setup panel's "Set up your first team" action no longer sends a
  genuine Admin to Access Denied.
- `WellKnownRoles`/`ApplicationRole`/Identity role-claim seeding remain in the codebase, now used
  only by `DemoDataSeeder` and the Web-test `TestUsers` fixture for their own historical reasons —
  no authorization decision anywhere reads an Identity role claim anymore.

## Update (Phase 22)
This ADR is a historical record of the authorization fix and is not rewritten in place. For current
readers: the "still a placeholder page" language in the Alternatives/Consequences sections above no
longer describes `/Admin`. Later phases built real team creation, team/category lifecycle management
(ADR-0022), and project management (`/Admin/Projects`) on top of the authorization fix this ADR made
possible. The authorization decision itself — `OrganizationMembership.Role` as the sole authority,
no Identity role claim anywhere in the check — remains exactly as decided here and is unaffected by
that later work.
