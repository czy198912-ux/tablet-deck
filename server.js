// Tablet Deck: serve a touch button page to a tablet on the LAN and turn button
// presses into keyboard scancodes on this PC. No dependencies beyond Node itself.
//
//   node server.js            normal mode
//   node server.js --dry-run  log key events instead of sending them
"use strict";
const http = require("http");
const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");
const { spawn, execFileSync } = require("child_process");
const qr = require("./qr");

const ROOT = __dirname;
const DRY_RUN = process.argv.includes("--dry-run");
// --port <n> overrides config.json, e.g. to run a dry-run copy next to the real one.
const portArg = process.argv.indexOf("--port");
const PORT_OVERRIDE = portArg > -1 ? Number(process.argv[portArg + 1]) : null;
const CONFIG_PATH = path.join(ROOT, "config.json");

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
  // The default layouts were renamed from deck*.json; move older configs along.
  const renamed = { "layouts/deck.json": "layouts/hud.json", "layouts/deck-phone.json": "layouts/hud-phone.json" };
  for (const key of ["layout", "phoneLayout"]) {
    if (renamed[config[key]]) { config[key] = renamed[config[key]]; changed = true; }
  }
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
  if (changed) fs.writeFileSync(CONFIG_PATH, JSON.stringify(config, null, 2) + "\n");
  // Allow a numeric code such as "token": 1234 without quotes; the page sends the code as lowercase text.
  config.token = String(config.token).trim().toLowerCase();
  return config;
}
const config = loadConfig();
if (PORT_OVERRIDE) config.port = PORT_OVERRIDE;

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
    const full = path.resolve(ROOT, file);
    if (!fs.existsSync(full)) continue;
    const xml = fs.readFileSync(full, "utf8");
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
function layoutFile(device) {
  if (device === "phone" && config.phoneLayout && fs.existsSync(path.join(ROOT, config.phoneLayout))) return config.phoneLayout;
  return config.layout;
}

function loadLayout(device) {
  const layout = JSON.parse(fs.readFileSync(path.join(ROOT, layoutFile(device)), "utf8"));
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
  return { layout, buttons };
}

// ---------- key sender ----------

// Process name of the game (StarCitizen.exe). With onlyWhenGameFocused, presses are
// skipped unless this process owns the window in front.
const GAME_PROCESS = "StarCitizen";

// Builds native/<name>.exe from <name>.cs with the C# compiler that ships with Windows.
// Rebuilds when the .cs is newer, so an update's changes reach an existing install.
function buildNative(name, what, refs = []) {
  const exe = path.join(ROOT, "native", `${name}.exe`);
  const src = path.join(ROOT, "native", `${name}.cs`);
  if (!fs.existsSync(exe) || fs.statSync(exe).mtimeMs < fs.statSync(src).mtimeMs) {
    const csc = path.join(process.env.WINDIR || "C:\\Windows", "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
    console.log(`Compiling ${what}...`);
    execFileSync(csc, ["/nologo", "/optimize", "/target:exe", "/platform:x64", ...refs.map((r) => `/r:${r}`), `/out:${exe}`, src], { stdio: "inherit" });
  }
  return exe;
}

let sender = null;
const focusWaiters = []; // pending "focus" queries, answered in order by the key sender
function startSender() {
  if (DRY_RUN) return;
  sender = spawn(buildNative("keysender", "key sender"), [], { stdio: ["pipe", "pipe", "inherit"] });
  let pending = "";
  sender.stdout.on("data", (d) => {
    const lines = (pending + d.toString()).split(/\r?\n/);
    pending = lines.pop();
    for (const line of lines) {
      if (line.startsWith("err")) console.error("key sender:", line);
      else if (line.startsWith("focus ")) focusWaiters.shift()?.(line === "focus 1");
    }
  });
  sender.on("exit", (code) => {
    console.error(`Key sender exited (${code}); restarting.`);
    for (const answer of focusWaiters.splice(0)) answer(false);
    sender = null;
    setTimeout(startSender, 500);
  });
}

// Resolves true when keys may be sent: the check is off, it's a dry run, or the game is in front.
function gameFocused() {
  if (!config.onlyWhenGameFocused || !sender) return Promise.resolve(true);
  return new Promise((resolve) => {
    let done = false;
    const answer = (focused) => { if (!done) { done = true; resolve(focused); } };
    focusWaiters.push(answer);
    sender.stdin.write(`focus ${GAME_PROCESS}\n`);
    // An unanswered query counts as not focused; its late reply still shifts it off the queue.
    setTimeout(() => answer(false), 1000);
  });
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

// Presses a button's keys for `ms`, then releases them, unless the game isn't in front.
async function tapButton(btn, client, ms = btn.ms || config.tapMs) {
  if (!(await gameFocused())) { skipped(btn, client); return; }
  console.log(`${btn.label} (${btn.keys}) tap`);
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
function voiceCommands(buttons) {
  const byPhrase = new Map(); // phrase -> button, or null when ambiguous
  for (const btn of buttons.values()) {
    if (btn.voice === false) continue;
    for (const phrase of [btn.label, ...(btn.voice || [])].map(spoken).filter(Boolean)) {
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
let voiceOwner = null; // the tablet holding the talk button; it gets the results
function startVoice() {
  if (!config.voice) return;
  let exe;
  try { exe = buildNative("voice", "voice commands", ["System.Speech.dll"]); } catch (err) {
    console.error(`Voice commands unavailable: native/voice.cs didn't compile (${err.message}).`);
    return;
  }
  const commands = voiceCommands(loadLayout().buttons);
  const proc = spawn(exe, [config.voiceLanguage], { stdio: ["pipe", "pipe", "inherit"] });
  let pending = "";
  proc.stdout.on("data", (d) => {
    const lines = (pending + d.toString()).split(/\r?\n/);
    pending = lines.pop();
    for (const line of lines) {
      if (line.startsWith("ready")) {
        voice = proc;
        console.log(`Voice commands ready (${commands.size} phrases, ${line.slice(6)}). Hold the mic button on the tablet and speak.`);
      } else if (line.startsWith("err")) {
        console.error(`Voice commands unavailable: ${line.slice(4)}. See "Voice commands" in README.md.`);
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
    if (wasReady) setTimeout(startVoice, 2000);
  });
  for (const [phrase, id] of commands) proc.stdin.write(`phrase ${id} ${phrase}\n`);
  proc.stdin.write("ready\n");
}

// Presses the button for a recognised phrase. The layout is re-read so in-game rebinds count.
async function onVoice(id, confidence, text) {
  const client = voiceOwner;
  const btn = id ? loadLayout().buttons.get(id) : null;
  if (!btn || confidence < config.voiceConfidence) {
    console.log(btn ? `Voice: "${text}" ignored (confidence ${confidence.toFixed(2)})` : "Voice: didn't catch that");
    client?.send({ t: "unheard" });
    return;
  }
  console.log(`Voice: "${text}" -> ${btn.label} (confidence ${confidence.toFixed(2)})`);
  client?.send({ t: "heard", id: btn.id, label: btn.label, text });
  // Hold buttons (quantum engage, laser power) get a longer press, set per button.
  await tapButton(btn, client, btn.voiceMs || btn.ms || config.tapMs);
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

const server = http.createServer((req, res) => {
  const url = new URL(req.url, "http://x");
  if (url.pathname === "/layout.json") {
    if (url.searchParams.get("t") !== config.token) { res.writeHead(403); res.end("bad token"); return; }
    try {
      const { layout } = loadLayout(url.searchParams.get("device"));
      res.writeHead(200, { "Content-Type": MIME[".json"], "Cache-Control": "no-store" });
      res.end(JSON.stringify(layout, (k, v) => (k === "scancodes" ? undefined : v)));
    } catch (err) {
      res.writeHead(500); res.end(String(err.message));
    }
    return;
  }
  const rel = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
  const file = path.join(ROOT, "public", rel);
  if (!file.startsWith(path.join(ROOT, "public")) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404); res.end("not found"); return;
  }
  res.writeHead(200, { "Content-Type": MIME[path.extname(file)] || "application/octet-stream" });
  fs.createReadStream(file).pipe(res);
});

server.on("upgrade", (req, socket) => {
  const url = new URL(req.url, "http://x");
  if (url.pathname !== "/ws" || url.searchParams.get("t") !== config.token) {
    socket.end("HTTP/1.1 403 Forbidden\r\n\r\n"); return;
  }
  let buttons;
  try { ({ buttons } = loadLayout(url.searchParams.get("device"))); } catch (err) {
    console.error("Layout error:", err.message); socket.end("HTTP/1.1 500 Layout Error\r\n\r\n"); return;
  }
  wsAccept(req, socket);
  const who = socket.remoteAddress;
  console.log(`Tablet connected: ${who}`);
  const client = { send: (obj) => { if (!socket.destroyed) wsSend(socket, JSON.stringify(obj)); } };
  client.send({ t: "hello", voice: Boolean(config.voice) });

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
      if (!voice) {
        client.send({ t: "voice-off", reason: config.voice ? "Voice commands aren't running on the PC" : "Voice commands are turned off" });
      } else if (msg.on) {
        voiceOwner = client;
        voice.stdin.write("listen\n");
      } else if (voiceOwner === client) {
        voice.stdin.write("stop\n");
      }
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
      await pressCombo(btn.scancodes);
    } else if (msg.t === "up" && held.has(btn.id)) {
      // Releases always go through, even after the game loses focus, so no key stays stuck.
      const { sc } = held.get(btn.id);
      held.delete(btn.id);
      console.log(`${btn.label} up`);
      await releaseCombo(sc);
    } else if (msg.t === "tap") {
      await tapButton(btn, client);
    }
  }, () => {
    clearInterval(watchdog);
    void releaseAll();
    if (voiceOwner === client) { voice?.stdin.write("stop\n"); voiceOwner = null; }
    console.log(`Tablet disconnected: ${who}`);
  });
});

// ---------- start ----------

try { loadLayout(); loadLayout("phone"); } catch (err) { console.error("Layout error:", err.message); process.exit(1); }
startSender();
startVoice();
server.listen(config.port, "0.0.0.0", () => {
  const ips = Object.values(os.networkInterfaces()).flat()
    .filter((i) => i && i.family === "IPv4" && !i.internal && !i.address.startsWith("169.254"))
    .map((i) => i.address);
  console.log(`\nTablet Deck running${DRY_RUN ? " (DRY RUN: no keys are sent)" : ""}.`);
  console.log("Open this on your tablet (same Wi-Fi/network):");
  for (const ip of ips) console.log(`   http://${ip}:${config.port}/?t=${config.token}`);
  // Home networks are usually 192.168.x.x or 10.x.x.x; VPN and virtual adapters come later.
  const lan = ips.find((ip) => ip.startsWith("192.168.")) || ips.find((ip) => ip.startsWith("10.")) || ips[0];
  if (lan) {
    console.log(`\nOr scan this with the tablet's camera (${lan}):\n`);
    console.log(qr.toTerminal(`http://${lan}:${config.port}/?t=${config.token}`, { color: process.stdout.isTTY }));
  }
  console.log(`\nPairing code: ${config.token}`);
  console.log(`(You can also open http://${ips[0] || "<this PC's IP>"}:${config.port} and type the code.)`);
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
