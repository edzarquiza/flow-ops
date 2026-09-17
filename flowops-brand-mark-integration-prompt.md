# FlowOps — Replace the brand mark with the ring-and-wave logo

This **replaces** the existing three-node brand mark (open → in progress → resolved) documented in
`docs/ui/design-system.md`'s appendix. That's a deliberate decision, not an addition — confirm you
understand it as a replacement before starting, since the old mark is referenced in at least the
design doc and likely inline in the layout.

Presentation layer only. No domain logic, no business rules, no migrations.

---

## Step 0 — Find every place the current mark exists

The old three-node mark may be duplicated inline rather than componentized — that's been the
recurring problem with this codebase's UI work (icons, headings, and badges have each turned up
duplicated across pages before). Before changing anything:

1. Grep for the old mark's SVG (three `<circle>` elements with the specific coordinates from the
   design doc's "Brand mark" appendix section, or search for `0F766E` and `5FD3C4` together in one
   SVG block).
2. List every file it appears in — expect at minimum `_Layout.cshtml`'s sidebar brand link, but
   check Login, Register, Access Denied/Error (if those were already given the shell treatment from
   the audit pass), and any favicon reference in `<head>`.
3. Check whether it's already defined once in the icon helper class (per the UI audit: "~55
   hand-drawn inline SVGs in one C# class") or duplicated per-file. **If duplicated, this pass fixes
   that too** — one definition, referenced everywhere.

Report what you find before editing.

---

## 1. The primary mark — for use inside the app (sidebar, any in-app brand reference)

Colour comes from the existing token, not a hardcoded hex — this is a CSS-driven mark, not a static
image, so it should follow the same tokenisation rule as everything else in the app.

```html
<svg class="brand-mark" width="24" height="24" viewBox="0 0 24 24" aria-hidden="true">
  <circle class="brand-mark__ring" cx="12" cy="12" r="9.5" fill="none"></circle>
  <path class="brand-mark__wave" d="M4.5,12 Q8.25,5 12,12 T19.5,12" fill="none"></path>
</svg>
```

```css
.brand-mark { flex-shrink: 0; }
.brand-mark__ring { stroke: var(--fo-teal); stroke-width: 1.3; }
.brand-mark__wave { stroke: var(--fo-teal); stroke-width: 1.9; stroke-linecap: round; }
```

`aria-hidden="true"` because it's always paired with the visible "FlowOps" text next to it — the
same pattern already used for the five nav icons, which are decorative given their label is always
present.

**Add this as one method in the existing icon helper class** (wherever the ~55 hand-drawn icons
live), not inline in each `.cshtml` that needs it. Every place found in Step 0 should call that one
method.

---

## 2. The favicon — a separate asset, separate weight, hardcoded colour is correct here

Same geometry, thicker strokes. At true 16px the 1.3/1.9px weights from §1 are close to sub-pixel
and will blur — this is a deliberate second pass for the one size that needs it, not a mistake.

`wwwroot/favicon.svg`:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
  <circle cx="12" cy="12" r="9.3" stroke="#5FD3C4" stroke-width="2" fill="none"/>
  <path d="M4.5,12 Q8.25,5 12,12 T19.5,12" stroke="#5FD3C4" stroke-width="2.6" fill="none" stroke-linecap="round"/>
</svg>
```

**The hardcoded hex here is correct, not an exception to flag** — this file is a standalone static
asset loaded directly by the browser via `<link rel="icon">`, with no access to the app's CSS custom
properties. It's the one legitimate place a literal colour value belongs.

```html
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
```

Modern evergreen browsers (Chrome, Firefox, Edge, and current Safari) render SVG favicons directly —
no raster fallback is strictly required for the tab icon itself. **Two things still need raster
PNGs and are a one-time asset-export task, not something to automate with a new dependency:**

- `apple-touch-icon.png` (180×180) — iOS home-screen icon, requires PNG, no SVG support.
- A `favicon.ico` or 32×32 PNG fallback if you want to support older browsers that predate SVG
  favicon support.

**Do not add an image-processing NuGet package for this.** It's a one-time export of a six-line SVG.
Either export it yourself from any vector tool / browser devtools / online SVG-to-PNG converter, or
tell me and I can generate the PNGs directly and hand them back as files. Flag this as an open task
in your final report rather than silently skipping it or pulling in a dependency to solve it.

```html
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
```

---

## 3. Update every reference found in Step 0

Replace the old three-node mark with `.brand-mark` (§1) everywhere it appeared — sidebar brand link
at minimum. If Login/Register/Access Denied/Error currently show a brand mark (some may, per the
earlier shell-treatment work), update those too, at whatever size is appropriate for that context —
the 24×24 viewBox scales cleanly, just adjust the `width`/`height` attributes, not the geometry.

---

## 4. Update the design document

`docs/ui/design-system.md` still describes the old three-node mark in its appendix. Given the recent
audit's core finding was that this document has drifted from the implementation in multiple ways,
**do not let this be an eighth one** — update it in the same pass as the code, not later:

- Replace the old "Brand mark" markup with §1's version.
- Record §2's favicon as a separate, intentionally heavier-stroke variant, with the one-sentence
  reasoning: a favicon needs its own weight pass at its actual render size, not a scaled copy of the
  full-size mark.
- Note explicitly that the favicon SVG's hardcoded hex is a deliberate, documented exception to the
  "no colour outside the token set" rule, and why.

---

## 5. Verification

- [ ] Zero remaining references to the old three-node mark anywhere in the codebase — grep confirms.
- [ ] `.brand-mark` is defined once, in the icon helper class, and referenced (not duplicated) at
      every call site.
- [ ] Mark renders correctly in the actual browser sidebar at its real size, ring and wave visibly
      different weights.
- [ ] Favicon renders in an actual browser tab, both a light-theme and dark-theme browser skin if
      you can check both — confirm it doesn't disappear on either.
- [ ] `apple-touch-icon.png` either exists or is explicitly flagged as outstanding in your final
      report — not silently skipped.
- [ ] Design doc's Brand Mark section matches the shipped code exactly.
- [ ] `dotnet test` passes; `dotnet format --verify-no-changes` clean.
- [ ] No new NuGet package was added for this.

## Final output

Every file changed · where the old mark was found duplicated, if it was · confirmation the design
doc was updated in this same pass · the favicon PNG status (generated, or flagged as outstanding
with what you need from me to finish it).
