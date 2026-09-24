# 0032. Team, category, and project reactivation

## Status
Accepted

## Context
ADR-0022 made Team and Category deactivation soft and non-cascading, but deliberately left it
one-directional — there was no requirement driving a reversal at the time, so `Deactivate()` was
the only lifecycle method offered, and Project (added later, following the same shape) inherited
the same one-way behavior. In practice, deactivation is routinely used for things that come back:
a team stood down for a quarter, a seasonal project, a category retired and then needed again. The
current workaround — recreating the row under the same name — silently loses the original row's
history association for any ticket created *after* the recreation (existing tickets still point at
the old, still-inactive row), which is worse than simply restoring it. Reactivation is also not
free to add: Team, Category, and Project all enforce name-uniqueness only among *active* rows via a
filtered unique index, precisely so a deactivated name becomes reusable — which means restoring an
old row can now collide with a same-named row created in the meantime.

## Decision
`Team`, `Category`, and `Project` each gain a `Reactivate()` domain method, symmetric with
`Deactivate()` and with the `Organization`/`ApplicationUser` reactivation already in place at the
platform level. `TeamService.ReactivateAsync`, `CatalogService.ReactivateCategoryAsync`, and
`CatalogService.ReactivateProjectAsync` apply the same authorization gates as their `Deactivate*`
counterparts (Admin-only, organization/team-scoped), reject a no-op ("already active") explicitly,
and guard the name-collision race exactly as `RenameAsync`/`CreateAsync` already do: an up-front
active-name existence check for a friendly message, plus a `DbUpdateException` catch around
`SaveChangesAsync` as the final guard against the filtered unique index. The Admin UI exposes a
plain "Reactivate" button (no confirmation dialog — reactivating is the safe direction, unlike
deactivating) on each inactive team, category, and project row, mirroring the existing
`Platform/Organizations` and `Platform/Users` reactivation pattern.

Category reactivation is deliberately independent of its parent Team's `IsActive` state, keeping
ADR-0022's non-cascading rule intact in both directions: a category may be reactivated while its
team is still inactive, exactly as it could previously stay active while its team was deactivated.
It simply won't be *offered* on Create Ticket until the team is active too — the UI surfaces this
directly next to the Reactivate button rather than leaving it to be discovered.

## Alternatives considered
- **Keep deactivation one-way, tell users to recreate the row.** Rejected — loses the "same
  category/team/project" identity for new tickets going forward, contradicting the entire point of
  soft-delete over hard-delete.
- **Cascade Category reactivation to also reactivate its Team.** Rejected — breaks ADR-0022's
  explicit independence rule for no stated benefit, and would surprise an Admin who reactivates one
  category expecting only that one row to change.
- **Silently rename on collision (e.g. append a suffix) instead of rejecting.** Rejected — every
  other mutation in this codebase (`RenameAsync`, `CreateAsync`) rejects with a friendly message on
  collision rather than inventing a name on the caller's behalf; reactivation follows the same
  convention for consistency.

## Consequences
Makes deactivation a genuinely safe, reversible action instead of a one-way gate, which should
reduce hesitation to deactivate stale teams/categories/projects in the first place. Makes the
Admin UI slightly busier (one more button per inactive row) and requires the "won't appear until
its team is active" caveat to be explained in-place for categories. Accepts the same rare
name-collision-on-reactivation race window every other rename/create path already accepts, guarded
by the same two-layer check rather than a stronger mechanism (e.g. locking), consistent with the
rest of the codebase.
