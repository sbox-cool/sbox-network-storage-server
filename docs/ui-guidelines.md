# Owner console visual system

Authoritative spec for the shipped owner console (`src/SboxNetworkStorage.Server/Views/Owner`,
`wwwroot/`). This document describes what is implemented, not a proposal. The
design reference is `docs/proposals/ui-redesign/DESIGN.md`; the mockups under
`docs/proposals/ui-redesign/mockup/` are reference only. The console and
sboxns.com share one token system and the same lime brand.

## Brand

The brand is the approved 02 Sharp companion: an angular lime cube (lime
`#70e000` with `#a3f55c`/`#c3ff8a` top and `#58b300`/`#469000` side shading),
shallower top, original smug eyes, brows and mouth, face vertically centered.
No blue anywhere. Canonical source:
`sboxns.com/images/icon.svg` (final centered transform
`matrix(1 -.166667 0 1 0 0.778336)`).

Shipped assets (`wwwroot/img`, served as embedded resources under
`/owner-assets/`):

| File | Route | Use |
| --- | --- | --- |
| `sbox-network-storage-icon.svg` | `/owner-assets/icon.svg` | Static full-colour cube |
| `sbox-network-storage-favicon.svg` | `/owner-assets/favicon.svg` | Adaptive favicon: full cube with face, simplified faceless cube at 24px and below |
| `sbox-network-storage-icon-512.png` | `/owner-assets/icon-512.png` | 512px raster, also the `apple-touch-icon` |

Rules: the topbar, sidebar and auth hero use `/owner-assets/icon.svg`, never
the adaptive favicon or a recolored tile. The favicon is head-only. There is
no brand tile or glyph wrapper. The standalone auth
hero image carries `alt="Network Storage logo"`; brand images next to their
own labelled link or adjacent name text stay decorative (`alt=""`). The SVG
`<title>` reads `Network Storage`.

## Themes

System (default), Dark, Light, High contrast. System resolves to Dark or Light
from `prefers-color-scheme`. The choice is stored in `localStorage` under
`sbox-ns-theme` and applied before first paint by the external `theme.js`
(no inline script). Themes only redefine tokens; components never branch on
the theme name.

Retired hue-variant themes map forward so stored preferences survive:
`cooldark`, `slate`, `mint` and `warm` render as Dark; `coollight` renders as
Light. High contrast is the only extra theme because it does a different job.
Unknown saved values fall back to System; only the listed legacy keys map forward.

| Token | Dark | Light | High contrast | Use |
| --- | --- | --- | --- | --- |
| `--bg` | `#0a0a0b` | `#ffffff` | `#000000` | Canvas |
| `--side` | `#0a0a0b` | `#fafafa` | `#000000` | Sidebar |
| `--bg-raised` | `#141416` | `#ffffff` | `#0d0d0d` | Overlays only |
| `--fill` | `#17171a` | `#f4f4f5` | `#1a1a1a` | Secondary buttons, chips, hover, current nav |
| `--fill-strong` | `#26262b` | `#ffffff` | `#3a3a3a` | Hover on fills, segmented thumb |
| `--track` | `#2c2c31` | `#e4e4e7` | `#555555` | Switch off, avatar |
| `--line` | `#222226` | `#e4e4e7` | `#6e6e6e` | Panel outlines |
| `--line-strong` | `#313137` | `#d4d4d8` | `#a3a3a3` | Inputs |
| `--line-faint` | `#18181b` | `#efeff1` | `#3d3d3d` | Row and section rules |
| `--text` | `#ededef` | `#09090b` | `#ffffff` | Primary text |
| `--text-2` | `#8f8f98` | `#62626b` | `#d4d4d4` | Secondary text, labels, meta |
| `--text-3` | `#66666f` | `#86868f` | `#a3a3a3` | Placeholders, disabled, separators, icons beside a label |
| `--accent` | `#70e000` | `#70e000` | `#70e000` | Fills: primary button, checked, selection tint |
| `--on-accent` | `#0b1400` | `#0b1400` | `#000000` | Text on accent fills |
| `--accent-fg` | `#70e000` | `#3b7600` | `#70e000` | Accent as text or icon: links, current nav icon, code strings |
| `--success` | `#4ade80` | `#15803d` | `#4ade80` | State only |
| `--warning` | `#fbbf24` | `#b45309` | `#fcd34d` | State only |
| `--danger` | `#f87171` | `#dc2626` | `#fca5a5` | State only |
| `--info` | `#60a5fa` | `#2563eb` | `#93c5fd` | State only, code identifiers |
| `--code` | `#0f0f11` | `#f6f6f7` | `#0d0d0d` | Code and JSON blocks |

Lime fills use `--accent`; lime text and icons use `--accent-fg`, never
`--accent` (the light theme needs the darker `#3b7600` for contrast). No raw
hex outside theme blocks. `--text-3` never carries data or a label. State
colors mean state, always with a word.

Contrast (WCAG, verified): accent text on canvas 11.64 (Dark), 5.56 (Light),
12.35 (High contrast); button text on accent 11.08 (Dark and Light), 12.35
(High contrast); darker lime `#3b7600` on `#f4f4f5` fills 5.06. Secondary text
on canvas is 6.17 (Dark) and 6.04 (Light); tertiary stays non-text at 3:1 or
better. The full table is in `docs/proposals/ui-redesign/DESIGN.md`.

## Surfaces and depth

- Near-black Dark canvas, white Light canvas, black High contrast canvas.
- Content is outlined: panels, cards, notices and tables are a 1px `--line`
  outline with no fill.
- Fills are for controls: secondary buttons, chips, hover and the current nav
  item.
- Only overlays are raised: dialogs, menus and toasts use `--bg-raised` and
  `--shadow`. Nothing else casts a shadow.
- Sticky bars blur: the topbar uses 82% `--bg` with `saturate(180%)
  blur(16px)`.

## Shape

4px checkboxes and method tags, 6px buttons/inputs/chips/nav items, 10px
panels and tiles, 12px menus, 14px dialogs, 18px top corners of mobile bottom
sheets, pill for status tags, avatar, switches and toasts.

## Type

System UI stack; `ui-monospace` for IDs, keys, commands, code and JSON. Sizes:
12px meta and table headers, 13px tables and secondary text, 14px body, 22px
page titles (28px mobile large titles come from the shell). Titles track at
-0.02em. Tabular numbers in tables, stats and counters. Sentence case
everywhere; no uppercase labels.

## Icons

Solid glyphs vendored in `Owner/OwnerIcons.cs` from
`docs/proposals/ui-redesign/mockup/icons.json`: Heroicons 20 solid (MIT,
Tailwind Labs) for navigation and actions, Simple Icons (CC0) for brand marks
(GitHub, Discord). Inline SVG only: no icon font, no CDN. Glyphs inherit
`currentColor`: `--text-3` at rest beside a label, `--accent-fg` when current.
16px in controls and nav, 18px for the Discord mark, 20px for the mobile nav
toggle. Icon-only buttons need a tooltip and `aria-label`.

The full Heroicons MIT notice is vendored in `wwwroot/vendor/heroicons.LICENSE.txt`,
embedded in published binaries, and served at `/owner-assets/heroicons.LICENSE.txt`
through the footer's Icon license link.

## Controls

Primary button: `--accent` fill, `--on-accent` text, one per view. Secondary:
`--fill`, no border. Icon and ghost buttons: transparent, `--fill` on hover.
Danger: 14% danger tint with danger text; the action itself confirms in a
modal. Inputs: 36px, 1px `--line-strong`, transparent; focus is an accent
border plus a 3px 25% accent halo. Status tags are pills: 13% state tint with
a state word. Tables: 34px headers, `--line-faint` rules, tabular numbers,
70% `--fill` row hover. Focus is a 2px accent outline with 2px offset
everywhere (3px in High contrast).

## Shell

- Topbar: 48px, sticky, blurred. Brand cube (24px), breadcrumb trail, Discord
  icon button (18px mark, correctly sized), owner menu. The theme picker lives
  in the owner menu as System, Dark, Light, High contrast; logged-out pages
  keep the same inline select. The owner menu also holds Security, Server,
  Docs, Discord, the version and Log out (a plain POST form, antiforgery
  token kept).
- Sidebar: 220px (200px below 1100px), sticky with its own scroll. Groups use
  plain sentence-case labels. The current item uses `--fill` plus an
  `--accent-fg` icon (state, not decoration). Below 760px the hamburger opens
  the nav as a panel; breadcrumbs collapse to the current page.
- Content: fluid against the sidebar with a consistent 24-32px gutter. Only
  long prose (`.prose`, 72ch) and narrow forms take a readable cap. Mobile
  dialogs become bottom sheets with full-width actions.
- Server notices render once as the compact dismissible alert strip above the
  shell (demo, plain HTTP, update).

## Copy

Sentence case, no em dashes, no filler, errors state cause and fix, no emoji,
no invented numbers in shipped UI. Ranges use a plain hyphen (`1-50`).

## Dialogs, toasts, no-JavaScript

One modal vocabulary: a native `<dialog>` with a title, body, primary action
and cancel action. Destructive and irreversible actions confirm in the shared
dialog; typed-name confirmation stays for project and record deletes, checked
server-side. Never call `window.confirm`. Every dialog form is a plain server
`<form method="post">` reachable without JavaScript; without JavaScript the
form POSTs directly. Success toasts name what changed, render from one-shot
TempData flashes in a polite live region, and stay visible as inline notices
without JavaScript. One-time secrets use a persistent panel, never a toast.

## Assets

No inline `<script>`, `<style>` or `style=""` in views. CSS in `wwwroot/css`,
JS in `wwwroot/js`. No CDN dependencies: the console works offline on a LAN.

## Checklist before merging dashboard changes

1. `rg -n '—|–' src/SboxNetworkStorage.Server/Views src/SboxNetworkStorage.Server/Owner/*.cs`
   is empty. Controller messages are visible text too.
2. No `text-transform: uppercase`, one-side accent rails, `linear-gradient`
   (outside the brand artwork itself), `box-shadow` beyond overlay depth, raw
   hex outside theme token blocks, or `--accent-2`/purple references in
   `wwwroot/css`. Links, current nav icons and code strings use
   `--accent-fg`, never `--accent`.
3. Every new view checked in Dark, Light and High contrast, desktop and
   760px mobile.
4. Empty, loading and error states exist for every new list or form.
5. `rg -n 'window\.confirm' src/SboxNetworkStorage.Server/wwwroot/js` is empty:
   destructive actions use the shared confirm dialog, and every dialog form
   works with JavaScript disabled. Toasts render from TempData flashes and
   stay visible as inline notices without JavaScript.
6. Brand images resolve to the `/owner-assets/` routes above; the Discord mark
   renders at 18px with a tooltip and label.
