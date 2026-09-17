# 0027. Assignee picker, invite-to-team, and admin-generated password reset

## Status
Accepted

## Context
Three reported problems traced to two root causes. First: `TicketAccessPolicy.CanAssign` already
let an Admin assign any ticket regardless of team scoping, but the domain invariant it feeds
(`Ticket.Assign`'s "assignee must be an active team member," TICKET-INV-03) had no such exemption,
and `TeamService.CreateAsync` never added the creating Admin as an actual team member — so an
Admin who had just set up their own team could not assign a ticket on it to themselves. The same
gap meant Ticket Details offered no way to assign to anyone but yourself, even though
`TicketService.AssignAsync` already accepted an arbitrary assignee. Second: accepting an
invitation only ever created an `OrganizationMembership`, never a `TeamMembers` row, and ticket
visibility (`TicketAccessPolicy.CanView`) requires team membership for every non-Admin role — so
an invited-and-accepted member saw none of their organization's tickets unless separately added to
a team by hand. Separately, there was no password-reset path of any kind, and the app sends no
email at all.

## Decision
**`TeamService.CreateAsync` now also adds the creating Admin as an active team member**
(`IsTeamManager: true`), closing the self-assign gap at its root rather than special-casing the
invariant for Admin.

**Ticket Details gains an "assign to someone else" picker** (`TicketQueryService.
GetAssignableTeamMembersAsync`, gated the same way `TicketAccessPolicy.CanAssign` already
authorizes Admin/Manager) — a plain `<select>` of the ticket's own active team members, next to
the existing "Assign to me" button.

**Invitations gain an optional Team field.** `Invitation.TeamId` (nullable) is validated against
the inviter's own organization at creation, and accepting an invitation now also adds a
`TeamMembers` row for that team (best-effort: a team deactivated between invite and accept never
blocks the underlying organization membership, it just skips the team join).

**Password reset is Admin-generates-a-link, reusing the invitation UX exactly.** No email
capability exists to build a traditional "email yourself a reset link" flow against. An Admin
(deliberately not extended to Manager — this hands out direct account access, a narrower gate than
ordinary member management) generates a one-time link via ASP.NET Core Identity's built-in
`UserManager.GeneratePasswordResetTokenAsync`, copies it from `Organization/Members`, and sends it
out-of-band; a new anonymous `/Account/ResetPassword` page accepts the token and sets a new
password, with one generic failure message for every rejection (no such user, expired/tampered
token) to avoid an account-enumeration oracle.

## Alternatives considered
- **Exempting Admin from TICKET-INV-03's team-membership check directly**: rejected — the
  invariant should still mean what it says (an assignee is a real, resolvable team member,
  reflected everywhere else a team roster is read); joining the team is the honest fix.
- **Counting organization membership alone toward ticket visibility for every role**: rejected —
  visibility scoped by team is an existing, deliberate, well-tested design (AUTH-RULE-02); the fix
  is giving invitations a way to actually establish that team membership, not loosening the policy.
- **A self-service "forgot password" page that emails a reset link**: not possible without adding
  an email-sending capability the app has never had; out of scope here.

## Consequences
- New migration `AddInvitationTeamId` (nullable `team_id` FK on `invitations`).
- Existing invitations/accepted members from before this change are not retroactively added to any
  team — the existing "Add member" control on `Admin/Teams/Details` remains the correct manual
  fix for anyone already in this state.
- `WorkspaceSetupService`/`TicketQueryService`/`TeamService` each gain one new method; no new
  module, no generic "membership sync" abstraction.
