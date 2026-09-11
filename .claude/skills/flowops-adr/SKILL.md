---
name: flowops-adr
description: Create a new Architecture Decision Record for FlowOps. Use whenever a significant architectural, security, data-integrity, or scope decision is made — including a deliberate decision NOT to adopt something (a technology, pattern, or dependency).
argument-hint: [short-title]
---

## Existing ADRs

!`ls docs/adr/ 2>/dev/null || echo "docs/adr/ does not exist yet"`

## Instructions

Create `docs/adr/NNNN-$ARGUMENTS.md`, where `NNNN` is the next sequential 4-digit number after
whatever's listed above (start at `0001` if the directory is empty or missing).

Use exactly this structure, per CLAUDE.md §20:

```markdown
# NNNN. <Title>

## Status
Accepted

## Context
<What problem or question forced this decision. 2-4 sentences.>

## Decision
<What was decided, stated plainly.>

## Alternatives considered
<Each alternative with one line on why it was rejected. Include "do nothing" if relevant.>

## Consequences
<What this makes easier, what it makes harder, and any known trade-off it deliberately accepts.>
```

Keep the whole file under one page. If the decision is a deliberate non-adoption (e.g. "we did
not add Redis"), say so directly in the Decision section rather than only implying it — a
recorded non-decision is exactly as valuable as a recorded decision here.

Do not create the file if the decision doesn't rise to §19.3's bar (architecture, security, data
integrity, business rules, scope, or an irreversible choice) — say so instead and explain why an
ADR isn't warranted for this one.
