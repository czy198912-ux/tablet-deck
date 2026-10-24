// Tablet Deck: serve a touch button page to a tablet on the LAN and turn button
// presses into keyboard scancodes on this PC. No dependencies beyond Node itself.
//
//   node server.js            normal mode
//   node server.js --dry-run  log key events instead of sending them, and stream them
//                             to the sandbox page (public/showcase.html)
//
// The installer ships this as TabletDeck.exe, a Node single executable (scripts/build.js).
"use strict";
const http = require("http");
const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");
const { spawn, spawnSync, execFileSync } = require("child_process");
const qr = require("./qr");

// node:sea is missing before Node 20; that's never the installed app.
const sea = (() => { try { return require("node:sea"); } catch { return null; } })();
const INSTALLED = Boolean(sea && sea.isSea());
// Running from source, everything lives in the repo folder. The installed app keeps
// public/, layouts/ and keybinds/ inside TabletDeckServer.exe, the native helpers next to
// it in Program Files (read-only), and config.json plus the player's own files in %APPDATA%.
const APP_DIR = INSTALLED ? path.dirname(process.execPath) : __dirname;
const DATA_DIR = INSTALLED ? path.join(process.env.APPDATA || os.homedir(), "TabletDeck") : __dirname;
const DRY_RUN = process.argv.includes("--dry-run");
// --ui: run under the app window (native/launcher.cs), which shows the console output as
// its log and reads "@ui {json}" status lines. It sends "quit" on stdin to stop the server.
const UI = process.argv.includes("--ui");
// --port <n> overrides config.json, e.g. to run a dry-run copy next to the real one.
const portArg = process.argv.indexOf("--port");
const PORT_OVERRIDE = portArg > -1 ? Number(process.argv[portArg + 1]) : null;
const CONFIG_PATH = path.join(DATA_DIR, "config.json");

// The installed app has its own console window, which closes as soon as the process
// ends. Like start.bat's "pause", keep it open after a fatal error so it can be read.
function fatal(message) {
  console.error(message);
  ui({ t: "fatal", text: message });
  if (INSTALLED && process.stdin.isTTY) spawnSync("cmd.exe", ["/c", "pause"], { stdio: "inherit" });
  process.exit(1);
}

// A status line for the app window; nothing without --ui.
function ui(event) {
  if (UI) console.log(`@ui ${JSON.stringify(event)}`);
}
if (INSTALLED) {
  process.title = "Tablet Deck";
  process.on("uncaughtException", (err) => fatal(err.stack || String(err)));
}

// ---------- files ----------

// A bundled file (e.g. "public/index.html") as a Buffer, or null if there's no such file.
function readBundled(rel) {
  if (INSTALLED) {
    try { return Buffer.from(sea.getAsset(rel)); } catch { return null; }
  }
  const file = path.join(__dirname, rel);
  return fs.existsSync(file) && fs.statSync(file).isFile() ? fs.readFileSync(file) : null;
}

// A file named in config.json (layouts, actionmaps) as a Buffer, or null if it's missing.
// Absolute paths are used as they are. Relative ones come from the data folder first, so
// a player's own layouts/my.json works, then from the bundled files.
function readConfigFile(file) {
  const own = path.resolve(DATA_DIR, file);
  if (fs.existsSync(own) && fs.statSync(own).isFile()) return fs.readFileSync(own);
  return path.isAbsolute(file) ? null : readBundled(file.replace(/\\/g, "/"));
}

const VERSION = (() => { try { return JSON.parse(readBundled("package.json")).version; } catch { return ""; } })();

// ---------- config ----------

// Finds the player's actionmaps.xml in the usual Star Citizen install folders.
function findActionmaps() {
  const sub = "StarCitizen/LIVE/user/client/0/Profiles/default/actionmaps.xml";
  const roots = [];
  for (const drive of "CDEFGH") {
    roots.push(`${drive}:/Program Files/Roberts Space Industries`, `${drive}:/Roberts Space Industries`, `${drive}:/RSI`, `${drive}:/Games/Roberts Space Industries`);
  }
  return roots.map((r) => `${r}/${sub}`).find((p) => fs.existsSync(p)) || null;
}

function loadConfig() {
  let config = {};
  if (fs.existsSync(CONFIG_PATH)) config = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8"));
  let changed = false;
  if (!config.port) { config.port = 8765; changed = true; }
  if (!config.token) { config.token = crypto.randomBytes(4).toString("hex"); changed = true; }
  if (!config.layout) { config.layout = "layouts/hud.json"; changed = true; }
  if (!config.phoneLayout) { config.phoneLayout = "layouts/hud-phone.json"; changed = true; }
  if (config.actionmaps === undefined) {
    // Later files win; missing files are skipped. The player's actionmaps.xml goes last:
    // loading a profile in-game copies it there, and later rebinds land there too.
    config.actionmaps = ["keybinds/TabletDeck-StarCitizen.xml", findActionmaps()].filter(Boolean);
    changed = true;
  }
  if (!config.tapMs) { config.tapMs = 60; changed = true; }
  if (config.onlyWhenGameFocused === undefined) { config.onlyWhenGameFocused = true; changed = true; }
  if (config.voice === undefined) { config.voice = true; changed = true; }
  if (!config.voiceLanguage) { config.voiceLanguage = "en-US"; changed = true; }
  if (!config.voiceConfidence) { config.voiceConfidence = 0.6; changed = true; }
  // The installed app window checks GitHub for new versions unless this is false.
  if (INSTALLED && config.autoUpdate === undefined) { config.autoUpdate = true; changed = true; }
  // Websites a tablet may open on this PC ("Open on PC" in its sliders sheet).
  if (config.links === undefined) {
    config.links = [
      { name: "UEX", url: "https://uexcorp.space/" },
      { name: "Erkul", url: "https://www.erkul.games/" },
      { name: "SPViewer", url: "https://www.spviewer.eu/" },
      { name: "SCMDB", url: "https://scmdb.net/" },
    ];
    changed = true;
  }
  if (changed) {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    fs.writeFileSync(CONFIG_PATH, JSON.stringify(config, null, 2) + "\n");
  }
  // Allow a numeric code such as "token": 1234 without quotes; the page sends the code as lowercase text.
  config.token = String(config.token).trim().toLowerCase();
  return config;
}
const config = loadConfig();
if (PORT_OVERRIDE) config.port = PORT_OVERRIDE;

// Settings the app window may change, each turning its text into a value, or undefined
// when it isn't valid. See applySetting() and --set below.
const onOff = (v) => (v === "on" ? true : v === "off" ? false : undefined);
const wholeNumber = (min, max) => (v) => (/^\d+$/.test(v) && +v >= min && +v <= max ? +v : undefined);
const SETTINGS = {
  voiceConfidence: (v) => (/^[\d.]+$/.test(v) && +v >= 0 && +v <= 1 ? Math.round(+v * 100) / 100 : undefined),
  autoUpdate: onOff,
  onlyWhenGameFocused: onOff,
  voice: onOff,
  shortEmotes: onOff,
  token: (v) => (/^[a-z0-9]{1,32}$/i.test(v) ? v.toLowerCase() : undefined),
  port: wholeNumber(1024, 65535),
  tapMs: wholeNumber(20, 1000),
  voiceLanguage: (v) => (/^[a-z]{2}(-[A-Za-z]{2,4})?$/.test(v) ? v : undefined),
  // Push-to-talk from a PC key: its Windows virtual-key code (0 = none) and a name to show.
  pttKey: wholeNumber(0, 255),
  pttKeyName: (v) => (/^[\w +-]{1,30}$/.test(v) ? v : undefined),
  pttMode: (v) => (v === "hold" || v === "toggle" ? v : undefined),
  pttSound: onOff,
  // The address tablets should use (the QR code), when the automatic choice is wrong;
  // empty for automatic.
  host: (v) => (v === "" || /^(\d{1,3}\.){3}\d{1,3}$/.test(v) || /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$/i.test(v) ? v : undefined),
  // JSON list of { name, url }: at most 12, https only.
  links: (v) => {
    let list;
    try { list = JSON.parse(v); } catch { return undefined; }
    if (!Array.isArray(list) || list.length > 12) return undefined;
    const ok = list.every((l) => l && typeof l.name === "string" && /^[^<>"]{1,24}$/.test(l.name.trim())
      && typeof l.url === "string" && /^https:\/\/[^\s"<>]{3,300}$/.test(l.url));
    return ok ? list.map((l) => ({ name: l.name.trim(), url: l.url })) : undefined;
  },
};

// --set name=value ...: save settings to config.json and exit without starting. The app
// window uses this while the server is stopped, e.g. to change a port that's taken.
const setArg = process.argv.indexOf("--set");
if (setArg > -1) {
  let ok = true;
  for (const pair of process.argv.slice(setArg + 1)) {
    const [name, ...rest] = pair.split("=");
    const value = SETTINGS[name] ? SETTINGS[name](rest.join("=")) : undefined;
    if (value === undefined) { console.error(`"${pair}" isn't a valid setting.`); ok = false; continue; }
    saveSetting(name, value);
  }
  process.exit(ok ? 0 : 1);
}

// ---------- scancodes (set 1). [code, extended] ----------

const SCANCODES = {
  escape: [0x01, 0], "1": [0x02, 0], "2": [0x03, 0], "3": [0x04, 0], "4": [0x05, 0], "5": [0x06, 0],
  "6": [0x07, 0], "7": [0x08, 0], "8": [0x09, 0], "9": [0x0a, 0], "0": [0x0b, 0], minus: [0x0c, 0],
  equals: [0x0d, 0], backspace: [0x0e, 0], tab: [0x0f, 0], q: [0x10, 0], w: [0x11, 0], e: [0x12, 0],
  r: [0x13, 0], t: [0x14, 0], y: [0x15, 0], u: [0x16, 0], i: [0x17, 0], o: [0x18, 0], p: [0x19, 0],
  lbracket: [0x1a, 0], rbracket: [0x1b, 0], enter: [0x1c, 0], lctrl: [0x1d, 0], a: [0x1e, 0],
  s: [0x1f, 0], d: [0x20, 0], f: [0x21, 0], g: [0x22, 0], h: [0x23, 0], j: [0x24, 0], k: [0x25, 0],
  l: [0x26, 0], semicolon: [0x27, 0], apostrophe: [0x28, 0], grave: [0x29, 0], lshift: [0x2a, 0],
  backslash: [0x2b, 0], z: [0x2c, 0], x: [0x2d, 0], c: [0x2e, 0], v: [0x2f, 0], b: [0x30, 0],
  n: [0x31, 0], m: [0x32, 0], comma: [0x33, 0], period: [0x34, 0], slash: [0x35, 0],
  rshift: [0x36, 0], np_multiply: [0x37, 0], lalt: [0x38, 0], space: [0x39, 0], capslock: [0x3a, 0],
  f1: [0x3b, 0], f2: [0x3c, 0], f3: [0x3d, 0], f4: [0x3e, 0], f5: [0x3f, 0], f6: [0x40, 0],
  f7: [0x41, 0], f8: [0x42, 0], f9: [0x43, 0], f10: [0x44, 0], numlock: [0x45, 0],
  scrolllock: [0x46, 0], np_7: [0x47, 0], np_8: [0x48, 0], np_9: [0x49, 0], np_subtract: [0x4a, 0],
  np_4: [0x4b, 0], np_5: [0x4c, 0], np_6: [0x4d, 0], np_add: [0x4e, 0], np_1: [0x4f, 0],
  np_2: [0x50, 0], np_3: [0x51, 0], np_0: [0x52, 0], np_period: [0x53, 0], f11: [0x57, 0],
  f12: [0x58, 0], f13: [0x64, 0], f14: [0x65, 0], f15: [0x66, 0], f16: [0x67, 0], f17: [0x68, 0],
  f18: [0x69, 0], f19: [0x6a, 0], f20: [0x6b, 0], f21: [0x6c, 0], f22: [0x6d, 0], f23: [0x6e, 0],
  f24: [0x76, 0],
  np_enter: [0x1c, 1], rctrl: [0x1d, 1], np_divide: [0x35, 1], print: [0x37, 1], ralt: [0x38, 1],
  home: [0x47, 1], up: [0x48, 1], pgup: [0x49, 1], left: [0x4b, 1], right: [0x4d, 1], end: [0x4f, 1],
  down: [0x50, 1], pgdn: [0x51, 1], insert: [0x52, 1], delete: [0x53, 1],
};
// Some Star Citizen profiles write numpad keys as num1 instead of np_1.
for (let d = 0; d <= 9; d++) SCANCODES[`num${d}`] = SCANCODES[`np_${d}`];

// "lalt+n" -> [[0x38,0],[0x31,0]]; modifiers first, main key last.
function parseCombo(combo) {
  const keys = String(combo).toLowerCase().split("+").map((k) => k.trim()).filter(Boolean);
  return keys.map((k) => {
    const sc = SCANCODES[k];
    if (!sc) throw new Error(`Unknown key "${k}" in "${combo}"`);
    return sc;
  });
}

// ---------- Star Citizen actionmaps overrides ----------

// Returns { actionName: "lalt+n" } for keyboard rebinds in Star Citizen actionmaps or
// Mappings profile files. `files` is one path or a list; later files win, matching a
// profile loaded in-game on top of earlier customisations.
function readActionmapOverrides(files) {
  const overrides = {};
  for (const file of [].concat(files || [])) {
    const xml = readConfigFile(file)?.toString("utf8");
    if (!xml) continue;
    const actionRe = /<action name="([^"]+)"[^>]*>([\s\S]*?)<\/action>/g;
    let m;
    while ((m = actionRe.exec(xml))) {
      const rebind = /<rebind input="kb1_([^"]*)"/.exec(m[2]);
      if (rebind && rebind[1].trim()) overrides[m[1]] = rebind[1].trim();
    }
  }
  return overrides;
}

// ---------- layout ----------

// Loads the layout, applies actionmaps overrides, and validates every key combo.
// A button is any object in the layout with an "id" and "keys", wherever it sits
// (grid pages, canvas panels, menus), so both layout styles share one code path.
// The page asks for device=phone on small screens; phones get config.phoneLayout
// when that file exists, everything else gets config.layout.
// A tablet may ask for another layout it picked (`chosen`, e.g. "layouts/combat.json");
// it gets that one when it exists, else the PC's choice for its device.
function loadLayout(device, chosen) {
  const text = (chosen && layoutFiles().includes(chosen) && readConfigFile(chosen))
    || (device === "phone" && config.phoneLayout && readConfigFile(config.phoneLayout)) || readConfigFile(config.layout);
  if (!text) throw new Error(`Layout file not found: ${config.layout}`);
  const layout = JSON.parse(text.toString("utf8"));
  return { layout, buttons: prepareLayout(layout) };
}

// Gives every button its keys (from the keybind files when it has an scAction) and
// scancodes, and returns them by id. Throws on an unknown key or a duplicate id, so the
// layout editor's saves get the same checks as loading.
function prepareLayout(layout) {
  const overrides = readActionmapOverrides(config.actionmaps);
  const buttons = new Map();
  (function collect(node) {
    if (Array.isArray(node)) { node.forEach(collect); return; }
    if (!node || typeof node !== "object") return;
    if (typeof node.id === "string" && typeof node.keys === "string") {
      if (node.scAction && overrides[node.scAction]) node.keys = overrides[node.scAction];
      node.scancodes = parseCombo(node.keys);
      if (buttons.has(node.id)) throw new Error(`Duplicate button id "${node.id}"`);
      buttons.set(node.id, node);
    }
    for (const v of Object.values(node)) if (v && typeof v === "object") collect(v);
  })(layout);
  return buttons;
}

// ---------- key sender ----------

// Process name of the game (StarCitizen.exe). With onlyWhenGameFocused, presses are
// skipped unless this process owns the window in front.
const GAME_PROCESS = "StarCitizen";

// Returns the path to native/<name>.exe. The installer ships it prebuilt next to
// TabletDeck.exe. From source, it's built from <name>.cs with the C# compiler that ships
// with Windows, and rebuilt when the .cs is newer, so an update's changes reach it.
function buildNative(name, what, refs = []) {
  const exe = path.join(APP_DIR, "native", `${name}.exe`);
  if (INSTALLED) {
    if (!fs.existsSync(exe)) throw new Error(`${exe} is missing; reinstall Tablet Deck`);
    return exe;
  }
  const src = path.join(APP_DIR, "native", `${name}.cs`);
  if (!fs.existsSync(exe) || fs.statSync(exe).mtimeMs < fs.statSync(src).mtimeMs) {
    const fw = path.join(process.env.WINDIR || "C:\\Windows", "Microsoft.NET", "Framework64", "v4.0.30319");
    console.log(`Compiling ${what}...`);
    // WPF holds System.Speech.dll, which csc doesn't search by default.
    try {
      execFileSync(path.join(fw, "csc.exe"), ["/nologo", "/optimize", "/target:exe", "/platform:x64", `/lib:${path.join(fw, "WPF")}`, ...refs.map((r) => `/r:${r}`), `/out:${exe}`, src], { stdio: "inherit" });
    } catch (err) {
      throw new Error(`native/${name}.cs didn't compile (${err.message})`);
    }
  }
  return exe;
}

// Starts native/keysender.exe. `proc.focusWaiters` holds pending "focus" queries, answered in order.
function spawnKeySender(onExit) {
  const proc = spawn(buildNative("keysender", "key sender"), [], { stdio: ["pipe", "pipe", "inherit"] });
  proc.focusWaiters = [];
  let pending = "";
  proc.stdout.on("data", (d) => {
    const lines = (pending + d.toString()).split(/\r?\n/);
    pending = lines.pop();
    for (const line of lines) {
      if (line.startsWith("err")) console.error("key sender:", line);
      else if (line.startsWith("focus ")) proc.focusWaiters.shift()?.(line === "focus 1");
    }
  });
  proc.on("exit", (code) => {
    for (const answer of proc.focusWaiters.splice(0)) answer(false);
    onExit(code);
  });
  return proc;
}

// Resolves true if the game owns the window in front, asking the given key sender process.
function askFocus(proc) {
  return new Promise((resolve) => {
    let done = false;
    const answer = (focused) => { if (!done) { done = true; resolve(focused); } };
    proc.focusWaiters.push(answer);
    proc.stdin.write(`focus ${GAME_PROCESS}\n`);
    // An unanswered query counts as not focused; its late reply still shifts it off the queue.
    setTimeout(() => answer(false), 1000);
  });
}

let sender = null;
function startSender() {
  if (DRY_RUN) { startFocusProbe(); return; }
  sender = spawnKeySender((code) => {
    console.error(`Key sender exited (${code}); restarting.`);
    sender = null;
    setTimeout(startSender, 500);
  });
}

// Resolves true when keys may be sent: the check is off, it's a dry run, or the game is in front.
function gameFocused() {
  if (!config.onlyWhenGameFocused || !sender) return Promise.resolve(true);
  return askFocus(sender);
}

// Dry runs press nothing, but a key sender that only ever gets "focus" queries lets the
// sandbox log say whether the focus check would have skipped a press. It's kept apart
// from `sender`, so sendKey can't reach it.
let focusProbe = null;
function startFocusProbe() {
  if (!config.onlyWhenGameFocused) return;
  try {
    focusProbe = spawnKeySender((code) => { console.error(`Focus probe exited (${code}).`); focusProbe = null; });
  } catch (err) {
    console.error(`Focus probe unavailable: ${err.message}`);
  }
}

// Dry run: true if the focus check would have skipped a press, null when that's unknown.
async function wouldBlock() {
  if (!config.onlyWhenGameFocused) return false;
  if (!focusProbe) return null;
  return !(await askFocus(focusProbe));
}

function sendKey(direction, [code, ext]) {
  const cmd = `${direction} ${code.toString(16)} ${ext}`;
  if (DRY_RUN || !sender) console.log("  key", cmd);
  else sender.stdin.write(cmd + "\n");
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Reference-count physical keys so two buttons sharing a modifier don't release it early.
const keyHolds = new Map();
function keyDown(sc) {
  const k = sc.join(":");
  const n = keyHolds.get(k) || 0;
  keyHolds.set(k, n + 1);
  if (n === 0) sendKey("d", sc);
}
function keyUp(sc) {
  const k = sc.join(":");
  const n = keyHolds.get(k) || 0;
  if (n <= 1) { keyHolds.delete(k); if (n === 1) sendKey("u", sc); }
  else keyHolds.set(k, n - 1);
}

async function pressCombo(scancodes) {
  for (const sc of scancodes) { keyDown(sc); await sleep(15); }
}
async function releaseCombo(scancodes) {
  for (const sc of [...scancodes].reverse()) { keyUp(sc); await sleep(15); }
}

// Tells a tablet a press was skipped because the game isn't the window in front.
function skipped(btn, client) {
  console.log(`${btn.label}: skipped, Star Citizen not focused`);
  client?.send({ t: "blocked", id: btn.id });
}

// ---------- sandbox event stream (dry run only) ----------

// The showcase page (public/showcase.html) reads these as its live "keys sent" log.
const eventStreams = new Set();
function emit(event) {
  if (!DRY_RUN) return;
  const data = `data: ${JSON.stringify({ time: Date.now(), ...event })}\n\n`;
  for (const res of eventStreams) res.write(data);
}
setInterval(() => { for (const res of eventStreams) res.write(": ping\n\n"); }, 20000).unref();

// Logs a press with the focus verdict. Queued so a hold's "up" can't overtake its "down".
let reports = Promise.resolve();
function report(btn, kind, extra = {}) {
  if (!DRY_RUN || !eventStreams.size) return;
  const time = Date.now(); // when it happened, not when the focus probe answered
  reports = reports.then(async () => {
    // Releases always go through, so only presses get a focus verdict.
    const blocked = kind === "up" ? false : await wouldBlock();
    emit({ t: "press", time, id: btn.id, label: btn.label, keys: btn.keys, kind, blocked, ...extra });
  });
}

// Buttons with a "state" switch something on or off in the game (doors open, gear down,
// lights on). The state is kept here, per group, so every tablet, voice commands and
// redrawn panels agree; buttons light up while their group is on. "set": true or false
// sets it (Open/Close Doors), otherwise a press flips it. The deck can't see the game, so
// this is only what Tablet Deck pressed: it starts all off, and tablets can reset it.
const buttonStates = new Map(); // group -> true while on
function applyState(btn) {
  const s = btn.state;
  if (!s || typeof s.group !== "string") return;
  const on = typeof s.set === "boolean" ? s.set : !buttonStates.get(s.group);
  if (on) buttonStates.set(s.group, true); else buttonStates.delete(s.group);
  for (const c of tablets) c.send({ t: "state", group: s.group, on });
}

// Presses a button's keys for `ms`, then releases them, unless the game isn't in front.
// `kind` (tap, timed, voice) only labels the press in the sandbox log.
async function tapButton(btn, client, ms = btn.ms || config.tapMs, kind = btn.mode === "timed" ? "timed" : "tap") {
  if (!(await gameFocused())) { skipped(btn, client); return; }
  console.log(`${btn.label} (${btn.keys}) tap`);
  report(btn, kind, { ms });
  applyState(btn);
  await pressCombo(btn.scancodes);
  await sleep(ms);
  await releaseCombo(btn.scancodes);
}

// ---------- voice commands ----------

// Button label as a spoken phrase: "Shields +" -> "shields up", "Master: SCM" -> "master scm".
function spoken(label) {
  return String(label).toLowerCase()
    .replace(/\s*\+$/, " up").replace(/\s*[−-]$/, " down").replace(/&/g, " and ")
    .replace(/[^a-z0-9' ]+/g, " ").replace(/\s+/g, " ").trim();
}

// Phrase -> button id: each button's label plus its "voice" phrases. "voice": false leaves a
// button out (eject, self-destruct). A phrase that fits buttons with different keys is
// dropped rather than guessed; buttons with the same keys (e.g. Mining in two panels) share it.
// Emotes answer to "emote wave", so a stray "wait" or "point" doesn't set one off; with
// shortEmotes they answer to "wave" as well.
function voiceCommands(buttons) {
  const byPhrase = new Map(); // phrase -> button, or null when ambiguous
  for (const btn of buttons.values()) {
    if (btn.voice === false) continue;
    let phrases = [btn.label, ...(btn.voice || [])].map(spoken).filter(Boolean);
    if (/^emote_/.test(btn.scAction || "")) {
      const prefixed = phrases.map((p) => (p.startsWith("emote ") ? p : `emote ${p}`));
      phrases = config.shortEmotes ? [...prefixed, ...phrases] : prefixed;
    }
    for (const phrase of phrases) {
      const other = byPhrase.get(phrase);
      if (other === undefined) byPhrase.set(phrase, btn);
      else if (other && other.keys !== btn.keys) {
        byPhrase.set(phrase, null);
        console.warn(`Voice: "${phrase}" fits both ${other.label} and ${btn.label}, so it's left out.`);
      }
    }
  }
  return new Map([...byPhrase].filter(([, btn]) => btn).map(([phrase, btn]) => [phrase, btn.id]));
}

let voice = null;      // the voice listener, once its grammar is loaded
let voiceList = [];    // what can be said: [phrase, button label] pairs
let voiceFromKey = false; // the last listening came from the PC's push-to-talk key
let voiceStatus = null;   // the window's "voice ready" status, re-sent when push-to-talk changes
let voiceOwner = null; // the tablet holding the talk button; it gets the results
// What tablets are told about voice: "off", "starting", "ready", or "unavailable" with the
// reason. They hide the mic unless it can work, and the voice list explains why.
let tabletVoice = { state: config.voice ? "starting" : "off" };
function setTabletVoice(state, reason) {
  tabletVoice = reason ? { state, reason } : { state };
  for (const c of tablets) c.send({ t: "voice-status", ...tabletVoice });
}
function startVoice() {
  if (!config.voice) { ui({ t: "voice", state: "off" }); setTabletVoice("off"); return; }
  let exe;
  try { exe = buildNative("voice", "voice commands", ["System.Speech.dll"]); } catch (err) {
    console.error(`Voice commands unavailable: ${err.message}.`);
    ui({ t: "voice", state: "error", text: err.message });
    setTabletVoice("unavailable", err.message);
    return;
  }
  const { buttons } = loadLayout();
  const commands = voiceCommands(buttons);
  // [phrase, button label] pairs by label, for the window's list and the tablet's (/voice.json).
  voiceList = [...commands].map(([phrase, id]) => [phrase, buttons.get(id).label])
    .sort((a, b) => a[1].localeCompare(b[1]) || a[0].localeCompare(b[0]));
  ui({ t: "voice-commands", commands: voiceList });
  const proc = spawn(exe, [config.voiceLanguage], { stdio: ["pipe", "pipe", "inherit"] });
  let pending = "";
  proc.stdout.on("data", (d) => {
    const lines = (pending + d.toString()).split(/\r?\n/);
    pending = lines.pop();
    for (const line of lines) {
      if (line.startsWith("ready")) {
        voice = proc;
        sendPushToTalk();
        const key = config.pttKey ? ` or hold ${config.pttKeyName || "the push-to-talk key"} on the PC` : "";
        console.log(`Voice commands ready (${commands.size} phrases, ${line.slice(6)}). Hold the mic button on the tablet${key} and speak.`);
        voiceStatus = { t: "voice", state: "ready", phrases: commands.size, language: line.slice(6) };
        ui({ ...voiceStatus, ptt: config.pttKey ? config.pttKeyName || "on" : "" });
        setTabletVoice("ready");
      } else if (line === "ptt 1" || line === "ptt 0") {
        // Push-to-talk from a PC key: results go to every tablet, and their mic buttons light up.
        voiceFromKey = true;
        voiceOwner = null;
        for (const c of tablets) c.send({ t: "listening", on: line === "ptt 1" });
        emit({ t: "voice", result: line === "ptt 1" ? "listen" : "stop", running: true });
      } else if (line.startsWith("err")) {
        console.error(`Voice commands unavailable: ${line.slice(4)}. See "Voice commands" at https://github.com/nodieCode/tablet-deck#voice-commands`);
        ui({ t: "voice", state: "error", text: line.slice(4) });
        setTabletVoice("unavailable", line.slice(4));
      } else if (line.startsWith("heard ")) {
        const [, id, confidence, ...words] = line.split(" ");
        void onVoice(id, Number(confidence), words.join(" "));
      } else if (line === "none") {
        void onVoice(null, 0, "");
      }
    }
  });
  proc.on("exit", (code) => {
    const wasReady = voice === proc;
    if (wasReady) voice = null;
    console.error(`Voice listener exited (${code})${wasReady ? "; restarting" : ""}.`);
    ui({ t: "voice", state: wasReady ? "starting" : "error", text: `voice listener exited (${code})` });
    setTabletVoice(wasReady ? "starting" : "unavailable", `the voice listener exited (${code})`);
    if (wasReady) setTimeout(startVoice, 2000);
  });
  for (const [phrase, id] of commands) proc.stdin.write(`phrase ${id} ${phrase}\n`);
  proc.stdin.write("ready\n");
}

// Presses the button for a recognised phrase. The layout is re-read so in-game rebinds count.
// Tells the voice listener the push-to-talk key (Windows virtual-key code, 0 = none).
function sendPushToTalk() {
  if (!voice) return;
  voice.stdin.write(`ptt ${config.pttKey || 0} ${config.pttMode === "toggle" ? "toggle" : "hold"}\n`);
  voice.stdin.write(`chime ${config.pttSound === false ? "off" : "on"}\n`);
}

async function onVoice(id, confidence, text) {
  // After push-to-talk on the PC there's no tablet holding the mic, so all of them hear back.
  const client = voiceFromKey ? { send: (obj) => { for (const c of tablets) c.send(obj); } } : voiceOwner;
  const btn = id ? loadLayout().buttons.get(id) : null;
  if (!btn || confidence < config.voiceConfidence) {
    console.log(btn ? `Voice: "${text}" ignored (confidence ${confidence.toFixed(2)})` : "Voice: didn't catch that");
    emit(btn ? { t: "voice", result: "ignored", text, label: btn.label, confidence } : { t: "voice", result: "unheard" });
    ui(btn ? { t: "heard", result: "ignored", text, label: btn.label, confidence } : { t: "heard", result: "unheard" });
    client?.send({ t: "unheard" });
    return;
  }
  console.log(`Voice: "${text}" -> ${btn.label} (confidence ${confidence.toFixed(2)})`);
  emit({ t: "voice", result: "heard", text, label: btn.label, confidence });
  ui({ t: "heard", result: "accepted", text, label: btn.label, confidence });
  client?.send({ t: "heard", id: btn.id, label: btn.label, text });
  // Hold buttons (quantum engage, laser power) get a longer press, set per button.
  await tapButton(btn, client, btn.voiceMs || btn.ms || config.tapMs, "voice");
}

// ---------- open a website on the PC ----------

// A tablet asks by position in config.links, so it can only open the addresses set up on
// this PC, never one of its own. Opens in the default browser; dry runs only log it.
let lastOpen = 0;
function openLink(index, client) {
  const link = (config.links || [])[index];
  if (!link || !/^https:\/\/[^\s"<>]{3,300}$/.test(link.url)) return;
  if (Date.now() - lastOpen < 1500) return; // one at a time, even on a double tap
  lastOpen = Date.now();
  if (DRY_RUN) {
    console.log(`Open on PC: ${link.name} (${link.url}) - dry run, not opened`);
    client.send({ t: "opened", name: link.name, dryRun: true });
    return;
  }
  console.log(`Opening ${link.name} (${link.url}) on this PC.`);
  // rundll32 hands the address to the default browser without a command shell in between.
  spawn("rundll32.exe", ["url.dll,FileProtocolHandler", link.url], { detached: true, stdio: "ignore" }).unref();
  client.send({ t: "opened", name: link.name });
}

// ---------- minimal WebSocket server (RFC 6455, text frames) ----------

function wsAccept(req, socket) {
  const key = req.headers["sec-websocket-key"];
  const accept = crypto.createHash("sha1").update(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
    `Sec-WebSocket-Accept: ${accept}\r\n\r\n`
  );
  socket.setNoDelay(true);
}

function wsSend(socket, text) {
  const payload = Buffer.from(text);
  let header;
  if (payload.length < 126) header = Buffer.from([0x81, payload.length]);
  else if (payload.length < 65536) { header = Buffer.alloc(4); header[0] = 0x81; header[1] = 126; header.writeUInt16BE(payload.length, 2); }
  else { header = Buffer.alloc(10); header[0] = 0x81; header[1] = 127; header.writeBigUInt64BE(BigInt(payload.length), 2); }
  socket.write(Buffer.concat([header, payload]));
}

// Calls onText for each complete text frame; handles ping and close.
function wsReader(socket, onText, onClose) {
  let buf = Buffer.alloc(0);
  socket.on("data", (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    while (buf.length >= 2) {
      const opcode = buf[0] & 0x0f;
      const masked = (buf[1] & 0x80) !== 0;
      let len = buf[1] & 0x7f;
      let off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const maskOff = off;
      if (masked) off += 4;
      if (buf.length < off + len) return;
      const data = Buffer.from(buf.subarray(off, off + len));
      if (masked) for (let i = 0; i < data.length; i++) data[i] ^= buf[maskOff + (i % 4)];
      buf = buf.subarray(off + len);
      if (opcode === 0x1) onText(data.toString("utf8"));
      else if (opcode === 0x8) { socket.end(Buffer.from([0x88, 0])); return; }
      else if (opcode === 0x9) socket.write(Buffer.concat([Buffer.from([0x8a, data.length]), data]));
    }
  });
  socket.on("close", onClose);
  socket.on("error", () => socket.destroy());
}

// ---------- HTTP ----------

const MIME = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".css": "text/css", ".json": "application/json", ".svg": "image/svg+xml", ".png": "image/png", ".webmanifest": "application/manifest+json" };

// ---------- layout editor (public/editor.html) ----------

const fromThisPC = (req) => ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(req.socket.remoteAddress);

// Wrong pairing codes: after PAIR_TRIES from one device within a minute, that device is
// refused for a minute, even with the right code, so nothing on the network can keep
// guessing. The PC itself is exempt (it can read the code from config.json anyway).
const PAIR_TRIES = 5, PAIR_WINDOW_MS = 60_000, PAIR_LOCK_MS = 60_000;
const pairFails = new Map(); // remote address -> { count, first, lockedUntil }
// "ok", "wrong" or "locked".
function checkPairing(req, code) {
  if (code === config.token && fromThisPC(req)) return "ok";
  const ip = req.socket.remoteAddress || "";
  const now = Date.now();
  const fails = pairFails.get(ip);
  if (fails && fails.lockedUntil > now) return "locked";
  if (code === config.token) { pairFails.delete(ip); return "ok"; }
  if (fromThisPC(req)) return "wrong";
  const rec = fails && now - fails.first < PAIR_WINDOW_MS ? fails : { count: 0, first: now, lockedUntil: 0 };
  rec.count++;
  if (rec.count >= PAIR_TRIES) {
    rec.lockedUntil = now + PAIR_LOCK_MS;
    console.log(`${PAIR_TRIES} wrong pairing codes from ${ip}; ignoring it for a minute.`);
  }
  pairFails.set(ip, rec);
  if (pairFails.size > 1000) for (const [k, v] of pairFails) if (v.lockedUntil < now && now - v.first > PAIR_WINDOW_MS) pairFails.delete(k);
  return "wrong";
}
// For the HTTP routes: answers 403 (wrong code) or 429 (too many) and returns true when refused.
function refusePairing(req, res, url) {
  const result = checkPairing(req, url.searchParams.get("t"));
  if (result === "ok") return false;
  res.writeHead(result === "locked" ? 429 : 403);
  res.end(result === "locked" ? "too many wrong pairing codes" : "bad token");
  return true;
}
function sendJson(res, status, obj) {
  res.writeHead(status, { "Content-Type": MIME[".json"], "Cache-Control": "no-store" });
  res.end(JSON.stringify(obj));
}

// Every layout file: the bundled ones plus the player's own in the settings folder.
function layoutFiles() {
  let bundled;
  if (INSTALLED) {
    try { bundled = JSON.parse(readBundled("bundled-files.json")); } catch { bundled = []; }
  } else {
    bundled = fs.readdirSync(path.join(__dirname, "layouts")).map((f) => `layouts/${f}`);
  }
  const files = new Set(bundled.filter((f) => /^layouts\/[^/]+\.json$/.test(f)));
  const own = path.join(DATA_DIR, "layouts");
  if (fs.existsSync(own)) for (const f of fs.readdirSync(own)) if (f.endsWith(".json")) files.add(`layouts/${f}`);
  return [...files].sort();
}

// Tablets that are connected, so a saved layout can make them reload.
const tablets = new Set();
const reloadTablets = () => { for (const client of tablets) client.send({ t: "reload" }); };

// The editor reads layouts and saves the player's own; it only answers this PC, so a
// device on the network can't change files even with the pairing code.
function handleEditor(req, res, url) {
  if (url.searchParams.get("t") !== config.token) return sendJson(res, 403, { error: "Wrong pairing code." });
  if (!fromThisPC(req)) return sendJson(res, 403, { error: "The layout editor only works on the PC running Tablet Deck (open it at http://localhost)." });

  if (url.pathname === "/editor/layouts") {
    const layouts = [];
    for (const file of layoutFiles()) {
      try {
        const l = JSON.parse(readConfigFile(file));
        layouts.push({ file, name: l.name || file, canvas: l.canvas || null, custom: l.custom === true,
          active: file === config.layout ? "tablet" : file === config.phoneLayout ? "phone" : null });
      } catch { /* not a layout */ }
    }
    return sendJson(res, 200, {
      layouts,
      keys: Object.keys(SCANCODES).filter((k) => !/^num\d$/.test(k)),
      // scAction -> keys from the player's Star Citizen keybinds, for "Follow game keybind".
      overrides: readActionmapOverrides(config.actionmaps),
    });
  }

  if (url.pathname === "/editor/layout") {
    const file = url.searchParams.get("file") || "";
    if (!layoutFiles().includes(file)) return sendJson(res, 404, { error: "No such layout." });
    return sendJson(res, 200, JSON.parse(readConfigFile(file)));
  }

  if (url.pathname === "/editor/save" && req.method === "POST") {
    let body = "";
    req.setEncoding("utf8");
    req.on("data", (chunk) => { body += chunk; if (body.length > 4e6) req.destroy(); });
    req.on("end", () => {
      let data;
      try { data = JSON.parse(body); } catch { return sendJson(res, 400, { error: "The editor sent something unreadable." }); }
      const name = String(data.name || "").trim().toLowerCase();
      if (!/^[a-z0-9][a-z0-9-]{0,39}$/.test(name)) return sendJson(res, 400, { error: "Use letters, numbers and dashes for the name." });
      const file = `layouts/${name}.json`;
      // Only the player's own layouts (marked "custom") may be overwritten.
      const existing = readConfigFile(file);
      if (existing) {
        let custom = false;
        try { custom = JSON.parse(existing).custom === true; } catch { /* unreadable: treat as not ours */ }
        if (!custom) return sendJson(res, 400, { error: `"${name}" is a built-in layout. Pick another name.` });
      }
      const layout = data.layout;
      if (!layout || typeof layout !== "object" || !Array.isArray(layout.canvas)) return sendJson(res, 400, { error: "That isn't a layout." });
      layout.custom = true;
      try { prepareLayout(JSON.parse(JSON.stringify(layout))); } catch (err) { return sendJson(res, 400, { error: err.message }); }
      const target = path.join(DATA_DIR, file);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.writeFileSync(target, JSON.stringify(layout, null, 2) + "\n");
      console.log(`Saved layout ${file}.`);
      const kind = layout.canvas[1] < 500 ? "phoneLayout" : "layout";
      if (data.use && config[kind] !== file) {
        config[kind] = file;
        saveSetting(kind, file);
        console.log(`${kind === "layout" ? "Tablets" : "Phones"} now use ${file}.`);
      }
      if (config.layout === file || config.phoneLayout === file) {
        reloadTablets();
        // Voice commands come from the tablet layout; restart the listener so they match.
        if (kind === "layout" && voice) voice.kill();
      }
      sendJson(res, 200, { ok: true, file, active: config[kind] === file });
    });
    return;
  }
  sendJson(res, 404, { error: "Not found." });
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, "http://x");
  // Server-sent events for the showcase page's key log; dry runs only.
  if (url.pathname === "/events" && DRY_RUN) {
    if (refusePairing(req, res, url)) return;
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-store", Connection: "keep-alive" });
    res.write(`data: ${JSON.stringify({ t: "hello", time: Date.now(), focusCheck: config.onlyWhenGameFocused, focusProbe: Boolean(focusProbe), voice: Boolean(voice) })}\n\n`);
    eventStreams.add(res);
    req.on("close", () => eventStreams.delete(res));
    return;
  }
  // The voice commands list, for the tablet's "?" next to the mic button.
  // The names of the websites a tablet may open on this PC; the addresses stay here.
  if (url.pathname === "/links.json") {
    if (refusePairing(req, res, url)) return;
    return sendJson(res, 200, { links: (config.links || []).map((l) => l.name) });
  }
  if (url.pathname === "/voice.json") {
    if (refusePairing(req, res, url)) return;
    res.writeHead(200, { "Content-Type": MIME[".json"], "Cache-Control": "no-store" });
    res.end(JSON.stringify({ on: Boolean(config.voice), ready: Boolean(voice), ...tabletVoice, commands: voiceList }));
    return;
  }
  // Layouts a tablet can pick in its settings sheet: the ones made for its screen (phone
  // layouts are under 500 px tall), plus tabbed grid layouts, which fit any screen.
  if (url.pathname === "/layouts.json") {
    if (refusePairing(req, res, url)) return;
    const phone = url.searchParams.get("device") === "phone";
    const layouts = [];
    for (const file of layoutFiles()) {
      try {
        const l = JSON.parse(readConfigFile(file));
        if (l.canvas && (l.canvas[1] < 500) !== phone) continue;
        layouts.push({ file, name: l.name || file, custom: l.custom === true });
      } catch { /* not a layout */ }
    }
    const pcChoice = phone && config.phoneLayout && readConfigFile(config.phoneLayout) ? config.phoneLayout : config.layout;
    return sendJson(res, 200, { layouts, pcChoice });
  }
  if (url.pathname === "/layout.json") {
    if (refusePairing(req, res, url)) return;
    try {
      const { layout } = loadLayout(url.searchParams.get("device"), url.searchParams.get("layout"));
      res.writeHead(200, { "Content-Type": MIME[".json"], "Cache-Control": "no-store" });
      res.end(JSON.stringify(layout, (k, v) => (k === "scancodes" ? undefined : v)));
    } catch (err) {
      res.writeHead(500); res.end(String(err.message));
    }
    return;
  }
  if (url.pathname.startsWith("/editor/")) { handleEditor(req, res, url); return; }
  const rel = path.posix.normalize(`public/${url.pathname === "/" ? "index.html" : url.pathname.slice(1)}`);
  // /layouts/ serves the built-in layouts as files, for the deck's demo mode (?demo).
  const body = rel.startsWith("public/layouts/") ? readBundled(rel.slice("public/".length))
    : rel.startsWith("public/") ? readBundled(rel) : null;
  if (!body) { res.writeHead(404); res.end("not found"); return; }
  res.writeHead(200, { "Content-Type": MIME[path.extname(rel)] || "application/octet-stream" });
  res.end(body);
});

let connectedTablets = 0;
server.on("upgrade", (req, socket) => {
  const url = new URL(req.url, "http://x");
  const pairing = url.pathname === "/ws" ? checkPairing(req, url.searchParams.get("t")) : "wrong";
  if (pairing === "locked") { socket.end("HTTP/1.1 429 Too Many Requests\r\n\r\n"); return; }
  if (pairing !== "ok") {
    socket.end("HTTP/1.1 403 Forbidden\r\n\r\n"); return;
  }
  let buttons;
  try { ({ buttons } = loadLayout(url.searchParams.get("device"), url.searchParams.get("layout"))); } catch (err) {
    console.error("Layout error:", err.message); socket.end("HTTP/1.1 500 Layout Error\r\n\r\n"); return;
  }
  wsAccept(req, socket);
  const who = socket.remoteAddress;
  const device = url.searchParams.get("device") || "tablet";
  console.log(`Tablet connected: ${who}`);
  emit({ t: "client", on: true, device });
  ui({ t: "clients", count: ++connectedTablets });
  const client = { send: (obj) => { if (!socket.destroyed) wsSend(socket, JSON.stringify(obj)); } };
  tablets.add(client);
  client.send({ t: "hello", voice: Boolean(config.voice), voiceStatus: tabletVoice, states: Object.fromEntries(buttonStates) });

  const held = new Map(); // button id -> { sc } currently pressed by this tablet
  let lastSeen = Date.now();
  const releaseAll = async () => {
    for (const [id, hold] of held) { held.delete(id); await releaseCombo(hold.sc); }
  };
  // If the tablet sleeps or drops Wi-Fi mid-hold, don't leave keys stuck down.
  const watchdog = setInterval(() => {
    if (Date.now() - lastSeen > 5000) { void releaseAll(); socket.destroy(); }
  }, 1000);

  wsReader(socket, async (text) => {
    lastSeen = Date.now();
    let msg;
    try { msg = JSON.parse(text); } catch { return; }
    if (msg.t === "hb") return;
    if (msg.t === "voice") {
      emit({ t: "voice", result: msg.on ? "listen" : "stop", running: Boolean(voice) });
      if (!voice) {
        client.send({ t: "voice-off", reason: config.voice ? "Voice commands aren't running on the PC" : "Voice commands are turned off" });
      } else if (msg.on) {
        voiceOwner = client;
        voiceFromKey = false;
        voice.stdin.write("listen\n");
      } else if (voiceOwner === client) {
        voice.stdin.write("stop\n");
      }
      return;
    }
    if (msg.t === "open") { openLink(msg.index, client); return; }
    if (msg.t === "reset-states") {
      buttonStates.clear();
      for (const c of tablets) c.send({ t: "states", states: {} });
      return;
    }
    const btn = buttons.get(msg.id);
    if (!btn) return;
    if (msg.t === "down" && !held.has(btn.id)) {
      // Claim the hold before the focus check so a quick "up" during it cancels the press.
      const hold = { sc: btn.scancodes };
      held.set(btn.id, hold);
      if (!(await gameFocused())) {
        if (held.get(btn.id) === hold) held.delete(btn.id);
        skipped(btn, client);
        return;
      }
      if (held.get(btn.id) !== hold) return;
      console.log(`${btn.label} (${btn.keys}) down`);
      applyState(btn);
      report(btn, "down");
      await pressCombo(btn.scancodes);
    } else if (msg.t === "up" && held.has(btn.id)) {
      // Releases always go through, even after the game loses focus, so no key stays stuck.
      const { sc } = held.get(btn.id);
      held.delete(btn.id);
      console.log(`${btn.label} up`);
      report(btn, "up");
      await releaseCombo(sc);
    } else if (msg.t === "tap") {
      await tapButton(btn, client);
    }
  }, () => {
    clearInterval(watchdog);
    tablets.delete(client);
    void releaseAll();
    if (voiceOwner === client) { voice?.stdin.write("stop\n"); voiceOwner = null; }
    console.log(`Tablet disconnected: ${who}`);
    emit({ t: "client", on: false, device });
    ui({ t: "clients", count: --connectedTablets });
  });
});

// ---------- start ----------

try { loadLayout(); loadLayout("phone"); } catch (err) { fatal(`Layout error: ${err.message}`); }
startSender();
startVoice();
server.on("error", (err) => {
  if (err.code !== "EADDRINUSE") throw err;
  fatal(`Port ${config.port} is already in use. Is Tablet Deck already running? Otherwise change "port" in ${CONFIG_PATH}.`);
});
// Adapters a tablet can't be on: VPNs, virtual machines, WSL and Docker. Matched on the
// name Windows gives the adapter ("vEthernet (WSL)", "NordLynx", "Tailscale", ...).
const VIRTUAL_ADAPTER = new RegExp([
  "vEthernet", "WSL", "Hyper-V", "VirtualBox", "VMware", "VMnet", "Docker", "Loopback", "Bluetooth",
  "Tailscale", "ZeroTier", "Hamachi", "NordLynx", "Nord", "Proton", "WireGuard", "OpenVPN", "TAP-", "Wintun",
  "Mullvad", "ExpressVPN", "Surfshark", "PIA", "CyberGhost", "Windscribe", "Cisco", "AnyConnect",
  "Fortinet", "FortiClient", "GlobalProtect", "PANGP", "Juniper", "Pulse", "SonicWall", "Radmin", "VPN",
].join("|"), "i");

// This PC's network addresses, best guess first, and the one a tablet most likely shares
// (the QR code): the "host" setting if set, else a private address on a real adapter.
function addresses() {
  const all = [];
  for (const [name, infos] of Object.entries(os.networkInterfaces())) {
    for (const i of infos || []) {
      if (i.family === "IPv4" && !i.internal && !i.address.startsWith("169.254")) {
        all.push({ name, ip: i.address, virtual: VIRTUAL_ADAPTER.test(name) });
      }
    }
  }
  // Home networks are usually 192.168.x.x, then 10.x.x.x or 172.16-31.x.x.
  const rank = (a) => (a.virtual ? 10 : 0) + (a.ip.startsWith("192.168.") ? 0 : a.ip.startsWith("10.") ? 1
    : /^172\.(1[6-9]|2\d|3[01])\./.test(a.ip) ? 2 : 3);
  const sorted = all.sort((a, b) => rank(a) - rank(b));
  const lan = config.host || (sorted[0] && sorted[0].ip);
  return { all: sorted, ips: sorted.map((a) => a.ip), lan };
}

// Everything the app window shows and its settings dialog edits. Sent again after a
// setting changes, so a new pairing code gets a new QR code.
function sendReady() {
  const { ips, lan } = addresses();
  const link = lan ? `http://${lan}:${config.port}/?t=${config.token}` : null;
  ui({
    t: "ready", version: VERSION, port: config.port, token: config.token, link, settings: CONFIG_PATH,
    others: ips.filter((ip) => ip !== lan).map((ip) => `http://${ip}:${config.port}`),
    // The window draws the QR code itself; rows of "1" (dark) and "0".
    qr: link ? qr.matrix(link).map((row) => row.map((dark) => (dark ? "1" : "0")).join("")) : [],
    dryRun: DRY_RUN, focusCheck: Boolean(config.onlyWhenGameFocused), voiceConfidence: config.voiceConfidence,
    autoUpdate: config.autoUpdate !== false, tapMs: config.tapMs, voice: Boolean(config.voice),
    voiceLanguage: config.voiceLanguage, portFromCommandLine: Boolean(PORT_OVERRIDE),
  });
}

server.listen(config.port, "0.0.0.0", () => {
  const { all, ips, lan } = addresses();
  console.log(`\nTablet Deck${VERSION ? ` ${VERSION}` : ""} running${DRY_RUN ? " (DRY RUN: no keys are sent)" : ""}.`);
  if (UI) { sendReady(); return; }
  if (INSTALLED) console.log(`Settings: ${CONFIG_PATH}`);
  console.log("Open this on your tablet (same Wi-Fi/network):");
  if (config.host && !ips.includes(config.host)) console.log(`   http://${config.host}:${config.port}/?t=${config.token}   (the "host" setting)`);
  for (const a of all) {
    const note = a.virtual ? "   (VPN or virtual adapter: tablets usually can't reach this)" : "";
    console.log(`   ${a.name}: http://${a.ip}:${config.port}/?t=${config.token}${note}`);
  }
  if (lan) {
    console.log(`\nOr scan this with the tablet's camera (${lan}${config.host ? `, from the "host" setting` : ""}):`);
    console.log(`If the tablet can't open it, set "host" in ${CONFIG_PATH} to the address of your Wi-Fi or Ethernet.\n`);
    console.log(qr.toTerminal(`http://${lan}:${config.port}/?t=${config.token}`, { color: process.stdout.isTTY }));
  }
  console.log(`\nPairing code: ${config.token}`);
  console.log(`(You can also open http://${lan || "<this PC's IP>"}:${config.port} and type the code.)`);
  console.log(`\nLayout editor (on this PC): http://localhost:${config.port}/editor.html?t=${config.token}`);
  console.log(config.onlyWhenGameFocused
    ? "\nKeys are only sent while Star Citizen is the window in front.\n"
    : "\nKeep the game window focused; keys go to whatever window is in front.\n");
});

const shutdown = () => {
  for (const k of keyHolds.keys()) sendKey("u", k.split(":").map(Number));
  setTimeout(() => process.exit(0), 100);
};
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
// Closing the console window (SIGHUP) or Ctrl+Break also releases held keys first.
process.on("SIGHUP", shutdown);
process.on("SIGBREAK", shutdown);
// The app window asks the server to stop with "quit"; if the window itself goes away,
// stdin closes, which stops the server the same way.
// Commands from the app window, one per line: "quit", or "set <name> <value>" from its
// slider, checkboxes and settings dialog. Each setting is checked (SETTINGS), saved to
// config.json and applied at once; the window restarts the server after port or voice ones.
function applySetting(name, text) {
  const value = SETTINGS[name] ? SETTINGS[name](text) : undefined;
  if (value === undefined) { console.error(`Setting not changed: "${text}" isn't a valid ${name}.`); return; }
  saveSetting(name, value);
  // The port only changes when the server restarts, as the window does next.
  if (name !== "port") config[name] = value;
  if (name === "voiceConfidence") {
    console.log(`Voice strictness set to ${Math.round(value * 100)}%.`);
    ui({ t: "voiceConfidence", value });
    return;
  }
  console.log(`Saved ${name}: ${value === true ? "on" : value === false ? "off" : Array.isArray(value) ? value.map((l) => l.name).join(", ") || "none" : value}.`);
  if (name.startsWith("ptt")) {
    sendPushToTalk();
    if (voiceStatus) ui({ ...voiceStatus, ptt: config.pttKey ? config.pttKeyName || "on" : "" });
  }
  // The listener restarts with the new phrases.
  if (name === "shortEmotes" && voice) voice.kill();
  sendReady();
}

if (UI) {
  let pendingInput = "";
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (text) => {
    const lines = (pendingInput + text).split(/\r?\n/);
    pendingInput = lines.pop();
    for (const line of lines.map((l) => l.trim())) {
      if (line === "quit") { shutdown(); return; }
      const set = /^set (\w+) (.+)$/.exec(line);
      if (set) applySetting(set[1], set[2].trim());
    }
  });
  process.stdin.on("end", shutdown);
}

// Rewrites one setting in config.json, keeping the rest of the file as the player wrote it.
function saveSetting(key, value) {
  const saved = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8"));
  saved[key] = value;
  fs.writeFileSync(CONFIG_PATH, JSON.stringify(saved, null, 2) + "\n");
}
