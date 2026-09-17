# 0028. Scoped exception to "no unnecessary JavaScript": themed dropdown menus

## Status
Accepted

## Context
CLAUDE.md §11.1's "no unnecessary JavaScript" rule has held since Phase 4: every form, workflow
action, and confirmation on FlowOps works with JavaScript disabled, and every existing script
(`copy-button.js`, `dashboard-filters.js`) is a small, optional, progressive-enhancement layer over
markup that already functions without it. Native `<select>` elements follow the same rule — the
closed control is CSS-styled (a custom chevron, matched height/border to every other input), but
the *open* option list has always been left as the browser's own native popup, since no CSS
technique can restyle it, and no JS component existed to replace it.

The product owner asked directly for a themed dropdown menu (the open list currently breaks the
dark theme with a stock white/OS-styled popup) and explicitly asked to overwrite the no-JS rule to
get it, rather than accept the native list as a permanent constraint.

## Decision
**Scoped exception, not a repeal.** CLAUDE.md §11.1 now reads: "No unnecessary JavaScript. Forms,
confirmations, and workflow actions must work with JavaScript disabled (progressive enhancement
only). Exception (ADR-0028): a custom-styled dropdown/select component may use JavaScript, since no
CSS-only technique can restyle a native `<select>` popup to match the app's theme. Every such
component must still degrade to a plain, fully functional native `<select>` with JavaScript
disabled — never broken, only less styled." Nothing else on the site (forms, ticket workflow
actions, confirmations, invite/reset flows) is affected — those all remain zero-JS by the
unmodified rule.

The component itself keeps a real, hidden-but-present native `<select>` in the DOM as the actual
form control (so form submission, validation, and the no-JS fallback are all the same element that
has always worked), with a JS-built, ARIA `listbox`/`option`-roled popup layered visually on top of
it that stays in sync with the native element's value and is removed entirely (falling back to the
plain native control) if JavaScript is disabled or fails to load.

## Alternatives considered
- **Repeal the no-JS rule entirely**: rejected — the product owner's own follow-up narrowed the ask
  to dropdowns specifically once the trade-off was made explicit; every other page's zero-JS
  guarantee (accessibility, resilience, simplicity) stays intact.
- **A CSS-only theming attempt (`::picker`/`<selectlist>`-style proposals)**: rejected for now —
  not reliably supported across the browsers this app targets; revisit if/when that changes.
- **Leave the native list as a permanent, accepted limitation**: was the status quo; rejected per
  the product owner's explicit request.

## Consequences
- A new `wwwroot/js/custom-select.js` (loaded site-wide from `_Layout.cshtml`, not per-page) plus
  new CSS for the themed trigger/popup; `script-src 'self'` needs no change (no inline script, no
  new host).
- Every existing `<select>` gains the enhancement automatically on page load — one shared
  component, not a per-page reimplementation, and no `.cshtml` markup changes needed anywhere.
- **Real finding made building this**: the CSP had no `img-src` directive, so `default-src 'none'`
  was silently blocking *every* image on the site, including the plain `<select>`'s own `data:`
  URI chevron — which had therefore never actually rendered in a real browser, before or after this
  change, independent of anything else in this ADR. Two fixes followed from this: the JS-enhanced
  trigger's caret is drawn as a real bordered `<span>` (a CSS corner rotated 45°/-135°), needing no
  image and no CSP change at all; and `img-src 'self'` was added (same-origin only, never `data:`,
  never a wildcard) so the plain-`<select>` fallback's chevron — now two real files under
  `wwwroot/img/` instead of inline `data:` URIs — can load for the JS-disabled case that ADR-0028
  itself already treats as a "less styled, never broken" degradation.
- The native option list remains the fallback and the JS-disabled experience — this is the one
  place on the site where a feature is now visually different with JavaScript on vs. off, a
  deliberate, narrow departure from "every page looks and works the same either way."
