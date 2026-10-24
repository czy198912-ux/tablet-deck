# Changelog

What changed in each version of Tablet Deck, newest first. Each version's
section becomes the notes of its
[GitHub release](https://github.com/nodieCode/tablet-deck/releases), so add one
before tagging a release.

## Unreleased

- The mic and **?** buttons only show when voice commands can actually run on
  the PC, and the voice list says why when they can't.
- Messages on the tablet appear above the page names instead of covering them.
- "Hold to engage" no longer runs into the quantum charge marks, and status
  lines like "Retracted" have room under their label.
- The installer now includes `THIRD-PARTY-NOTICES.txt` with the licence of the
  Node.js runtime inside `TabletDeckServer.exe`.

## 1.0.0 (2026-10-22)

The first public release.

- **A HUD-style control deck for any tablet or phone.** It runs in the browser,
  so iPad, Android and iPhone all work, with no app or account. Scan the QR
  code in the PC window to pair. Each button presses the real key on your PC,
  and by default only while Star Citizen is the window in front.
- **12 manufacturer themes**, press animations and a night mode.
- **Layouts:** the Tablet Deck HUD, plus the **Combat**, **Mining & Salvage**
  and **Hauling & Exploration** starter layouts, for tablets and phones. Each
  device picks its own under **Layout** in the sliders sheet. You can turn on
  a two-finger swipe there to flip panels.
- **Layout editor** (**Edit layout** in the window): drag and resize buttons,
  and change their label, keys, press mode, colour, icon, animation and voice
  phrases, with undo.
- **Voice commands:** hold the mic on the tablet, or set a push-to-talk key or
  mouse button on the PC. Over 300 phrases like "gear up", "guns mode", "full
  power to shields", "previous hostile" and "emote wave". The **?** next to the
  mic lists them all, and the window has a **Voice strictness** slider.
- **Follows your keybinds:** buttons read your Star Citizen keybinds, so
  rebinding in-game moves them too. The optional Tablet Deck Keyboard profile
  covers actions with no default key, and **Install keybind profile...** puts
  it in your game folders.
- **Open on PC:** buttons in the sliders sheet open UEX, Erkul, SPViewer and
  SCMDB in the PC's browser. Change the list in **Settings**.
- **A Windows app:** one installer, a window with the QR code, connected
  tablets and a log of presses, a tray icon, optional **Start with Windows**,
  and **Settings** for the pairing code, port, tap length, voice and more.
- **Updates:** Tablet Deck tells you when a new version is out and installs it
  when you click **Update now**. Turn the check off in the window if you like.
- Lets itself through Windows Firewall on private networks only. Settings live
  in `%APPDATA%\TabletDeck`, and uninstalling asks before deleting them.
