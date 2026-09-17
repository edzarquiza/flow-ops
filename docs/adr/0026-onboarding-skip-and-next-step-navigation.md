# 0026. Onboarding: skippable steps, invitation-sent completion, and next-step navigation

## Status
Accepted

## Context
ADR-0020 shipped the first-run workspace setup checklist as three derived, non-persisted facts
(team/invite/ticket), deliberately rejecting persisted onboarding state, a wizard-style Next/Back
UI, and counting a sent-but-unaccepted invitation toward "invite your team" completion.

Using the checklist in practice surfaced three real gaps, reported directly by the product owner:

1. **No forward navigation.** Finishing one setup page (e.g. creating the first team on
   `/Admin`) left no way to move to the next step from that page — only back to the dashboard.
2. **Invite and project should be skippable.** A workspace is genuinely usable without a second
   member or a project; forcing those to completion (or leaving them permanently "incomplete" in
   the checklist) doesn't match how the product is actually used.
3. **"I invited someone and it's still not done."** ADR-0020's "must actually accept" rule
   produces exactly this experience: an Admin who has done everything in their control (sent the
   invite) still sees the item as incomplete, for a reason outside their control (whether the
   invitee has gotten to it yet). The product owner's explicit call: sending the invite should be
   enough.

Ticket completion ("create your first ticket" completing the instant a ticket exists) was already
correct and needed no change.

A fourth step, **"create your first project,"** is added at the same time — ADR-0020 could not
point "set up your first team" anywhere real because `/Admin` was a placeholder; real Team and
Project management exist now, so a project step has somewhere genuine to go.

## Decision

**Invite completion becomes "sent OR joined."** `WorkspaceSetupService.GetWorkspaceSetupStatusAsync`
now treats the organization as having satisfied "invite your team" if it has ever created an
`Invitation` row (any status — pending, accepted, or expired) *or* has more than one active
member. This is a deliberate reversal of ADR-0020's explicit "must actually accept" reasoning: the
checklist rewards the Admin's own action, not an outcome the invitee controls.

**Skip state is persisted, narrowly.** Unlike every other checklist fact, "skipped" cannot be
derived from existing data — skipping creates no row. Two nullable `DateTimeOffset` columns,
`invite_step_skipped_at` and `project_step_skipped_at`, are added directly to `organizations`
(`Organization.SkipInviteStep`/`SkipProjectStep`, idempotent, one-way — there is no "un-skip,"
since actually completing the step later already supersedes the skipped state in the checklist's
own display logic). This is the smallest schema footprint that makes skip real without inventing a
generic "onboarding progress" table.

**"Next step" is contextual, not a wizard.** Four pages — `/Admin` (team), `/Organization/Members`
(invite), `/Admin/Projects/Index` (project), `/Tickets/Create` (ticket) — each render a small
`_SetupNextStep` banner once *that page's own step* is done and another step remains. This is
still not the "modal wizard with Next/Back" ADR-0020 rejected: there is no enforced order beyond
what `SetupSteps.All` documents as a sensible default, no page is blocked from being visited out of
order, and the banner is purely a convenience link plus (for skippable steps) a skip action — the
same "every action is a plain link/POST form, works with JavaScript off" discipline as the
dashboard panel itself.

**Skip mutations live on `WorkspaceSetupService`, not `AnalyticsQueryService`.**
`GetWorkspaceSetupStatusAsync` moved with them — `AnalyticsQueryService` is read-only by its own
doc comment, and skip is a write. Still deliberately narrow: one service, three methods, no
generic `IOnboardingService` (ADR-0020's own restraint, kept).

## Alternatives considered
- **Counting only accepted invitations, with a separate "pending invite" visual state**: closer to
  ADR-0020's original spirit, but explicitly rejected by the product owner in favor of a plain
  done/not-done read on the Admin's own action.
- **A dismissal flag per step ("hide forever") instead of "skip"**: rejected — skip should still
  show as an honest "Skipped" state on the checklist (ADR-0020's "shape carries the state" rule),
  not silently disappear as if it had never existed.
- **A real multi-step wizard UI (progress rail, forced order, Back button)**: rejected, same as
  ADR-0020 — FlowOps teaches itself through empty states and contextual links, not a guided tour.

## Consequences
- New migration `AddOrganizationSetupSkipState` (two nullable `timestamp with time zone` columns
  on `organizations`).
- `WorkspaceSetupStatus` gains `HasSentInvitationOrMember`/`InviteStepSkipped`/`HasProject`/
  `ProjectStepSkipped` alongside the renamed invite field; `IsComplete` now requires
  `InviteComplete && ProjectComplete` (either genuinely done or explicitly skipped) rather than
  four independent booleans.
- `docs/adr/0020-first-run-workspace-setup.md`'s invite-completion and "no wizard" reasoning is
  superseded in part by this ADR — its original text is left unedited; see its own cross-reference
  note.
- Four PageModels (`Admin/Index`, `Admin/Projects/Index`, `Organization/Members`,
  `Tickets/Create`) each gain a `WorkspaceSetupService` dependency and a `NextStep` property; the
  new `SetupSteps` static class is the single place step order/labels/skippability live, so the
  dashboard checklist and the four banners can never silently disagree about ordering.
