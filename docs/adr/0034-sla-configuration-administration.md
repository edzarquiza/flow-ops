# 0034. SLA configuration administration (Platform-level, global)

## Status
Accepted

## Context
`SlaConfiguration` (SLA-RULE-01/02) had no edit path anywhere — Domain, Application, and Web all
treated it as fixed, seeded reference data (four rows: the Critical/High/Medium/Low priority
defaults). An operator who wanted a different target resolution time or at-risk threshold had no
way to change one short of a manual database edit. Building the obvious screen for this raised the
same question ADR-0015 already answered explicitly: `SlaConfiguration` is documented there as
"GLOBAL — deliberate — shared policy reference data, not sensitive, no requirement yet for per-org
SLA customization; revisit only if that requirement becomes real." No such requirement was raised
here, so this phase implements editing within that existing decision rather than reopening it.

## Decision
SLA configuration editing is a **Platform-level** screen (`/Platform/Sla`), gated by
`PlatformUserAccessor` exactly like every other `/Platform` page (ADR-0023) — never a tenant-facing
`/Admin` screen, and never scoped by `OrganizationId` (the table still has none). A Platform Admin
can:
- Edit an existing row's `TargetMinutes`/`RiskThresholdPercent` (never its `WorkType`/`Priority`
  identity — changing that is a different row entirely).
- Add a new per-`WorkType` override for a priority that doesn't already have one.
- Remove a `WorkType`-specific override, which reverts that `(WorkType, Priority)` pair back to the
  priority's own default row.

The four priority-default rows (`WorkType IS NULL`) can never be removed through this screen —
`SlaPolicy.ResolveTargetMinutes` requires one to exist for every `Priority` (SLA-RULE-01); without
one, ticket creation and reopening for that priority would throw. `SlaConfiguration` gained a
`UpdateTargets` mutator with the same "caller validates bounds, the method trusts its input" split
`TeamService.RenameAsync` already uses for its own plain-field edits.

Unlike `Team`/`Category`/`Project`, a removed override is **hard-deleted**, not soft-deleted: no
ticket ever holds a foreign key to a specific `SlaConfiguration` row — a ticket only ever copies
`TargetMinutes` out of one, once, at its own SLA clock start (SLA-RULE-03) — so there is nothing
left referencing the row for a delete to orphan, and no `IsActive` concept was invented for a
lifecycle this data never has.

SLA changes are **not** written to `PlatformAuditEvent`. That table's own doc comment (ADR-0023) is
explicit that "exactly one of `TargetOrganizationId`/`TargetUserId` is set" — an SLA configuration
change has neither, and loosening that constraint to "zero or one" for one new event type would
weaken a guarantee every existing platform event type currently relies on. This matches existing
precedent already in the codebase: `PlatformOrganizationService.RenameOrganizationAsync` also
accepts a `PlatformAdminIdentity actor` and does not write an audit event for it. Every new SLA
method still accepts `actor`, matching every other Platform service method's shape, for the same
reason `RenameOrganizationAsync` does.

## Alternatives considered
- **Per-organization SLA configuration** (add `OrganizationId`, seed defaults per new org, migrate
  the four existing global rows, move the screen to tenant `/Admin`). This is the real alternative
  ADR-0015 names as the trigger to revisit its own decision — explicitly declined for this phase; no
  requirement for it was raised, and it is a schema/architecture change, not a screen.
- **Soft-delete for overrides** (an `IsActive` flag, matching Team/Category/Project). Rejected — no
  ticket ever references a specific `SlaConfiguration` row, so there is no historical-integrity
  reason a hard delete would break, unlike those three entities.
- **Extending `PlatformAuditEvent`** to cover SLA changes. Rejected for now — see Decision; revisit
  only alongside a broader rework of that table's "exactly one target" shape, not as a one-off.

## Consequences
Makes SLA targets/thresholds genuinely tunable for the first time, without touching any ticket
already in flight (SLA-RULE-03, unchanged). Keeps ADR-0015's tenant-isolation boundary exactly as
drawn — every organization still shares one SLA policy. SLA configuration changes are not currently
recorded anywhere beyond structured application logs, a real (if minor) accountability gap this
phase accepts rather than forcing into a table whose own contract doesn't fit; a future phase that
needs it can loosen `PlatformAuditEvent`'s target constraint deliberately, as its own decision.
