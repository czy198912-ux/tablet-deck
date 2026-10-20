// Puts the website together in _site/ for GitHub Pages (.github/workflows/pages.yml):
//
//   node scripts/build-site.js
//
// - site/ is the page itself;
// - docs/ images go to img/;
// - the deck (public/) and the built-in layouts go to try/, as the "Try it" page, with
//   window.DECK_DEMO set so it runs without a PC (see DEMO in public/app.js).
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..");
const OUT = path.join(ROOT, "_site");
const copy = (from, to, filter = () => true) =>
  fs.cpSync(path.join(ROOT, from), path.join(OUT, to), { recursive: true, filter: (src) => filter(path.basename(src)) });

fs.rmSync(OUT, { recursive: true, force: true });
copy("site", ".");
copy("docs", "img", (name) => name === "docs" || /\.(jpg|png)$/.test(name));
// The editor and showcase need the PC server, so they stay out of the demo.
copy("public", "try", (name) => !/^(editor|showcase)\./.test(name));
copy("layouts", "try/layouts");

const deck = path.join(OUT, "try", "index.html");
const html = fs.readFileSync(deck, "utf8");
const marker = '<script src="app.js"></script>';
if (!html.includes(marker)) throw new Error(`public/index.html no longer loads app.js as ${marker}; update scripts/build-site.js.`);
fs.writeFileSync(deck, html.replace(marker, `<script>window.DECK_DEMO = true;</script>\n${marker}`));
fs.writeFileSync(path.join(OUT, ".nojekyll"), ""); // serve files as they are

const count = (dir) => fs.readdirSync(dir, { withFileTypes: true }).reduce((n, e) => n + (e.isDirectory() ? count(path.join(dir, e.name)) : 1), 0);
console.log(`Built _site/: ${count(OUT)} files.`);
