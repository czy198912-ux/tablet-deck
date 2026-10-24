# Setting up code signing with SignPath

Steps for signing Tablet Deck's releases with free code signing from
[SignPath Foundation](https://signpath.org). The policy the project follows is
[CODE_SIGNING.md](../CODE_SIGNING.md); the workflow side is already in place
(see [Code signing](DEVELOPMENT.md#code-signing)). SignPath's forms and menus
change from time to time, so follow their wording where it differs from this.

- [1. Before applying](#1-before-applying)
- [2. Applying](#2-applying)
- [3. After approval](#3-after-approval)
- [4. Releasing with signing](#4-releasing-with-signing)

## 1. Before applying

- Turn on **two-factor authentication** on the GitHub account of everyone in
  the Team section of CODE_SIGNING.md. SignPath Foundation requires it.
- Check that CODE_SIGNING.md still lists the right people and describes what
  the app sends over the network.

## 2. Applying

Apply on [signpath.org](https://signpath.org). Answers for the form:

| question | answer |
|---|---|
| Project name | Tablet Deck |
| Repository | https://github.com/nodieCode/tablet-deck |
| Home page / download page | https://nodiecode.github.io/tablet-deck/ and https://github.com/nodieCode/tablet-deck/releases |
| Licence | MIT, for all of the project's own code |
| Code signing policy | https://github.com/nodieCode/tablet-deck/blob/main/CODE_SIGNING.md |
| Build system | GitHub Actions on GitHub-hosted Windows runners (`.github/workflows/release.yml`) |
| What gets signed | Windows executables: the installer `TabletDeck-Setup-<version>.exe`, and inside it `TabletDeck.exe`, `TabletDeckServer.exe`, `native/keysender.exe` and `native/voice.exe` |

**Description** (adjust to taste):

> Tablet Deck turns a tablet or phone into a touch control panel for the game
> Star Citizen, like a Stream Deck. A small app on the gaming PC serves the
> deck to devices on the home network. When a button is tapped, the PC presses
> that button's key or key combination once, using Windows' SendInput. It's
> MIT-licensed and built entirely by GitHub Actions from the public repository.

**Worth stating up front**, since SignPath checks for unwanted software and
the app presses keys:

> One tap presses one key or key combination once: there are no macros,
> sequences, repeats or timed presses. By default keys only reach the game
> while it's the window in front, and only devices with the pairing code can
> press anything. Tablet Deck never reads or changes the game's memory or
> files, apart from reading the player's keybinds and copying a keybind
> profile into the game's folder when the player asks.

**Third-party code**, so SignPath can judge it:

> `TabletDeckServer.exe` contains the Node.js runtime (MIT, with the licences
> of its bundled libraries listed in `THIRD-PARTY-NOTICES.txt` in the
> installer). The installer is built with Inno Setup, which has its own
> permissive licence (https://jrsoftware.org/files/is/license.txt). There is no
> proprietary code.

## 3. After approval

SignPath Foundation sets up an organization for the project and invites you.
Then, in SignPath:

1. **Project:** create (or check) the project with the slug `tablet-deck`,
   linked to the GitHub repository.
2. **Artifact configurations:** add two, with the slugs `app` and `installer`,
   and paste in `.signpath/artifact-configurations/app.xml` and
   `installer.xml` from this repository.
3. **Trusted build system:** link the predefined **GitHub.com** build system
   to the project, so SignPath only signs what GitHub Actions built from this
   repository.
4. **Signing policy:** create `release-signing` with the release certificate
   SignPath Foundation provides. Make yourself the **approver**, so every
   request waits for your approval, and restrict it to the `main` branch if
   the form offers that.
5. **CI user:** create a CI user, allow it to submit signing requests for
   `release-signing`, and create an **API token** for it.

Then on GitHub, in the repository's **Settings** → **Secrets and variables** →
**Actions**:

6. Add the secret **`SIGNPATH_API_TOKEN`** with the CI user's token.
7. Add the variable **`SIGNPATH_ORGANIZATION_ID`** with the organization ID
   from SignPath's organization settings.
8. Only if you chose other names than above: the variables
   `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_POLICY_SLUG`.

## 4. Releasing with signing

Release as usual (a version tag, or **Actions** → **Release** → **Run
workflow** with **Publish** ticked). The run now stops twice to wait for you:

1. **The app files:** SignPath shows a signing request for `app`. Check that
   it comes from the Release workflow on `main`, and approve it.
2. **The installer:** a second request, for `installer`. Approve it too.

Each request waits up to an hour; if it runs out, nothing is published and you
can run the workflow again. Test builds (the workflow without **Publish**) are
never signed and never ask.

Once signing works, update the README's and the website's "Windows protected
your PC" answers, and the status note in CODE_SIGNING.md.
