# 0031. User Appearance Preference and Theme Delivery

## Status
Accepted

## Context
FlowOps was dark-only. Users asked for a Light theme and a personal choice. The theme must not flash, must
work under the CSP (`script-src 'self'; style-src 'self'`, no inline script), must not depend on a
client-only store for authenticated users, and must not turn into a generic preferences framework.

## Decision
- **Per-user column, not a table.** `AspNetUsers.appearance` (`Dark` | `Light` | `System`), text with a
  check constraint (ADR-0009), default `Dark`. Existing users receive `Dark` from the column default.
  `System` is stored as itself so the theme keeps following the OS.
- **Server-rendered `<html data-theme>`.** The layout reads the saved value (one primary-key lookup) and
  emits the attribute, so the first response is already themed: no flash, no inline script. `system` is
  resolved by CSS (`prefers-color-scheme`) alone.
- **Tokens, not themed components.** Dark stays the `:root` values; Light is a token override block (plus one
  identical block for System under the media query). Semantic colours are exposed as channel tokens so tints
  follow the theme.
- **Preview is an enhancement.** An external `appearance-preview.js` sets the same attribute on radio
  change; the saved, server-rendered value stays authoritative and unsaved previews are discarded.
- **Anonymous pages are always Dark** (no cookie, no anonymous preference). Demo personas may change theirs.
- **An invalid stored value is an error, not a silent default**: the column is checked in the database and
  read with the standard text conversion, so corruption surfaces instead of being hidden.
- **Deliberately not adopted:** a generic key/value preferences table, `localStorage` as the source of
  truth, an inline boot script or `unsafe-inline`, CSS `light-dark()` (silent failure in older browsers).

## Alternatives considered
- Cookie/`localStorage` only — flashes, is not per account, and is not the source of truth.
- Organization-level theme — appearance is personal.
- Preferences table — speculative abstraction for one setting.

## Consequences
One extra indexed lookup per authenticated page render (the layout already makes several). Light is a
maintained value set: a test fails if a themed token is not restated. A light-OS user sees a dark login
page until they choose Light or System; adding a cookie later would be a separate, explicit decision.
