# 0025. Account rejection as a fourth, terminal status; real-time approval feedback via polling

## Status
Accepted

## Context
ADR-0024 gave FlowOps a three-state account lifecycle (Pending/Active/Inactive) and a Platform Admin
approval gate, but left two real gaps once used in practice:

1. **No way to reject.** A Platform Admin could only Approve a Pending account, or silently ignore
   it forever — there was no way to record "this registration was reviewed and declined," and no
   way to communicate that outcome to the registrant.
2. **No feedback loop.** After registering, a caller landed on a static `/Account/PendingApproval`
   page with no way to learn the outcome except manually retrying `/Account/Login` every so often.

This phase adds Reject as a first-class lifecycle transition, and gives the waiting page a way to
detect a Platform Admin's decision without the caller refreshing anything.

## Decision

### 1. Rejected: a fourth `AccountStatus`, never conflated with Inactive
`ApplicationUser` gains one more nullable column, `RegistrationRejectedAt`, the same plain shape as
`RegistrationApprovedAt` (ADR-0024). Together with the existing two columns, the four-state mapping is:

| `RegistrationRejectedAt` | `RegistrationApprovedAt` | `IsActive` | Status |
|---|---|---|---|
| set | (irrelevant) | (irrelevant) | **Rejected** |
| `NULL` | `NULL` | (irrelevant) | **Pending** |
| `NULL` | set | `true` | **Active** |
| `NULL` | set | `false` | **Inactive** |

`RegistrationRejectedAt` and `RegistrationApprovedAt` are mutually exclusive by construction:
`AccountLifecyclePolicy.CanReject`/`CanApprove` both require `AccountStatus.Pending`, so a rejected
account was, by definition, never approved. Rejected is deliberately terminal — `AccountLifecyclePolicy`
gives it no outgoing transition at all (not even back to Pending): it has no `CanX` predicate that
ever returns `true` for it. This is the direct fix for the exact ambiguity the product spec named:
**Rejected must never be conflated with Inactive.** Inactive means "was let in, then removed, and
can be let back in by Reactivate." Rejected means "was reviewed once, at the door, and never let in
at all" — reusing Inactive's own Deactivate/Reactivate pair for this would have made a Platform
Admin's Reactivate button silently double as an "override my colleague's rejection" button, which
was never the intent.

### 2. A shared status resolver, not two independently-maintained copies
`FlowOps.Application.Accounts.AccountStatusResolver.Resolve` is now the one place the three raw
columns become the `AccountStatus` enum. Before this phase, `PlatformUserService` had its own
private `ResolveStatus`; this phase's Login page also needs the identical derivation (Decision §4),
so the logic was promoted out to a shared, public static method rather than duplicated — the "two
sources of truth" pattern this codebase has repeatedly found and removed elsewhere.

### 3. Reject reuses the exact recipe Approve already established
`PlatformUserService.RejectUserAsync` mirrors `ApproveUserAsync`'s shape precisely: resolve the
authoritative user, check `AccountLifecyclePolicy.CanReject`, mutate, record a `PlatformAuditEvent`
(`UserRejected`, joining `OrganizationDeactivated`/`Reactivated`/`UserDeactivated`/`Reactivated`/
`Approved` in the same small, closed event table — ADR-0023 §6, never a new one). It deliberately
does **not** touch `LockoutEnabled`/`LockoutEnd`: the account is already fully locked out from the
moment `AccountService.RegisterAsync` created it (ADR-0024 §2), and rejection's whole job is to make
that permanent, never to loosen it. Never a hard delete: `ApplicationUser`, `Organization`, and
`OrganizationMembership` rows are untouched by rejection, for the same reason ADR-0023 rejected hard
deletion generally — `RESTRICT` foreign keys aside, there is no product requirement to destroy a
rejected registration's history, and preserving it costs nothing.

### 4. Approval/rejection concurrency: Identity's own `ConcurrencyStamp`, not new infrastructure
Two Platform Admins racing Approve and Reject on the same Pending account must end in exactly one
valid state. `UserManager.UpdateAsync` already carries ASP.NET Core Identity's own optimistic
concurrency check via `ConcurrencyStamp` — a fact that existed unused in this codebase before this
phase (`ApproveUserAsync`/`DeactivateUserAsync`/`ReactivateUserAsync` never checked
`UpdateAsync`'s result). `ApproveUserAsync` and `RejectUserAsync` now check it explicitly: if a
concurrent mutation already committed, the second call's `UpdateAsync` fails, and the caller gets a
"this account was just changed by another administrator, please refresh" result rather than
silently overwriting the first outcome. Verified directly (two independent `DbContext`s loading the
same Pending row before either commits, then racing Approve against Reject) — the loser's own
`UpdateAsync` genuinely fails, and the account ends in exactly the winner's state. No distributed
lock, no new infrastructure — the mechanism was already present in the framework this app already
uses.

### 5. Real-time feedback: server-rendered polling, never SignalR/WebSockets/SSE
`/Account/PendingApproval` gained a `GET ?handler=Status` Razor Pages handler returning
`{ "status": "Pending" | "Active" | "Rejected" | "Unknown" }`, polled by a small (~100-line) vanilla
JavaScript file every 4 seconds — squarely within CLAUDE.md §11.1's existing, already-documented
allowance ("JavaScript: vanilla, progressive enhancement only... every page must work with JS
disabled, degrading to full-page posts"), the same category as its own listed "live 'time remaining'
tick" example. It is, however, the *first* page in the app to actually use that allowance, so the
CSP's `default-src 'none'` needed narrowing by exactly `script-src 'self'` (one self-hosted file, no
inline script, no CDN, no third-party origin) and `connect-src 'self'` (so `fetch()` itself is not
blocked by the otherwise-`'none'` default). With JavaScript disabled, the page still server-renders
the caller's correct current status on every load (Decision §6) — a manual reload replaces the
automatic poll tick, never a broken or blank page.

**Why polling over SignalR/WebSockets/SSE**: a persistent connection is disproportionate
infrastructure for a caller who checks in roughly once every few seconds, for a few minutes at
most, for one binary-ish outcome. Polling needs no new connection-management code, no new hosting
concern (SignalR's backplane/scale-out story), and degrades trivially (a failed poll just retries on
the next tick — see Decision §7) — exactly the "smallest correct solution using the existing
architecture" this phase's own spec required.

### 6. Identifying the caller: a scoped, non-sensitive correlation cookie — never a client-supplied id
The caller polling `?handler=Status` is, by design, never authenticated (ADR-0024: establishing a
session before approval would defeat the whole gate). `PendingRegistrationCookie` is a new,
Data-Protection-protected cookie — the same technique `CurrentUserAccessor`'s own organization-context
cookie already uses (ADR-0018) — written once, at registration, holding only the new user's id. It is
deliberately **not** the Identity application cookie (no claims, no roles, never touches
`SignInManager`, authorizes nothing else), **not** a raw id in the query string (would be logged,
cached, and trivially shareable), and scoped with `Path=/Account/PendingApproval` so it is never even
sent on any other request. It expires after two hours — long enough for a Platform Admin to notice
and act, never a standing credential.

Critically, `?handler=Status` reads **only** this cookie. It accepts no `userId` (or any other
identifying) parameter — live-verified by requesting the endpoint with a forged `userId` query
string attached to a real, valid correlation cookie: the response is unchanged, always the cookie
owner's own status. A caller with no cookie at all (never registered, or the cookie expired/was
tampered with) gets `"Unknown"`, never an error and never another account's real status. This is
the direct implementation of the spec's own constraint: the endpoint must answer only "is *my* own
just-registered account still pending," never become a general user-status lookup.

### 7. Polling failure and lifecycle: never infer rejection from a network error
The client script treats exactly two values, `"Active"` and `"Rejected"`, as terminal — every other
outcome (`"Pending"`, `"Unknown"`, a non-200 response, a malformed body, or a network failure)
leaves the page in its waiting state and keeps polling. A single dropped request must never read as
"your account was rejected." Polling stops the instant a terminal status is observed, and also
pauses while the tab is hidden (`visibilitychange`) and stops entirely on `pagehide` — never a
background timer that outlives the page.

### 8. Login messages: Pending and Rejected get a specific reason, but only after the password is verified
CLAUDE.md §12's generic "Invalid login attempt" remains the answer for a wrong password, an unknown
email, and a plain deactivated (Inactive) account — distinguishing those would leak which accounts
exist. This phase's product spec explicitly asks for a *distinct* message for Pending ("awaiting
approval") and Rejected ("not approved") — which would ordinarily be exactly the kind of
enumeration leak §12 exists to prevent, except for one detail that makes it safe here:
`LoginModel` now performs an **independent** `UserManager.CheckPasswordAsync` check — separate from
`SignInManager.PasswordSignInAsync`'s own lockout-driven result, which short-circuits before ever
checking the password once an account is already locked out (which every Pending/Rejected/Inactive
account already is) — and only reveals the specific reason once that check confirms the submitted
password is genuinely correct for that account. A caller who does not already hold the correct
password sees the same generic message as ever. In other words: the more specific message is shown
only to someone who has already proven, by supplying the real password, that they are either the
account's own owner or already possess its credentials by some other means — at which point telling
them their own account's status does not disclose anything to a third party who does not already
know it.

## Alternatives considered
- **Represent Rejected as `IsActive = false` with `RegistrationApprovedAt` left null** (i.e., reuse
  the existing two columns): rejected — this is indistinguishable from Pending at the storage level
  (both have `RegistrationApprovedAt == null`), which is exactly the ambiguity ADR-0024 already
  fixed once for Pending vs. Inactive. A third real state needs a third real signal.
- **SignalR / WebSockets / Server-Sent Events**: rejected — see Decision §5.
- **A raw user id (or a random opaque token stored server-side) in the status URL's query string**:
  rejected in favor of the cookie — a URL-embedded identifier is logged by default (access logs,
  browser history, any intermediate proxy) and trivially shared/replayed by copying a link; a
  server-side token store would also be new state to manage (expiry, cleanup) for no benefit over a
  cookie the browser already handles.
- **Reusing the Identity application cookie itself for correlation** (i.e., quietly signing the
  caller into a very restricted session): rejected — this is exactly the "establish a session before
  approval" shortcut ADR-0024 already rejected as defeating the gate's entire purpose, even if that
  session's claims were deliberately weak.
- **Always showing the generic login message for Pending/Rejected, exactly as ADR-0024 first
  decided**: reconsidered per this phase's explicit product requirement — see Decision §8 for why a
  password-gated specific message is safe where an ungated one would not be.
- **A distinct "awaiting approval" message directly on the Login page without the password check**:
  rejected — this is precisely the enumeration leak ADR-0024 originally avoided, and nothing about
  adding Reject changes that reasoning; the password-verification gate is what makes the new,
  more specific messages safe to add now.

## Consequences
- `ApplicationUser` gains `RegistrationRejectedAt`; `platform_audit_events`'s `event_type` check
  constraint gains `UserRejected`. One migration (`AddAccountRejection`), additive only — no
  backfill needed (every existing row already correctly has this new column `NULL`).
- `AccountLifecyclePolicy` gains `CanReject`; `PlatformUserService` gains `RejectUserAsync` and now
  checks `UserManager.UpdateAsync`'s result on both `ApproveUserAsync` and `RejectUserAsync` for
  concurrency safety.
- `/Account/PendingApproval` is no longer a static page: it gained a `?handler=Status` JSON endpoint,
  a `PendingRegistrationCookie`, and the codebase's first JavaScript file
  (`wwwroot/js/pending-approval.js`, ~100 lines, no dependencies). The CSP's `default-src 'none'`
  gained exactly `script-src 'self'` and `connect-src 'self'` — no inline script, no CDN, no
  third-party origin of any kind.
- `/Platform/Users/Details`, `/Platform/Users/Pending`, and the `/Platform` homepage's Pending
  Approvals section all gained a Reject action alongside the existing Approve action, through the
  same `PlatformUserService.RejectUserAsync` — no duplicated lifecycle logic in any PageModel.
- `LoginModel` now performs one additional `CheckPasswordAsync` call on a failed sign-in attempt, to
  decide whether a specific Pending/Rejected message is safe to show (Decision §8).
- Rejection-reason capture, admin notes, and rejection appeals remain explicitly out of scope,
  matching the phase's own non-goals — inventing a reason field not already supported by the backend
  was deliberately avoided.
