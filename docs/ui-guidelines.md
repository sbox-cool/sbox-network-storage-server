# Dashboard UI guidelines

Rules for the owner dashboard (`src/SboxNetworkStorage.Server/Views/Owner`,
`wwwroot/`). They remove the patterns that make an interface read as
generated ("AI slop") and keep the dashboard plain, dense and predictable.

Sources: the [Gesso anti-slop rule catalog](https://github.com/Gesso-Build/skills/blob/main/skills/anti-slop/references/rules.md),
[anti-slop-design](https://github.com/Ferousco-dev/anti-slop-design) visual and
component patterns, and the sboxcool.com design guidelines. Rule ids in
brackets match the Gesso catalog so findings can be cross-referenced.

## Copy

- No em dashes (`—`) or spaced en dashes in any visible string, including
  tooltips, alt text and error messages [em-dash-copy]. Use a period, comma,
  colon or parentheses. Ranges use a plain hyphen (`1-50`).
- No eyebrows, kickers or "supertext": no small uppercase or accent-colored
  label sitting above a heading [hero-kicker-eyebrow, row-kicker-eyebrow].
  The heading carries the section. A page title may have one plain sentence
  under it, nothing above it.
- No uppercase body or label text [all-caps-body]. Table headers and form
  labels are sentence case at normal letter spacing.
- No emoji as icons [emoji-icon]. Use text, or nothing.
- No filler or benefit-speak ("seamless", "powerful", "effortless") and no
  "not X, but Y" cadence [benefit-speak, not-x-but-y-cadence]. Say what the
  control does.
- Error copy states what failed and what to do. No apologies
  [apologetic-error-copy].
- No invented numbers or placeholder data in shipped UI [fabricated-precision,
  lorem-ipsum]. Empty states say what is empty and how to fill it.

## Shape and borders

- No colored side rails: no `border-left`/`border-right` of 2px or more used
  as decoration, and never a side rail on a rounded box [edge-stripe]. Notices
  and alerts use a full 1px border in the state color, or a tinted
  background, not a stripe. A selected row or the current nav item may use a
  rail (state, not decoration).
- One radius scale: `--radius-sm` (4px) for inputs, buttons and chips,
  `--radius` (6px) for panels. Nothing larger [over-rounded-card]. No pill
  badges with heavy fills.
- No cards inside cards [nested-cards]. No card per table row
  [row-as-card]. Lists separate rows with spacing or one hairline, never a
  border on every side of every row [redundant-border].
- No heavy or colored drop shadows, glows or neon [heavy-box-shadow,
  dark-glow]. Elevation is a 1px border and a slightly lighter surface.
- No glassmorphism, `backdrop-filter` blur, gradients, gradient borders or
  gradient text [gradient-text, gradient-fill, gradient-border].

## Color

- Neutral surfaces, one accent per theme, used for primary actions, links,
  focus and the current nav item only. No second accent mid-page.
- No indigo/violet default accents and no purple washes unless a theme is
  explicitly purple [indigo-accent, purple-violet-wash].
- State colors (success, warning, danger) only for state. Never decorative.
- No pure `#000` or `#fff` surfaces. Text contrast meets WCAG AA in every
  theme, including muted text.
- No decorative status dots or fake visualizations [fake-dot-viz]. A status is
  a word ("Healthy", "Disabled"), optionally colored.

## Type and layout

- One sans stack for UI, one monospace stack for code, IDs, keys and JSON
  [monospace-body]. Body 14-15px, line height 1.5 or more [tiny-body-text,
  tight-line-height]. No tracking tweaks on body text.
- Numbers are tabular (`font-variant-numeric: tabular-nums`) in tables and
  counters. No oversized hero numbers [oversized-number].
- No section numbering ("01", "02") [numbered-section-markers], no icon-topped
  feature cards, no three identical cards in a row.
- Motion: none by default. If used, transition only `opacity`, `color`,
  `background-color` or `transform`, under 150 ms, never `transition: all`,
  never bounce easing, and honor `prefers-reduced-motion`
  [transition-all, bounce-easing].

## Themes

- Every color in CSS comes from a token (`--bg`, `--surface`, `--surface-2`,
  `--border`, `--text`, `--muted`, `--accent`, `--accent-text`, `--success`,
  `--warning`, `--danger`, `--code-bg`). No raw hex outside theme blocks.
- Themes are `[data-theme="…"]` blocks on `<html>` that only redefine tokens.
  Components never branch on the theme name.
- The default theme follows `prefers-color-scheme`. A user choice is stored in
  `localStorage` and applied before first paint from an external script (no
  inline script).

## Assets

- No inline `<script>`, `<style>` or `style=""` in views. CSS in
  `wwwroot/css`, JS in `wwwroot/js`.
- No CDN dependencies. The dashboard must work offline on a LAN.

## Checklist before merging dashboard changes

1. `rg -n '—|–' src/SboxNetworkStorage.Server/Views` is empty.
2. No `text-transform: uppercase`, `border-left`/`border-right` rails,
   `linear-gradient`, `backdrop-filter`, `box-shadow` larger than 1px, or raw
   hex outside theme token blocks in `wwwroot/css`.
3. Every new view checked in the light theme and one dark theme.
4. Empty, loading and error states exist for every new list or form.
