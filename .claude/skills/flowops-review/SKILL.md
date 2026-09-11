---
name: flowops-review
description: Run the FlowOps standing review checklist against the current uncommitted changes before a PR.
disable-model-invocation: true
allowed-tools: Bash(git diff *) Bash(git status *)
---

## Current changes

!`git status --short`

!`git diff HEAD`

## Instructions

Review the diff above against the FlowOps CLAUDE.md §21 checklist, point by point:

1. Business logic leaked into a PageModel, controller, query, or JS instead of Domain/Application?
2. Any rule (SLA, transition, at-risk, permission) now has two implementations instead of one?
3. Every mutating path authorized server-side via `TicketAccessPolicy`?
4. Every state change produces exactly one audit event, in the same transaction?
5. Any N+1 query, unbounded query, or missing index for a new query shape?
6. Any abstraction added with exactly one implementation and no real test-seam value?
7. Any new dependency — and is it justified by an ADR?
8. Any secret, credential, or real personal data anywhere in the diff?
9. Do the tests cover real behavior, or only the happy path?
10. Are nullable columns genuinely optional, with constraints enforced at the database level?
11. Could a service desk technician understand any new screen without developer vocabulary?
12. Could this change be defended in an interview in two sentences?

For each numbered item: state pass, fail, or n/a, with the specific file/line if it's a fail.
Don't restate items that clearly don't apply (e.g. no UI touched → skip 11) — just say n/a in one
line. End with a short list of what to fix before this is PR-ready, or say it's clean.
