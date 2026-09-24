# 0035. Explainable attention brief and transactional email V1

## Status
Accepted

## Context
Two related gaps: (1) the At-Risk queue and Ticket Detail could say *that* a ticket needed
attention (via `AttentionPolicy`'s signals) but never *why*, in plain language, with the ticket's
own recent history as evidence; and (2) FlowOps sent no email at all — an invited member, a newly
assigned agent, and a ticket's requester/assignee all learned about events only by noticing them in
the app. CLAUDE.md §23 lists "email notifications" as Stretch scope, gated on an ADR; this phase
adds exactly the two capabilities and no more, following a Gate A investigation-and-approval step
before any implementation began.

## Decision
**Explainable Attention Brief.** `AttentionPolicy` remains the sole authority on whether/why a
ticket needs attention — nothing here re-derives that decision. A new `AttentionSuggestion` (Domain)
maps the signal codes `AttentionPolicy.Evaluate` already found, plus the ticket's already-known
`Status`, to one fixed "suggested next step" sentence — deliberately not a second policy engine: it
never reads the `Ticket` aggregate and never decides whether a signal applies. `AttentionQueryService
.GetBriefAsync` assembles an `AttentionBrief` (signals + up to two recent real `TicketEvent` rows,
excluding comments, for "what changed" + the suggested next step) — no fabricated history, no risk
score, no AI. Surfaced read-only on Ticket Detail ("Why this needs attention," shown only when the
ticket has signals) and as a `<details>` disclosure per row on At-Risk Work ("View attention
brief"). The Dashboard is unchanged.

**Transactional Email V1.** Exactly three notifications: invitation created, ticket
assigned/reassigned, and comment added. A narrow `IEmailSender` (Infrastructure) with one method,
`SendAsync(EmailMessage) -> EmailSendResult`, backed by `LogEmailSender` (Development — logs, never
sends) or `ResendEmailSender` (a plain `HttpClient` POST to Resend's REST API, no SDK), chosen at
composition time in `Program.cs` from `FlowOps:Email:Provider`. `IEmailSender`/`EmailOptions` are
**required** constructor dependencies on `TicketService`/`InvitationService` — never an optional
parameter defaulting to "no email" — so a production composition can never silently end up without
one; tests pass a `RecordingEmailSender` test double instead of a mock.

Recipients: invitation → the invited address only. Assignment/reassignment → the new assignee only,
never the actor (no self-assign email), never the previous assignee. Comment → the ticket's
requester and current assignee, excluding the comment's author, any duplicate (the same person can
be both), any deactivated account, and — for an internal comment only — anyone
`TicketAccessPolicy.CanSeeInternalComments` excludes (reused directly, not restated).

**Failure semantics.** An email send happens strictly after its triggering write has already
committed, and a failure (thrown or returned) is logged and never rolled back into the business
operation, never retried, and never surfaced as an error to the actor — with one exception:
invitation creation shows a small inline warning plus the existing copy-link fallback, since that is
the one notification whose failure otherwise leaves the recipient with no way to join at all.
Assignment/comment failures are log-only.

**Email base URL.** Every email-embedded link is built from one trusted `FlowOps:Email:BaseUrl`
configuration value plus a fixed, known application-relative path — never derived from a request's
Host header, forwarded headers, query string, or any other client-supplied input — because
`InvitationService`/`TicketService` run outside any HTTP request context and must not trust one
anyway.

## Alternatives considered
- **A generic notification framework** (event bus, outbox, message broker, background scheduler).
  Rejected — CLAUDE.md §23's own prohibition list, and three fixed triggers do not need general
  infrastructure; ADR-0006 already rejected a background scheduler for this codebase.
- **A second `AttentionBriefPolicy`** independently re-deciding what needs attention. Rejected —
  would create two sources of truth that could silently disagree; `AttentionPolicy` stays the only
  one, and the brief only explains its output.
- **An AI-generated explanation or risk score.** Rejected per the phase's own scope — signals are
  deterministic and already carry human-readable headlines; a score would imply precision the
  underlying rules don't have.
- **Optional `IEmailSender? = null` for test convenience.** Rejected — would let a production
  composition accidentally end up with no email capability at all; every composition (Development,
  Test, Production) now provides a real implementation.

## Consequences
Ticket Detail and At-Risk Work now explain their own attention signals in plain language backed by
real ticket history, with no new source of truth to keep in sync with `AttentionPolicy`. FlowOps can
now reach invited members, newly assigned agents, and ticket participants by email for the first
time, through one narrow, swappable abstraction — adding a fourth notification later means adding
one more trigger call, not new infrastructure. The tradeoff this phase deliberately accepts: no
delivery retry, no notification preferences, and no history of what was sent beyond structured
application logs — acceptable for a first version with exactly three fixed triggers, revisit only if
a real requirement for retry or preferences emerges.
