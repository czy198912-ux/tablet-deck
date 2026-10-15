# Tablet Deck

Turn any tablet into a touch control panel for Star Citizen. A small server on
your gaming PC serves a HUD-style button deck to the tablet over your home
network, and each button sends real keyboard scancodes that Star Citizen reads.

- 1024×768 two-panel HUD with landing, power management, quantum, operator
  modes, shields, targeting, salvage, doors, emotes and more
- 12 ship-manufacturer themes (RSI, Drake, Aegis, Anvil, Origin, MISC, Crusader,
  Banu, Aopoa, Argo, Consolidated Outland, Esperia)
- Press animations: landing-gear struts, quantum charge ring, decoy sparks,
  lock-on brackets, sliding doors, slide-to-eject and more
- Works in any tablet browser (iPad or Android), no app or account needed

> Fan project. Not affiliated with or endorsed by Cloud Imperium Games.
> Star Citizen and the manufacturer names belong to their owners;
> the emblems here are original designs, not the companies' logos.

## Requirements

- Windows 10 or 11 on the gaming PC
- [Node.js](https://nodejs.org) 18 or newer
- A tablet on the same Wi-Fi or network as the PC

Nothing else to install. The key sender (`native/keysender.cs`) compiles itself
on first run with the C# compiler that ships with Windows.

## Setup

1. Download this repository (**Code → Download ZIP**, then extract it) or clone it.
2. Double-click `start.bat`.
3. When Windows Firewall asks whether Node.js may use the network, allow
   **Private networks**.
4. The window prints an address and a pairing code, for example
   `http://192.168.1.20:8765` and `Pairing code: 1a2b3c4d`. Open the address on
   your tablet and enter the code. Use "Add to Home Screen" for a full-screen app.
5. Click into Star Citizen. Keys go to whichever window is in front.

`start.bat --dry-run` logs button presses without sending any keys, which is
handy for trying the deck out.

## Keybinds

The deck uses Star Citizen 4.x default keys. Some actions (operator modes,
shield facings, door controls, emotes and so on) have no default keyboard key;
bind them in-game to use those buttons.

The server also reads your own `actionmaps.xml`, so actions you have rebound
yourself use your keys. It looks in the usual install folders; if yours is
elsewhere, set the path in `config.json` (see Settings).

Buttons marked `"verify": true` in the layout use Star Citizen defaults that the
profile doesn't set (power management, system power toggles, SCM/NAV, eject,
chat, docking camera). Check those against your keybinding screen.

## Layouts

`config.json` → `layout` picks what the tablet shows:

- `layouts/deck.json` (default): the 1024×768 two-half HUD. Each half
  pages through panels with the arrows at the bottom: POWER/LANDING, Flight
  Assist, Shields, Targeting and Salvage on the left; QT/SYSTEMS, Operator
  Modes, Combat, Crew/Doors, On Foot and Emotes on the right.
- `layouts/star-citizen.json`: a simpler tabbed grid of buttons.

Canvas layouts place each element at `x`, `y`, `w`, `h` in canvas pixels (panel
elements are relative to their half). Element types include `button`, `header`,
`switch` (with `when` to show elements per mode), `menu`, `slider`, `grid` and
decorations.

## Changing buttons

Edit a layout file, then reload the page on the tablet. Button fields:

| field      | meaning |
|------------|---------|
| `keys`     | Star Citizen key names joined with `+`: `r`, `lalt+n`, `np_1`, `f5`, `ralt+y` |
| `mode`     | `tap` (quick press), `hold` (held while your finger is down), `timed` (held for `ms`) |
| `color`    | `green`, `amber`, `red`, `blue` or blank (uses the theme's accent) |
| `icon`     | icon name from `public/icons.js`; defaults to the button `id` |
| `anim`     | press animation: `flash`, `ripple`, `gear`, `charge`, `burst`, `glow`, `recoil`, `streak`, `doors`, `lock`, `hazard`, `timed` |
| `scAction` | Star Citizen action name; if a keybind file in `actionmaps` binds it to a key, that key wins |

Key names: `a`-`z`, `0`-`9`, `f1`-`f24`, `np_0`-`np_9`, `np_period`, `np_add`,
`np_subtract`, `np_multiply`, `np_divide`, `np_enter`, `lalt`, `ralt`, `lctrl`,
`rctrl`, `lshift`, `rshift`, `space`, `tab`, `enter`, `escape`, `backspace`,
`insert`, `delete`, `home`, `end`, `pgup`, `pgdn`, `up`, `down`, `left`, `right`.

## Themes

Tap the sliders button (top right) to pick a manufacturer theme. Each tablet
remembers its own choice. Themes are CSS variables in `public/themes.css`; add
one by copying a block there and adding an entry to `THEMES` in `public/app.js`.
Theme fonts come from Google Fonts; without internet access the tablet falls
back to system fonts.

## Settings (`config.json`, created on first run)

- `port`: server port (default 8765).
- `token`: the pairing code. Change it to anything you like, or delete it to
  generate a new random one. Restart the server afterwards.
- `layout`: which layout file to serve.
- `actionmaps`: keybind files to read, in order (later files win). Defaults to
  your Star Citizen `actionmaps.xml`.
- `tapMs`: how long a tap holds the key (default 60 ms).

## Safety

- Only devices with the pairing code can press keys. Keep the server on your
  home network; don't expose its port to the internet.
- Held keys are released if the tablet disconnects or goes quiet for 5 seconds.
- If Star Citizen runs as administrator, run `start.bat` as administrator too.
  Windows blocks keypresses from a normal program into an elevated one.
