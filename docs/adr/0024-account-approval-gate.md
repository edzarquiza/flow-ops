# 0024. Account approval: a Platform-level gate on authentication, orthogonal to deactivation

## Status
Accepted

## Context
Phase 24 (ADR-0023) gave the platform an operator's console but left self-service registration
exactly as Phase 17 built it: `AccountService.RegisterAsync` creates the account, its first
Organization, and an Admin membership, and the caller is signed in immediately. Nothing stands
between "typed an email into a public form" and "has an authenticated FlowOps session with Admin
rights over a brand-new organization."

Phase 24A closes that gap: a self-registered account must not gain any normal FlowOps access until
a Platform Admin explicitly approves it. This raises four questions with no existing precedent:

1. How is "not yet approved" represented, given `ApplicationUser.IsActive` already exists and
   already means "not deactivated" (ADR-0023 §5)?
2. How is the gate enforced so it is a genuine authentication-boundary control, not a UI-only
   redirect a client could simply skip?
3. Does an organization invitation — the *other* way a new identity enters FlowOps — need the same
   gate, or is it a different trust relationship?
4. How does a state machine with three states (Pending/Active/Inactive) stay unambiguous, given two
   of its transitions (Approve, Reactivate) both end at Active and must never substitute for each
   other?

## Decision

### 1. Account status: two existing-shaped columns, never a third boolean bolted onto `IsActive`
`ApplicationUser` gains one new column, `RegistrationApprovedAt` (`DateTimeOffset?`, nullable,
default `NULL`) — the same plain-column shape as `IsActive`/`IsPlatformAdmin`/`IsDemoProtected`
already established. `IsActive` keeps its exact existing meaning ("not deactivated"); it is not
overloaded to also mean "approved," because Pending and Inactive are semantically different facts
that happen to produce the same access consequence (no access) but require different reversing
actions (Approve vs. Reactivate) and must never be interchangeable (Decision §5).

The three-state status a Platform Admin reasons about is derived, never stored directly:

| `RegistrationApprovedAt` | `IsActive` | Status |
|---|---|---|
| `NULL` | (irrelevant) | **Pending** |
| set | `true` | **Active** |
| set | `false` | **Inactive** |

`FlowOps.Domain.Accounts.AccountStatus` (a plain enum) and `AccountLifecyclePolicy` (a static,
dependency-free policy — the same shape as `OrganizationAccessPolicy`) live in Domain even though
`ApplicationUser` itself does not (CLAUDE.md §4.2: Domain never references the Identity type) —
`AccountLifecyclePolicy.CanApprove`/`CanDeactivate`/`CanReactivate` take and return only the enum,
so they are genuinely independent of Infrastructure and unit-testable with zero setup.
`PlatformUserService` is the one place the raw-column tuple is resolved into the enum and the one
place any of the three transition methods are implemented — the same "one authoritative place"
discipline ADR-0023 already established for platform authority itself.

### 2. Enforcement: the existing Identity lockout mechanism, not a new pipeline hook
A Pending account must never receive a normal authentication cookie. Rather than add a custom
pre-check to `LoginModel`/`SignInManager` (a new authentication-pipeline concept), registration sets
the *exact* lockout `PlatformUserService.DeactivateUserAsync` already uses to block a deactivated
user: `LockoutEnabled = true`, `LockoutEnd = DateTimeOffset.MaxValue`. `SignInManager.PasswordSignInAsync`
already refuses correct credentials outright for a locked-out account — this was already a
proven, tested mechanism before this phase, not new surface area. `ApproveUserAsync` clears it
(`LockoutEnabled = false`, `LockoutEnd = null`) exactly the way `ReactivateUserAsync` already does.
The consequence: `Register.cshtml.cs` no longer calls `SignInManager.SignInAsync` at all — there is
no session to establish, since the credentials that would establish one are, by construction,
refused until approval.

**Login message**: a Pending account's login attempt renders the *same* generic "Invalid login
attempt" `LoginModel` already shows for a wrong password or a deactivated account (CLAUDE.md §12).
A distinct "your account is awaiting approval" message at the login form would leak account
existence to an unauthenticated caller who does not yet know the email is registered — exactly the
enumeration risk §12 already guards against for every other failure reason. The distinct,
informative message ("awaiting approval by a FlowOps administrator") instead lives on
`/Account/PendingApproval`, reached only immediately after the same caller's own successful
registration — they already know their own email is pending; nothing is disclosed to a third party.

### 3. Invitations: trusted organization onboarding, deliberately exempt
An account created by accepting an organization invitation (`InvitationService.AcceptForNewUserAsync`)
is approved immediately (`RegistrationApprovedAt` set at creation), never Pending. This is not an
oversight or a bypass: creating an invitation at all requires an authenticated caller with invite
permission (`OrganizationAccessPolicy.CanInvite`), which itself requires `CurrentUserAccessor` to
resolve a real `CurrentUser` — impossible for a Pending or Inactive account (`GetCurrentUserAsync`
already returns `null` for `!IsActive`, and a Pending caller can never even obtain a session per
Decision §2). By the time any invitation exists, its inviter is already an approved, active member
of an already-approved organization. Accepting it is vetted onboarding into an existing, trusted
context — materially different from self-registration, which is an unvetted identity arriving from
the public internet and simultaneously creating a brand-new organization with itself as Admin.

### 4. The state machine: `AccountLifecyclePolicy` as the one legality check, never an inline `if`
```
Pending  --Approve-->    Active
Active   --Deactivate--> Inactive
Inactive --Reactivate--> Active
```
`Pending` is a dead end for every transition except Approve. The critical guard, stated explicitly
because it is the one mistake this design has to actively prevent: **Reactivate must never approve
a Pending account, and Approve must never reactivate an Inactive one.** Both
`ApproveUserAsync`/`DeactivateUserAsync`/`ReactivateUserAsync` resolve the caller's actual
`AccountStatus` first and ask `AccountLifecyclePolicy` before mutating anything — never an inline
`if (user.IsActive)` check, which is exactly the shape that would silently blur Pending and Inactive
together (both currently leave `IsActive == false`... no — see the table in Decision §1: Pending
leaves `IsActive` at its post-registration default of `true`, only the lockout blocks it operationally,
which is precisely why a raw `IsActive` check alone cannot tell Pending and Active apart, let alone
Pending and Inactive apart from each other). `ApproveUserAsync` on an already-Active account is a
defined idempotent success (a second Platform Admin approving concurrently produces no duplicate
audit row or side effect — `AccountLifecyclePolicy.CanApprove` is `false` for `Active`, but the
method special-cases that exact status to a no-op `Success()` rather than a `Failed()`, per the
product's own explicit idempotency requirement); on an Inactive account it is a rejected, non-throwing
failure pointing the caller at Reactivate instead.

### 5. Auditability: one more `PlatformEventType` member, no new table
`UserApproved` joins the four members `PlatformAuditEvent`/ADR-0023 already introduced — the same
small, closed, non-general-purpose audit table, never a fifth kind of record.

### 6. Bootstrap: `approve-account`, the same trust tier as `grant-platform-admin`
Discovered live while verifying this phase end-to-end: the very first Platform Admin is themselves
a self-registered account, and self-registration always starts Pending — including for a caller who
will go on to hold `IsPlatformAdmin`. `grant-platform-admin` alone cannot break this circularity
(it sets platform authority, not approval, and a Pending Platform Admin still cannot sign in to use
that authority). `approve-account <email>` is a new startup CLI command, dispatched from `args`
exactly like `grant-platform-admin`/`init-database` — the same deployment/shell trust tier, never an
HTTP endpoint. Without it, the only way to unblock a fresh deployment's first operator would be raw
SQL against production, which is exactly what ADR-0023 §3 already rejected as an acceptable path for
platform-level state.

### 7. Existing users: backfilled to approved, never mass-converted to Pending
The migration's own data step (`UPDATE "AspNetUsers" SET registration_approved_at = NOW() WHERE
registration_approved_at IS NULL`) runs once, immediately after adding the column, and applies to
every row that exists at that moment — i.e. every account that predates this phase entirely. None of
them are "newly registered awaiting approval"; each keeps its own current `IsActive` value exactly as
before (an already-deactivated account stays deactivated — approval alone never reactivates anyone).
Live-verified against the project's own development database: 48 pre-existing accounts, all
backfilled to approved, with the pre-migration 47-active/1-inactive split unchanged.

## Alternatives considered
- **Overload `IsActive` to also mean "approved"**: rejected — see Decision §1. Would make Pending
  and Inactive indistinguishable from each other in every query that already reads `IsActive`
  (`SoleAdminGuard`, `CurrentUserAccessor`, `PlatformUserAccessor`), and would make "deactivate a
  still-Pending account" a silently reachable, meaningless operation.
- **A custom pre-check in `LoginModel`/a `SignInManager` subclass**: rejected — the existing
  Identity lockout mechanism already does exactly this job, already proven by ADR-0023 §5's
  deactivation recipe; adding a second mechanism for the same class of problem (block sign-in) would
  be the "two sources of truth" pattern earlier phases have repeatedly flagged and removed.
- **A distinct "awaiting approval" message on the Login page itself**: rejected — see Decision §2's
  enumeration-risk reasoning.
- **Requiring approval for invited accounts too**: rejected — see Decision §3. Would also introduce
  a real product regression (an org Admin inviting a teammate reasonably expects them to be able to
  sign in immediately, not wait on an unrelated platform operator).
- **`OrganizationMembership.IsApproved`**: never seriously considered — approval is a fact about the
  identity, not about any one membership; a user belonging to multiple organizations is approved
  once, for all of them, the moment their account is approved (`GetCurrentUserAsync` already resolves
  every membership for an Active user exactly as before — this phase changes nothing about
  membership resolution itself).

## Consequences
- `ApplicationUser` gains `RegistrationApprovedAt`; `platform_audit_events`'s `event_type` check
  constraint gains `UserApproved`. One migration (`AddAccountApproval`), additive plus one backfill
  `UPDATE` — every existing row becomes approved as of the migration's run time, never Pending.
- `AccountService.RegisterAsync` no longer results in an authenticated session; `Register.cshtml.cs`
  no longer injects `SignInManager`. A new anonymous page, `/Account/PendingApproval`, is the
  post-registration landing page.
- `PlatformUserService` gains `ApproveUserAsync`/`GetUserStatusSummaryAsync`/a `status` filter on
  `ListUsersAsync`; `DeactivateUserAsync`/`ReactivateUserAsync` are rewritten to consult
  `AccountLifecyclePolicy` instead of an inline `IsActive` check.
- `SoleAdminGuard`'s two "another Admin exists" joins now also require `RegistrationApprovedAt !=
  null` — a Pending Admin membership can no longer count as "another Admin" protecting a sole-admin
  action, closing a latent gap the same shape as the one ADR-0023 §5 already found and fixed for
  deactivated Admins.
- `/Platform/Users` gains a status summary strip and a dedicated `/Platform/Users/Pending` page;
  `/Platform/Users/Details`'s Lifecycle zone now branches three ways (Approve/Deactivate/Reactivate)
  instead of two.
- A new `approve-account` startup CLI command, symmetric in trust tier with `grant-platform-admin`.
- Permanent rejection/deletion of a Pending account remains explicitly out of scope, matching every
  prior phase's non-goals around deletion (ADR-0023's own "Rejected alternative" for hard deletion).
