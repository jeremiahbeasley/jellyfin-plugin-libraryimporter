# Library Importer — Interface Design System

The control surface for a Jellyfin metadata-import plugin. Lives **inside Jellyfin's
admin SPA**, so it must feel native while remaining independently accessible.

## Direction & Feel
**Projection-booth control desk.** Calm, purposeful, native to Jellyfin. One confident
"run the reel" action (Run Now) with live feedback. Character comes from the run panel and
the focus state — never from fighting the platform.

## Hard Constraints (do not regress)
- **WCAG 2.2 A + AA.** Text ≥4.5:1, UI/focus indicators ≥3:1 (1.4.11), status conveyed by
  **text/icon, not color alone** (1.4.1), targets ≥24px (we use ≥44px), every field has
  `<label for>`, live regions use `role="status"`/`aria-live`, progress uses `role="progressbar"`.
- **TV / D-pad first.** No hover-only behavior. Focus ring is the loudest thing on screen.
- **Controlled palette, NOT inherited.** We do not adopt arbitrary user skins — AA contrast
  can't be certified against an unknown skin. Tokens below match Jellyfin's default dark look.
- **SPA gotcha:** Jellyfin keeps only `div[data-role="page"]`. ALL markup + the `<script>`
  must live inside `#LibraryImporterConfigPage`. No inline `onclick` — wire with
  `addEventListener` (CSP-clean + remote-friendly).
- **Build:** `rm -rf bin obj` first (stale embedded-resource cache), then
  `/root/.dotnet/dotnet build -c Release` on the host.

## Tokens (scoped under `#LibraryImporterConfigPage`)
Depth strategy: **borders-only** with rgba tints (Jellyfin is flat; shadows fight it).
Same hue everywhere, lightness-only shifts.

```
Surfaces:  --li-surface rgba(255,255,255,.04) → --li-surface-2 .07 → inset rgba(0,0,0,.28)
Borders:   --li-border-soft .07 → --li-border .13 → --li-border-strong .24
Text (4):  --li-text .96 / --li-text-2 .80 / --li-text-3 .62 / on-accent #06151c
Meaning:   --li-accent #00a4dc (Jellyfin teal, near-black text = ~7:1)
           --li-beam   #ffb300 (projector beam: running lamp AND focus ring)
           --li-ok     #5cc66a   --li-danger #e2574e / bg #c62f27
Radius:    sm 4 (inputs/buttons) · md 8 (cards) · lg 14 (sections/modal)
Spacing:   base 8px → 4 / 8 / 16 / 24 / 32
Numbers:   font-variant-numeric: tabular-nums (counters = the "data" type)
```

## Signature: Focus = the projector beam
Amber double-ring, guaranteed ≥3:1 on any surface, thicker on TV:
```css
:focus-visible { box-shadow: 0 0 0 2px #0a0d10, 0 0 0 5px var(--li-beam); }
.layout-tv :focus-visible { box-shadow: 0 0 0 3px #0a0d10, 0 0 0 8px var(--li-beam); }
```
`:focus { outline:none }` only paired with a `:focus-visible` replacement — never bare.
Respect `prefers-reduced-motion` (kills the lamp pulse).

## Component Patterns
- **Run panel** (`.li-run`): lamp (color) + state word (text) → `data-state` idle/running/done/error;
  progress bar; `aria-live` status line; 4-up `.li-counts` grid. Drive via Jellyfin
  `POST ScheduledTasks/Running/{id}` + poll `ScheduledTasks/{id}` (`State`,
  `CurrentProgressPercentage`). Real counters come from `config.LastRun`. Re-attach on load.
- **Section card** (`.li-card`): uppercase tracked `<h2>`, surface tint, soft border.
- **Library row** (`.li-lib`): name + `type · N paths` sub, then inline `.li-check` toggles.
- **Override card** (`.li-ov`): body + right-aligned Edit/Delete (danger = red bg + white text).
- **Modal** (`.li-modal`): `role="dialog" aria-modal="true"`, Esc closes, focus first field.
- **Buttons** (`.li-btn` / `-primary` / `-danger` / `-lg`): real `<button type="button">`,
  min-height 44px.
- **Chips/people**: chips are `<button>`s with `aria-label="Remove X"`.

## Escaping
All interpolated values go through `esc()` before innerHTML. Always.
