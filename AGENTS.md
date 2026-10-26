# Agent guidelines

Instructions for AI coding agents (and a quick start for human contributors)
working on Tablet Deck. Read this first, then
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the details it points to.

## What the project is

Tablet Deck turns a tablet or phone into a HUD-style control deck for Star
Citizen. The deck runs in the tablet's browser; every button press travels over
the home network to a small server on the gaming PC, which presses the real keys
with Windows' `SendInput`. It's a fan project, not affiliated with Cloud
Imperium Games.

| path | what it is |
|---|---|
| `server.js` | the server: serves the deck, pairs tablets, sends keys, runs voice, the layout editor's API |
| `public/` | the deck, the layout editor (`editor.*`) and the showcase |
| `layouts/` | built-in layouts; `hud.json` is the source, most others are generated |
| `keybinds/` | the Tablet Deck Keyboard profile (generated) |
| `native/` | C# sources: the app window, the key sender, the voice listener |
| `tools/`, `scripts/` | generators, the installer build, release notes |
| `site/` | the website on GitHub Pages |

## Ground rules

These keep Tablet Deck safe to run next to an online game. Don't change them
without the owner's explicit agreement:

- **One press per tap.** A button presses its key or key combo once. No macros,
  sequences, repeats, timers that press keys, or any other automation.
- **The game is never touched.** No reading or writing game memory, no
  injection, no hooks into the game. The only game files used are the player's
  keybinds (`actionmaps.xml`, read only) and the optional profile copied into
  the game's `Controls\Mappings` folder when the player asks for it.
- **Keys only reach Star Citizen.** The focus check (`onlyWhenGameFocused`)
  stays on by default, and every new way of pressing keys goes through
  `gameFocused()` / `tapButton()` in `server.js`.
- **Releases always go through.** A key that went down must come up, even
  after focus is lost or a tablet disconnects, so no key stays stuck.
- **Pairing is required.** The WebSocket and every data route check the
  pairing code, and the layout editor's routes also only answer the PC itself
  (`fromThisPC`). New routes follow the same rules; don't add unauthenticated
  ones.
- **No tracking.** No accounts, analytics or new network calls. The only
  outgoing traffic is the update check against GitHub (which can be turned off)
  and the tablet's Google Fonts.

## Code conventions

- **Match the surrounding code:** its naming, comment density and idiom.
  Comments explain why, not what.
- **The server uses only Node's built-in modules.** Don't add runtime
  dependencies. `devDependencies` are for the installer build only. Running
  needs Node 18 or newer; the build needs 20 or newer.
- **C# must compile with the compiler that ships with Windows**
  (`csc.exe` from .NET Framework 4, i.e. C# 5): no newer language features, no
  NuGet packages.
- **Windows is the target** for everything that runs on the PC. The deck itself
  must keep working in Safari (iPad, iPhone) and Chrome (Android).
- **Translations:** new text on the deck or in the app window gets Spanish,
  French and German entries in `public/i18n.js` or `Lang` in
  `native/launcher.cs` (see [Translations](docs/DEVELOPMENT.md#translations)).
- **Design:** anything visual follows [docs/DESIGN.md](docs/DESIGN.md): theme
  tokens, button colours, the HUD style and the house colours for the editor,
  website and app window.

## Generated files

Never edit these by hand; change the source and rerun the tool:

| generated | from | rerun |
|---|---|---|
| `layouts/hud-phone.json` | `layouts/hud.json`, `tools/make-phone-layout.js` | `node tools/make-phone-layout.js` |
| starter and ship layouts (`combat*.json`, `mining-salvage*.json`, `hauling-exploration*.json`, `prospector*.json`, `vulture*.json`) | `hud.json`, `hud-phone.json`, `tools/make-starter-layouts.js` | `node tools/make-starter-layouts.js` (after the phone layout) |
| `keybinds/TabletDeck-StarCitizen.xml` | every button's `scAction` and keys | `node tools/make-profile.js` |

A new `scAction` must be added to `ACTIONMAPS` in `tools/make-profile.js`, or
the profile build stops.

## Checking your work

There's no automated test suite, so check by hand before you call a change
done:

1. `node --check` every JavaScript file you changed.
2. `node tools/make-profile.js --check` (the installer build runs it too).
3. Rerun the generators if you touched a layout, and make sure their output is
   committed.
4. Run a dry-run server, which presses no keys:
   `node server.js --dry-run --port 8790`. Open the deck, or the showcase at
   `http://localhost:8790/showcase.html?t=<pairing code>`, which shows each
   press, its keys and whether the focus check would have blocked it.
5. For the website or the "Try it" page: `node scripts/build-site.js` and serve
   `_site/`. The deck's demo mode is `public/index.html?demo`.

Say what you couldn't check. Windows-only parts (the key sender, voice, the app
window, the installer) can't be run on other systems; never report them as
tested if they weren't.

## Docs and releases

- **Every user-facing change** gets a line in the top section of
  `CHANGELOG.md`, and the README (and `site/index.html` if it lists the
  feature) is updated in the same change.
- **The version** lives in `package.json`. A release is a commit with the new
  version and its `CHANGELOG.md` section, then a `v<version>` tag; the Release
  workflow builds and publishes it. Don't tag or release without the owner.
- **Pushing to `main` publishes the website** when it touches `site/`,
  `public/`, `layouts/` or the images in `docs/`.

## Git

- Short, imperative commit subjects ("Add a Mining panel"), with a body that
  explains what changed and why when it isn't obvious.
- Never force-push, rewrite history or delete branches or tags on GitHub
  without the owner's explicit OK.
- Keep unrelated changes in separate commits.

## Privacy

Nothing from a real machine goes into commits, docs, screenshots or the build:
no real IP addresses, pairing codes, user names or local paths. Use the
README's examples instead: `http://192.168.1.20:8765` and pairing code
`1a2b3c4d`. `config.json` (pairing code, ports, game paths) is never committed.
