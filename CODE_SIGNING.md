# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io),
certificate by [SignPath Foundation](https://signpath.org).

> Status: applied for. Until SignPath Foundation approves the project, releases
> are unsigned and Windows shows an "unknown publisher" warning (see the
> README's Download section).

## What is signed

Each release's Windows files, all built from this repository:

- `TabletDeck-Setup-<version>.exe`, the installer
- `TabletDeck.exe`, the app window
- `TabletDeckServer.exe`, the server (Node.js packed with `server.js` and the
  files in `public/`, `layouts/` and `keybinds/`)
- `native/keysender.exe`, the key sender (`native/keysender.cs`)
- `native/voice.exe`, the voice listener (`native/voice.cs`)

They're built only by the **Release** workflow (`.github/workflows/release.yml`)
on GitHub-hosted runners, from a version tag in this repository. Nothing built
on a personal computer is signed. Every signing request is approved by hand in
SignPath before it's signed.

## Team

- Committers and reviewers: [nodieCode](https://github.com/nodieCode)
- Approvers: [nodieCode](https://github.com/nodieCode)

## Privacy

Tablet Deck doesn't collect or send any personal data, and it has no accounts,
ads or tracking. Its network use is:

- **Your home network:** it serves the deck to tablets and phones on your
  network. Only devices with the pairing code can press keys.
- **Update checks:** the app window asks GitHub
  (`api.github.com/repos/nodieCode/tablet-deck/releases/latest`) whether
  there's a newer version, when it starts and once a day. Untick **Check for
  updates automatically** to turn this off. Updates download only when you
  click **Update now**.
- **Fonts:** the tablet's browser loads the theme fonts from Google Fonts.

Fan project. Not affiliated with or endorsed by Cloud Imperium Games.
