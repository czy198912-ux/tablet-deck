// Prints the CHANGELOG.md section for a version, for its GitHub release notes.
//
//   node scripts/release-notes.js 1.1.0
//
// Fails when CHANGELOG.md has no "## 1.1.0" section, so a release can't go out
// without notes. .github/workflows/release.yml runs it.
"use strict";
const fs = require("fs");
const path = require("path");

const version = (process.argv[2] || "").replace(/^v/, "");
if (!version) {
  console.error("Usage: node scripts/release-notes.js <version>");
  process.exit(1);
}
const lines = fs.readFileSync(path.join(__dirname, "..", "CHANGELOG.md"), "utf8").split(/\r?\n/);
const start = lines.findIndex((l) => l.startsWith(`## ${version} `) || l === `## ${version}`);
if (start === -1) {
  console.error(`CHANGELOG.md has no "## ${version}" section. Add one before releasing.`);
  process.exit(1);
}
let end = lines.findIndex((l, i) => i > start && l.startsWith("## "));
if (end === -1) end = lines.length;
const notes = lines.slice(start + 1, end).join("\n").trim();
if (!notes) {
  console.error(`The "## ${version}" section of CHANGELOG.md is empty.`);
  process.exit(1);
}
console.log(notes);
