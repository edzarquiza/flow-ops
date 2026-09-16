# 0016. Account lifecycle: registration consistency and account deletion

## Status
Accepted

## Context
Phase 17 added public registration ("Create Account → Create Organization → Creator becomes
Admin") and account deletion. Two decisions in this phase are architecturally significant enough
to record rather than leave implicit in `AccountService`.

**Registration spans two independent persistence paths.** `UserManager<ApplicationUser>.CreateAsync`
persists the new `ApplicationUser` via its own internal `SaveChangesAsync` call, immediately, before
`AccountService.RegisterAsync` gets a chance to add the `Organization`/`OrganizationMembership` rows
through `FlowOpsDbContext` directly. A naive implementation risks an orphan `ApplicationUser` with
no organization at all if anything after `CreateAsync` fails (a violated constraint, a dropped
connection), or — the mirror case — an orphan `Organization` with no membership.

**Account deletion cannot mean deleting the `ApplicationUser` row.** `Ticket.RequesterId`/
`AssigneeId`, `TicketComment.AuthorId`, and `TicketEvent.ActorUserId` all carry a `RESTRICT` foreign
key to `AspNetUsers` (docs/database.md §7–9, PERSIST-RULE-01). A literal `DELETE` would be rejected
by the database itself the moment the account has ever touched a single ticket — which, for any
account that has done anything in FlowOps, is effectively always — and even where it would succeed,
doing it would destroy exactly the historical record CLAUDE.md §10 (audit history) and the Phase 17
brief both require to survive.

## Decision
**Registration** runs inside one explicit database transaction
(`_dbContext.Database.BeginTransactionAsync`, wrapped in `CreateExecutionStrategy().ExecuteAsync`
for `EnableRetryOnFailure` compatibility — the identical technique `DemoDataSeeder.SeedAsync` already
uses for its own multi-step seeding). `UserManager.CreateAsync`'s internal `SaveChangesAsync` call
executes *inside* this transaction, on the same `DbContext`/connection, so it is not actually
committed until the surrounding transaction commits; a failure adding the `Organization` or
`OrganizationMembership` after a successful `CreateAsync` rolls the user creation back too. No new
Unit-of-Work abstraction was introduced — this is the same raw EF Core transaction technique already
established in this codebase, applied to a second caller.

**Account deletion is a soft delete (deactivation), never a row delete**, built entirely from
existing, idiomatic ASP.NET Core Identity mechanisms — no manual password-hash or claim
manipulation:
1. Every `OrganizationMembership` row for the user is deleted outright (a pure access-grant join
   row, not historical data — nothing else references it by foreign key).
2. `ApplicationUser.IsActive` is set to `false` — already the exact flag `CurrentUserAccessor`
   checks to refuse resolving a `CurrentUser` at all (pre-existing behavior, unchanged; deletion is
   simply the first caller to ever set it to `false` after account creation).
3. The account is permanently locked out via Identity's own lockout fields (`LockoutEnabled = true`,
   `LockoutEnd = DateTimeOffset.MaxValue`) — `SignInManager.PasswordSignInAsync` refuses a locked-out
   account outright, so the deleted account cannot authenticate again even with a correct password.
4. `UserManager.UpdateSecurityStampAsync` invalidates any other already-signed-in session on its
   next validation.
5. The Web layer additionally calls `SignInManager.SignOutAsync()` on the *current* session
   immediately, rather than waiting on step 4's own validation interval.

The `ApplicationUser` row itself, and every `Ticket`/`TicketComment`/`TicketEvent` referencing it,
is left completely untouched. Display names continue to resolve exactly as they already did — by
joining to this same row at read time — so a deactivated account's historical activity remains
attributable and queryable, unchanged from before deletion. No anonymization subsystem was built:
nothing in Phase 17 requires scrubbing a deactivated user's name from history, and the existing
dynamic-resolution behavior (Step 6 of the phase brief) was explicitly preserved rather than
redesigned.

**The sole-admin safety rule** (a user may not delete their account if doing so would leave any
organization with zero Admins) is evaluated with two queries — every organization the user
administers, then which of those already has another Admin — never loading a full
organization/membership graph, consistent with the codebase's existing query-count discipline
(CLAUDE.md §16).

## Alternatives considered
- **A Unit-of-Work or generic transaction-scope abstraction wrapping both `UserManager` and
  `FlowOpsDbContext` calls**: rejected — explicitly out of scope, and unnecessary: an explicit,
  local `BeginTransactionAsync`/`CommitAsync` pair is the smallest correct fix and already has a
  precedent in this codebase.
- **Two-phase registration (create the user first, redirect to a second "create your organization"
  step)**: rejected — the product brief specifies a single submission
  ("Create Account → Create Organization → Creator becomes Admin → enter the dashboard"), and a
  two-step flow would reintroduce exactly the orphan-user risk this ADR's transaction avoids, just
  spread across two HTTP requests instead of hidden inside one.
- **Literal deletion of the `ApplicationUser` row, with historical FKs changed to `SET NULL` or
  cascaded**: rejected outright — this would either require a destructive schema change to
  long-standing `RESTRICT` foreign keys (weakening the same integrity guarantee PERSIST-RULE-01
  exists for) or would silently erase who requested, was assigned, authored, or acted on a ticket,
  which is exactly the audit trail CLAUDE.md §10 requires to be permanent.
- **A dedicated "anonymize on delete" pass that rewrites `DisplayName` to something like "Deleted
  User"**: rejected — no requirement demands it, the existing name-resolution behavior is explicitly
  preserved per the phase brief's own instruction, and it would need a schema or behavior change
  (a snapshot of the name at each historical event) that nothing today needs.
- **Blocking deletion only against the user's *current* organization** (ignoring other
  organizations they administer): rejected — the product brief is explicit that the safety rule
  must be evaluated against every organization the user is an Admin of, not just the one they
  happen to be viewing when they delete their account.

## Consequences
- A registration failure partway through (e.g., a database error while inserting the
  `Organization`) leaves no partial state: no orphan user, no orphan organization.
- Deleting an account is reversible only by an operator restoring `IsActive`/`LockoutEnd` directly —
  there is no self-service "reactivate" flow in Phase 17, and none was requested.
- Every historical `Ticket`/`TicketComment`/`TicketEvent` remains fully intact and queryable after
  the account that created it is deleted; only that account's own future authentication and
  organization access are affected.
- `DemoProtectionPolicy.EnsureMutable` — built in Phase 14/16 ahead of any real caller — gets its
  first caller in `AccountService`, so a demo persona can still never be renamed, have its email or
  password changed, or be deleted through this new surface.
