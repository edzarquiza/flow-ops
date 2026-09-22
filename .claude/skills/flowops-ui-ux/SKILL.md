---
name: flowops-ui-ux
description: UI design intent and accessibility standards for FlowOps Razor Pages. Use when building or editing a Razor Page, partial, view component, or wwwroot CSS/JS.
paths: ["**/Web/Pages/**", "**/Web/Views/**", "**/wwwroot/**"]
---

# FlowOps UI conventions

Full standards in CLAUDE.md §11.1 and §22. This skill is the quick check while you're actually
writing markup.

## Tone check before you write a screen

FlowOps is an operational tool someone stares at for eight hours, not a marketing page. Before
adding a screen or component, ask: does this help someone answer "what needs my attention?"
faster? If a piece of UI is decorative rather than informational, cut it.

Banned reflexes: gradients for decoration, animated counters, hero sections, emoji as UI,
glassmorphism, more than 4 charts on one page, a KPI card with no drill-through.

## Terminology

Use: ticket, work item, requester, assignee, team, category, priority, SLA, resolution.
Project planning (ADR-0029) also uses: sprint, sprint backlog, board.
**Never**: epic, story, story points, velocity, sprint goal, swimlane, burndown. A service
desk technician should never hit a word that assumes software-dev context.

## PageModel discipline

- PageModel = bind input → call an application service → map to view model → return. If you're
  writing SLA math, an authorization check, or an EF query inline in a PageModel, that logic
  belongs in `Application` or `Domain` — move it, don't leave it here even "temporarily."
- Every state-changing form needs antiforgery (Razor Pages default — don't disable it) and must
  work with JavaScript off, degrading to a full-page post.

## Accessibility, every time you touch markup

- Tabular data → real `<table>` with `<th scope="col">`/`<th scope="row">`, not styled `<div>`s.
- Every form input has a bound `<label>`; validation messages use `aria-describedby`.
- Status/priority/SLA urgency is never color-only — pair with text or an icon.
- Visible focus states preserved (don't `outline: none` without a replacement).
- Empty states say something useful ("No work is at risk right now"), not a blank panel.
- Relative times ("42m left", "2h over") carry the absolute time in a `title` attribute.

## Before you finish

Tab through the page with keyboard only. If you can't reach or trigger every action, fix it
before moving on — this isn't a later polish pass, it's part of "done" per §22.
