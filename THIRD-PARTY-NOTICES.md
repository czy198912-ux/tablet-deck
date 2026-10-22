# Third-party software

Tablet Deck itself is [MIT licensed](LICENSE). This page lists the third-party
software it ships or uses, and where their licences are.

## Shipped with Tablet Deck

| software | where | licence |
|---|---|---|
| [Node.js](https://nodejs.org) | inside `TabletDeckServer.exe`, which packs the Node.js runtime with the server | [Node.js licence](https://github.com/nodejs/node/blob/main/LICENSE): MIT, plus the licences of the libraries Node.js bundles (OpenSSL, V8, ICU, libuv, zlib and others) |

The installer puts the full licence text of the exact Node.js version it
contains in `THIRD-PARTY-NOTICES.txt`, next to `LICENSE.txt` in Tablet Deck's
install folder. The build writes that file from the Node.js it packs (see
[Building the installer](docs/DEVELOPMENT.md#building-the-installer)).

The key sender, voice listener and app window (`native/*.cs`) contain no
third-party code. They use parts of Windows (`SendInput`, the .NET Framework
and its speech recognition), which come with Windows and aren't shipped.

## Used, but not shipped

- **Theme fonts:** Black Ops One, Chakra Petch, Cinzel, Comfortaa, Exo 2,
  Marcellus, Montserrat, Oxanium, Rajdhani, Saira Condensed and Saira Stencil One,
  under the [SIL Open Font License](https://openfontlicense.org). The tablet
  loads them from [Google Fonts](https://fonts.google.com); Tablet Deck doesn't
  include the font files.
- **Build tools:** [postject](https://github.com/nodejs/postject) and
  [rcedit](https://github.com/electron/rcedit) (the `devDependencies` in
  `package.json`) and [Inno Setup](https://jrsoftware.org/isinfo.php) are used
  to build the installer, but aren't part of it.
