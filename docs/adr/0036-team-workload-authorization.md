# 0036. Team Workload authorization

## Status
Accepted

## Context
Team Workload ("who is carrying the current work, and where is the operational pressure?") needed
two authorization answers. First, page-level visibility — already solved: the existing Team
Analytics scope (`TicketAccessPolicy.GetAnalyticsScope`, AUTH-RULE-02's "Team analytics" row) gives
Admin the whole organization, Manager their managed teams, Agent their own assigned tickets, and
Viewer their member teams read-only — exactly the visibility this page needs, reused unchanged.
Second, a narrower question that scope alone doesn't answer: when a Manager or Viewer expands a
team to see its member-level workload, who may see that specific team's member list and per-member
counts? The only existing method that returns a team's roster, `TeamService.GetTeamDetailAsync`, is
gated by `DirectoryAccessPolicy.CanManageTeams` — Admin-only, because it exists for team
*management* (add/remove members, rename, deactivate), not read-only observation. Widening that
gate to admit Manager would hand every Manager real management-adjacent access to every managed
team's roster through a door meant for something else; reusing it as-is would make Team Workload
Admin-only, contradicting the explicitly approved requirement that Manager and Viewer reach their
own teams' member breakdowns.

## Decision
Team Workload's per-team member breakdown (`AnalyticsQueryService.GetTeamMemberWorkloadAsync`,
`AttentionQueryService.GetAtRiskCountsByAssigneeAsync`) is authorized by a new Domain policy method,
`TicketAccessPolicy.CanViewTeamWorkload(CurrentUser, teamId)`, which answers strictly from the
caller's existing `GetAnalyticsScope` result: true for Admin on any team, true for Manager/Viewer
when the team is in their own `TeamIds`, always false for Agent (whose analytics scope has no team
concept). This is deliberately not a new permission — it is the same analytics authority the page's
own team-level table already uses, extended one level deeper. It grants read-only workload
visibility only: no add/remove-member, rename, or deactivate action is reachable through it, and it
shares no code path with `GetTeamDetailAsync`/`DirectoryAccessPolicy.CanManageTeams`, which remains
exactly as Admin-only as before.

## Alternatives considered
- **Widen `DirectoryAccessPolicy.CanManageTeams` to admit Manager.** Rejected — that gate gates real
  management actions elsewhere (Admin/Teams/Details.cshtml.cs); widening it for this page would
  hand Manager unrelated management-adjacent authority as a side effect.
- **Reuse `GetTeamDetailAsync` directly, Admin-only, and make Team Workload's member breakdown
  Admin-only too.** Rejected — contradicts the explicitly approved scope decision that Manager and
  Viewer must reach their own teams' member data, and the product intent's "own teams" framing for
  those roles.
- **A general-purpose "can view team X" policy usable by any future feature.** Rejected as premature
  — `CanViewTeamWorkload`'s name and doc comment are deliberately scoped to this one read-only
  workload use; a future caller with a genuinely different authorization question should get its own
  named method, not silently reuse this one under an unrelated meaning.

## Consequences
Manager and Viewer can see their own teams' member-level workload without gaining any team-management
capability; Agent correctly never can (their scope has no team to expand); Admin is unaffected.
`GetTeamDetailAsync`'s existing Admin-only management authority is untouched — this decision adds a
second, narrower read path alongside it rather than modifying it. The tradeoff accepted: there are
now two different authorization answers for "can this user see team X's members" (one for
management, one for read-only workload), which is intentional given they are genuinely different
questions, but future readers of `TeamService`/`AttentionQueryService` need to know both exist rather
than assuming a single "can see this team" check.
