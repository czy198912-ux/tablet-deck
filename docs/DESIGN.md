# Design

How Tablet Deck looks, and the rules that keep the deck, the layout editor, the
website and the Windows app looking like one product. For running and building
the code, see [DEVELOPMENT.md](DEVELOPMENT.md).

- [Theme tokens](#theme-tokens)
- [Adding a theme](#adding-a-theme)
- [Button colours](#button-colours)
- [HUD style](#hud-style)
- [The other surfaces](#the-other-surfaces)
- [Motion, night mode and layering](#motion-night-mode-and-layering)
- [Accessibility](#accessibility)

## Theme tokens

Every theme is a block of CSS custom properties in `public/themes.css`, keyed by
`[data-theme="<id>"]`. The deck's styles only read these variables, so a new
theme never needs changes anywhere else in the CSS. A theme sets **all** of the
tokens below, except the two marked optional.

| token | controls | example (RSI) |
|---|---|---|
| `--bg` | page background colour | `#05090f` |
| `--bg-art` | backdrop drawn over `--bg`: glows, stripes, `var(--stars)` | radial glow + stars |
| `--panel` | button and panel fill | `#0b1420` |
| `--panel-hi` | raised or pressed fill | `#122236` |
| `--line` | borders and dividers | `#1c2c40` |
| `--line-hi` | highlighted borders | `#35598a` |
| `--text` | main text | `#e8f2fc` |
| `--dim` | secondary text: states ("OFF", "RETRACTED"), captions | `#7f95ad` |
| `--accent` | the theme's signature colour; buttons without a `color` | `#4fb3ff` |
| `--green`, `--amber`, `--red`, `--blue` | the four button colours (see [Button colours](#button-colours)) | `#46d79a` … |
| `--font-display` | headings, big labels | `"Exo 2", system-ui, sans-serif` |
| `--font-body` | button labels and everything else | same |
| `--label-case` | `text-transform` of labels | `uppercase` |
| `--label-track` | letter spacing of labels | `.06em` |
| `--label-weight` | font weight of labels | `600` |
| `--key-radius` | button corner radius | `6px` |
| `--key-clip` | `clip-path` for cut corners, or `none` | `none` |
| `--key-edge` | width of the coloured bar on a button's left edge (`0` for none) | `3px` |
| `--key-bg` | grid-layout button fill (usually a gradient) | `linear-gradient(...)` |
| `--key-top`, `--key-top-h` | highlight strip along a grid button's top, and its height | gradient, `2px` |
| `--key-shadow` | button shadow; optional | `0 8px 22px …` |
| `--icon-stroke` | stroke width of the line icons | `2.2` |
| `--icon-glow` | filter on icons: a `drop-shadow` glow, or `none` | `drop-shadow(...)` |
| `--tab-radius` | tab shape in the tabbed grid layout | `8px 8px 0 0` |
| `--label-shadow`, `--state-shadow` | shadow behind labels and state text; optional | dark blur by default |

`:root` also defines `--stars` and `--grid`, shared backdrop pieces that themes
can use inside `--bg-art`.

**Light themes** (Origin is the only one so far) also set `color-scheme: light`,
and must set `--label-shadow` and `--state-shadow`. The default dark blur makes
text look smudged on light buttons; Origin uses
`0 1px 0 rgba(255,255,255,.9)` and `none`.

## Adding a theme

1. **Copy a block** in `public/themes.css`, give it a new id, and set every
   token in the table. Start from a theme with a similar feel: a dark one for a
   dark theme, Origin for a light one.
2. **Fonts:** if the theme uses a new Google font, add it to the Google Fonts
   link in both `public/index.html` and `public/editor.html`, and end the font
   stack with a system fallback (`system-ui, sans-serif`). The deck also has to
   look right with the fallback, since it works offline too.
3. **Register it** in `THEMES` at the top of `public/app.js`: `id`, `name`
   (short, for the picker), `full` (the manufacturer's full name), `tag` (a
   short tagline) and `emblem`.
4. **The emblem** is an original badge design on a 64×64 SVG grid that suggests
   the maker's character. Never copy or trace a real company logo. Strokes use
   `currentColor`; `${F}` marks solid accent fills and `T(...)` wraps parts drawn
   in the theme's text colour. Keep a one-line comment describing the idea, as
   the existing emblems do.
5. **Check it:**
   - the theme picker card;
   - the deck on a tablet and a phone (the showcase in `docs/DEVELOPMENT.md` has
     both frames);
   - pressed buttons and every button colour;
   - night mode, and the intro screen (it draws the emblem);
   - the text contrast rules under [Accessibility](#accessibility).
6. **Update the counts:** "12 manufacturer themes" appears in the README,
   `CHANGELOG.md`, the website (`site/index.html`) and the theme gallery image
   `docs/themes.jpg`.

## Button colours

Layouts give a button one of four colours, or none for the theme's accent. Use
them by meaning, so a colour tells you what a button does in every theme:

| colour | meaning | examples |
|---|---|---|
| `green` | power, systems on, flight | Flight mode, Master: SCM, shield power |
| `amber` | work modes and caution | Mining, Salvage, Fracture/Extract, G-Safety |
| `red` | weapons and danger | Guns, Missiles, Fire Laser, Exit Seat, Self-destruct |
| `blue` | navigation, scanning and info | Quantum, Scanning, mining modules |
| none | everything else | most buttons |

Use colours sparingly. A panel where every button is coloured has no colours.

## HUD style

The deck should feel like a ship's display, not a web page:

- **Labels** are short and in Title Case in the layout JSON ("Landing Gear",
  "Engage Quantum"); themes decide upper or lower case through `--label-case`.
  Keep them to two or three words that fit a button on a phone.
- **State text** under a label is one short word or two in capitals: "OFF",
  "RETRACTED", "HOLD TO ENGAGE".
- **Section headers** start with `//` and use underscores between words:
  `// POWER_MANAGEMENT`, `// SHIP_SYSTEMS`.
- **Lines are thin** (1px), corners are bracketed (the intro screen, the
  banner, the website), and glow comes from a `drop-shadow` of the element's own
  colour, not from a separate colour.
- **Icons** are line icons on a 48×48 grid in `public/icons.js`, drawn with
  `currentColor`, using the theme's `--icon-stroke`. Add new ones in the same
  style, not as images.

## The other surfaces

The layout editor, the showcase, the website and the Windows app window don't
switch themes. They use the RSI look as the house style:

| role | colour | used for |
|---|---|---|
| background | `#05090f` | page and window background |
| panel | `#0b1726` | cards, panels |
| panel, raised | `#12253b` | hover, selected |
| line | `#1f3a58` | borders |
| accent | `#4fb3ff` | links, buttons, focus, highlights |
| text | `#e8eef5` | main text |
| dim | `#8aa0b8` | secondary text |

Fonts: **Oxanium** for display text, **Exo 2** for everything else, with
`system-ui, sans-serif` as the fallback.

The website keeps these as tokens in `site/style.css` (`--bg`, `--panel`,
`--accent`, `--display`, `--body` …); reuse them there instead of new hex
values. The Windows app (`native/launcher.cs`) sets the same colours in code,
with its own status colours for good, warning and error states. When you change
the house colours, change them in both.

## Motion, night mode and layering

- **Keep motion short.** UI transitions (paging, sheets, toasts) take about
  200 ms. Longer effects are only for moments the player triggers: press
  animations, the quantum charge, the intro.
- **Every animation respects "reduce motion".** `fx.css`, `hud.css` and
  `intro.css` each turn their animations off under
  `@media (prefers-reduced-motion: reduce)`. A new animation needs the same.
- **Night mode** is a black overlay (`#night`) over the deck. Anything that must
  stay readable at night goes above it.
- **Layering** (`z-index`), from bottom to top: deck content and its menus (up
  to 5), the night overlay (9), the theme picker (10), toasts (20), the intro
  screen (50). Put a new overlay in this order rather than picking a large
  number.

## Accessibility

- **Contrast:** `--text` on `--panel` should reach at least 4.5:1, and `--dim`
  on `--panel` at least 3:1, in every theme, including the light one. All 12
  themes do today (text 13.7:1 or more, dim 3.9:1 or more, Origin being the
  lowest). Check a new theme with any contrast checker.
- **Colour is never the only signal:** a button's label says what it does,
  and its state text says whether it's on. Colour adds to that.
- **Touch targets:** the smallest buttons are the phone's top bar, 44×36
  points. Don't make anything tappable smaller than that, and give new buttons
  at least 44 points in both directions where there's room.
- **Messages** (toasts, the theme picker, "Star Citizen not focused") must stay
  readable with night mode on.
- Buttons without a text label, like the icon-only top bar, need an
  `aria-label`.
