---
name: flowops-authorization
description: Authorization patterns for FlowOps application services. Use when adding a new application service method, ticket operation, or list query that returns or mutates tickets.
paths: ["**/Application/**"]
---

# FlowOps authorization discipline

Full role matrix and rules in CLAUDE.md §6. The two failure modes this skill exists to catch:

## 1. A mutating method with no policy check

Every application service method that views, mutates, or transitions a ticket must call
`TicketAccessPolicy` before doing anything else — `CanView`, `CanComment`, `CanAssign`,
`CanTransition`, `CanReopen`, `CanSeeInternalComments`. If you're adding a new method and it
doesn't start with one of these calls, that's a gap, not a shortcut.

The Razor Pages and the API controllers must call the exact same `TicketAccessPolicy` — never
write a second, "simpler" check in a controller because it seemed faster.

## 2. Fetch-broadly-then-filter

List queries scope visibility as a `WHERE` clause (team membership), not by fetching everything
and filtering in memory afterward. If you write a query that returns tickets and only *then*
checks `if (user.CanSee(ticket))`, rewrite it so the scope is in the query itself. This isn't
just a performance issue — it's the actual security boundary, and an in-memory filter is easy to
accidentally skip on a later refactor.

## Also check

- Does the new capability belong to a role per the §6.1 table, or does authorization actually
  depend on team membership/ownership rather than role alone? If the latter, it needs explicit
  logic, not just an `[Authorize(Roles=...)]` attribute.
- Privilege escalation: a user still can't change their own role, the last Admin can't be
  demoted, and demo persona accounts (§14) can't have role or credentials changed by anyone.
