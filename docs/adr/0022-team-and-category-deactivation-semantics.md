# 0022. Team and Category deactivation: soft, non-cascading, filter-at-read-time

## Status
Accepted

## Context
Phase 21's audit (PG-1/PG-2) found that neither `Team` nor `Category` could be renamed or
deactivated once created — a real correction gap, since `TeamService`/`CatalogService` only ever
supported `CreateAsync`/`CreateCategoryAsync`. `Project` already has this lifecycle (deactivation
phase, immediately before Phase 21): `Project.IsActive`, `Rename`, `Deactivate`, a filtered unique
index on `(OrganizationId, Name)`, and an active-only filter on the Create Ticket dropdown and
`TicketService.CreateAsync`.

Team is not a simple reference entity the way Project is. It sits at the center of the data model:

```
Organization
    -> Team
         -> TeamMember
         -> Category -> Ticket
         -> Ticket -> Project (optional)
```

A Team owns Categories (`Category.TeamId`, `RESTRICT` delete), TeamMembers (composite PK,
`RESTRICT` delete), and Tickets (`Ticket.TeamId`, `RESTRICT` delete). Deactivating a Team
therefore raises questions Project deactivation never had to answer: what happens to its
Categories, its TeamMembers, its historical tickets, and its analytics/workload figures?

## Decision
Team and Category deactivation are both **soft, terminal, and non-cascading** — the same shape as
Project's, extended with one additional rule specific to Team's ownership of Category:

1. **`Team.IsActive` / `Category.IsActive`**, both defaulting to `true`, with `Rename`/`Deactivate`
   domain methods mirroring `Project`'s exactly. Neither entity ever gets a hard-delete path.
2. **Filtered unique indexes** — `(OrganizationId, Name) WHERE is_active` for Team,
   `(TeamId, Name) WHERE is_active` for Category — so deactivating either frees its name for reuse,
   consistent with Project's existing index.
3. **No cascade from Team to Category.** Deactivating a Team does **not** touch any Category row
   under it. A Category's own `IsActive` is independent of its Team's. This was the one genuinely
   new decision Team deactivation required (Project has no children to cascade to):
   - **Rejected: auto-deactivate every Category under a deactivated Team.** This would be an
     extra write for every category on every deactivation, would make a Category's `IsActive`
     lie about its own state (it would read "active" in isolation while being functionally
     unusable), and would need to be *reversed* if the product ever adds Team reactivation later
     — a second migration of intent with no current requirement driving it.
   - **Chosen: filter Category availability by its Team's `IsActive` at the one place that
     matters** — `TicketQueryService.GetCreationOptionsAsync` only ever queries categories under
     teams already filtered to `IsActive`, and `TicketService.CreateAsync`'s own active check now
     inspects both `Team.IsActive` and `Category.IsActive` independently. A category on a
     deactivated team is therefore unavailable for new ticket creation exactly as if it had been
     deactivated itself, without any write to the `categories` table and without its own
     `IsActive` becoming a misleading signal.
4. **No cascade to TeamMember, Ticket, TicketComment, or TicketEvent, ever.** Deactivating a Team
   changes exactly one column on one row (`teams.is_active`). Every FK from Ticket/Category/
   TeamMember to Team remains `RESTRICT` and is never evaluated by deactivation, since the Team
   row is never removed.
5. **Historical integrity is a consequence of (4), not a special case.** `TicketQueryService`,
   `AttentionQueryService`, and `AnalyticsQueryService` scope by `OrganizationId` and
   role/team-membership — none of them filter on `Team.IsActive`, so a ticket already filed
   against a deactivated team remains fully visible, fully workable through its existing workflow,
   and fully counted in historical analytics/workload figures. `TicketAccessPolicy` is untouched;
   a deactivated team is not a new way to gain or lose ticket-scope access — it only stops being an
   option for *new* tickets and new categories.
6. **Team membership is unaffected.** `TeamMember` rows on a deactivated team are neither deleted
   nor altered. An Admin can still view/manage a deactivated team's membership from its detail
   page; nothing about `AddMemberAsync`/`RemoveMemberAsync`/`SetTeamManagerAsync` changed.

## Alternatives considered
- **Cascade-deactivate Categories with the Team**: rejected — see (3) above.
- **Block Team deactivation while open tickets exist**: rejected — this would make deactivation
  unpredictable (available today, blocked tomorrow depending on ticket state) for no integrity
  benefit, since open tickets are completely unaffected by deactivation either way (they keep their
  `TeamId`, keep their workflow, and are never auto-closed or reassigned).
- **Hard-delete on deactivation attempt if the team has no tickets, soft-deactivate otherwise**:
  rejected — a conditional lifecycle with two different behaviors depending on incidental history
  is exactly the kind of complexity CLAUDE.md's own review checklist flags; one behavior, always,
  is simpler to reason about and test.
- **A separate "Team status" enum (Active/Archived/Retired) instead of a boolean**: rejected — no
  product requirement distinguishes more than two states, and `Project.IsActive` already
  established the boolean convention; introducing an enum for Team alone would be an unjustified
  asymmetry between two structurally similar entities.

## Consequences
- `Team` and `Category` gain `IsActive`/`Rename`/`Deactivate`, mirroring `Project` exactly in
  shape (see `docs/database.md` §2/§4 for the resulting schema).
- One migration (`AddTeamAndCategoryIsActive`): two `boolean NOT NULL DEFAULT true` columns, two
  unique indexes replaced with their filtered equivalents. No data migration needed — every
  existing row defaults to active.
- `TicketService.CreateAsync` and `TicketQueryService.GetCreationOptionsAsync` each gained one
  additional, independent active check (Team, then Category) alongside their existing
  organization-boundary checks — no other Application service needed to change.
- `AttentionQueryService`, `AnalyticsQueryService`, and `TicketAccessPolicy` are deliberately
  **unchanged** — historical visibility, workload, and authorization all already worked correctly
  for any team a caller has ever belonged to, active or not, and this decision preserves that
  without adding a new `IsActive` check to any of them.
- Team's admin surface (`/Admin/Teams/Details`) becomes the natural place to rename/deactivate the
  team and manage its categories (add/rename/deactivate), alongside its existing membership
  management — no new page, no new navigation depth.
