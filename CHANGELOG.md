# Changelog

What changed in each version of Tablet Deck, newest first. Each version's
section becomes the notes of its
[GitHub release](https://github.com/nodieCode/tablet-deck/releases), so add one
before tagging a release.

## 1.4.0 (not released yet)

- **Push-to-talk from a PC key:** in **Settings**, pick any key or mouse button
  (Mouse 4/5 work well) to talk to Tablet Deck without touching the tablet.
  Hold it to talk, or press once to start and again to stop. A soft sound plays
  when it starts and stops listening, and the tablet's mic button lights up.
  It only watches that one key; it doesn't record anything you type.

## 1.3.0 (2026-10-20)

- **Settings** button in the window: change the pairing code (or make a new
  random one), the port, the "only while Star Citizen is in front" check, how
  long a tap holds the key, and voice commands and their language, without
  editing `config.json`. Settings also work when Tablet Deck couldn't start,
  for example to pick another port when yours is taken.
- **Install keybind profile...** (in Settings): finds your Star Citizen copies
  (LIVE, PTU and so on), shows which already have the current Tablet Deck
  Keyboard profile, and installs it into the ones you tick. You can also pick
  the game folder yourself.
- **Layout editor** (Edit layout in the window): move and resize buttons by
  dragging, and change their label, keys, press mode, colour, icon, animation
  and voice phrases, with undo. Saving makes your own copy of the layout, and
  tablets switch to it and reload by themselves.
- Pick a layout on each tablet or phone: tap the sliders button (top right)
  and choose under **Layout**. Each device remembers its own choice.
- Three starter layouts with the panels picked for one kind of flying:
  **Combat**, **Mining & Salvage** and **Hauling & Exploration**, for tablets
  and phones.
- Voice commands list on the tablet: the new **?** next to **Hold to talk**
  shows everything you can say and the button it presses, with a search box.

## 1.2.1 (2026-10-20)

- Behind the scenes: the release build uses current versions of GitHub's build
  tools. Nothing changes in the app; this release tests updating with
  **Update now**.

## 1.2.0 (2026-10-20)

- Update checker: Tablet Deck tells you when a new version is out and installs
  it when you click **Update now**. It checks when it starts and once a day;
  untick **Check for updates automatically** to turn that off, or use **Check
  for updates now** in the tray menu. Updating from 1.1.0 or earlier still
  means downloading the installer once.
- After an update, the window offers this list of what's new.
- **Voice commands...** lists every phrase you can say and the button it
  presses, with a search box.
- **Voice strictness** slider: set how sure speech recognition must be before a
  command counts. The window shows the last thing it heard and how sure it was.

## 1.1.0 (2026-10-20)

- Tablet Deck now opens as an app window instead of a console. It shows the QR
  code, address and pairing code, how many tablets are connected, whether voice
  commands are ready, and a log of button presses.
- Minimize the window to keep Tablet Deck running in the tray. Closing the
  window quits, and any held keys are released first.
- Optional **Start with Windows**, which starts Tablet Deck in the tray.
- Clearer errors, for example when the port is already in use, with a
  **Restart** button.

## 1.0.0 (2026-10-20)

- First release with a Windows installer: no Node.js or start.bat needed.
- Installs to Program Files with a Start menu shortcut and an optional desktop
  shortcut, and lets Tablet Deck through Windows Firewall on private networks
  only.
- Optional: installs the Tablet Deck Keyboard keybind profile into your Star
  Citizen folder.
- Settings live in `%APPDATA%\TabletDeck`. Uninstalling asks before deleting
  them.
- Includes the HUD deck for tablets and phones, 12 manufacturer themes, press
  animations, night mode and hold-to-talk voice commands.
