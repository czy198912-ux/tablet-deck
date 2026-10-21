// Records docs/demo.png, the animated demo at the top of the README: a short scripted run
// through the deck (intro, a few presses with their animations, paging, a theme change).
//
//   node server.js --dry-run --port 8790        (in another window; dry run presses nothing)
//   node tools/record-demo.js http://localhost:8790/?t=<pairing code>
//
// It drives Microsoft Edge (or Chrome) without a window through its DevTools protocol,
// taps with real mouse events so the deck's own animations play, and records the screen.
// The frames become an animated PNG (APNG): full colour, it plays in browsers and on
// GitHub like a GIF, and only the part of each frame that changed is stored.
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const zlib = require("zlib");
const { spawn } = require("child_process");

const ROOT = path.join(__dirname, "..");
const OUT = path.join(ROOT, "docs", "demo.png");
const SIZE = { width: 1024, height: 768 };   // the page, as a tablet sees it
const FRAME = { width: 800, height: 600 };   // the recording
const FPS = 12;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const deckUrl = process.argv[2];
if (!deckUrl || !/^http:\/\/(localhost|127\.0\.0\.1):\d+\/\?t=/.test(deckUrl)) {
  console.error("Usage: node tools/record-demo.js http://localhost:<port>/?t=<pairing code>  (run a --dry-run server first)");
  process.exit(1);
}

// ---------- browser ----------

function findBrowser() {
  const candidates = [
    process.env.BROWSER,
    path.join(process.env["ProgramFiles(x86)"] || "", "Microsoft", "Edge", "Application", "msedge.exe"),
    path.join(process.env.ProgramFiles || "", "Microsoft", "Edge", "Application", "msedge.exe"),
    path.join(process.env.ProgramFiles || "", "Google", "Chrome", "Application", "chrome.exe"),
  ];
  const found = candidates.find((p) => p && fs.existsSync(p));
  if (!found) throw new Error("Microsoft Edge or Google Chrome is needed (or set BROWSER to one).");
  return found;
}

// Starts the browser without a window and connects to its first tab.
async function openBrowser() {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "tabletdeck-demo-"));
  const proc = spawn(findBrowser(), [
    "--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`,
    `--window-size=${SIZE.width},${SIZE.height}`, "--hide-scrollbars", "--mute-audio",
    "--no-first-run", "--no-default-browser-check", "about:blank",
  ], { stdio: "ignore" });
  // The browser writes its debugging port to this file once it's listening.
  const portFile = path.join(profile, "DevToolsActivePort");
  for (let i = 0; i < 100 && !fs.existsSync(portFile); i++) await sleep(100);
  const port = fs.readFileSync(portFile, "utf8").split("\n")[0].trim();
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = targets.find((t) => t.type === "page");
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let nextId = 1;
  const pending = new Map();
  const listeners = new Map();
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result);
    } else if (msg.method && listeners.has(msg.method)) {
      listeners.get(msg.method)(msg.params);
    }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const close = () => {
    ws.close();
    proc.kill();
    setTimeout(() => fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5 }), 1000);
  };
  return { send, on: (method, fn) => listeners.set(method, fn), close };
}

// ---------- PNG in, APNG out ----------

const crc = (buf) => zlib.crc32(buf) >>> 0;
function chunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, "ascii");
  const tail = Buffer.alloc(4);
  tail.writeUInt32BE(crc(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, tail]);
}

// Decodes an 8-bit RGB or RGBA PNG (what the browser sends) into RGB pixels.
function decodePng(buf) {
  let pos = 8, width = 0, height = 0, channels = 0;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos), type = buf.toString("ascii", pos + 4, pos + 8), data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === "IHDR") {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      if (data[8] !== 8 || (data[9] !== 2 && data[9] !== 6) || data[12] !== 0) throw new Error("Unexpected PNG format");
      channels = data[9] === 6 ? 4 : 3;
    } else if (type === "IDAT") idat.push(data);
    pos += 12 + len;
  }
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels, out = Buffer.alloc(width * height * 3);
  let prev = Buffer.alloc(stride), cur = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
      let v = line[i];
      if (filter === 1) v += a;
      else if (filter === 2) v += b;
      else if (filter === 3) v += (a + b) >> 1;
      else if (filter === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      cur[i] = v & 255;
    }
    for (let x = 0; x < width; x++) cur.copy(out, (y * width + x) * 3, x * channels, x * channels + 3);
    [prev, cur] = [cur, prev];
  }
  return { width, height, pixels: out };
}

// Compressed RGB image data for a w x h area of `pixels` (full width W) at (x0, y0).
// Each row uses whichever PNG filter makes it smallest, which keeps the file small.
function encodeArea(pixels, W, x0, y0, w, h) {
  const stride = w * 3, rows = [];
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < h; y++) {
    const start = ((y0 + y) * W + x0) * 3, cur = pixels.subarray(start, start + stride);
    let best = null, bestScore = Infinity;
    for (let f = 0; f <= 4; f++) {
      const line = Buffer.alloc(stride + 1);
      line[0] = f;
      let score = 0;
      for (let i = 0; i < stride; i++) {
        const a = i >= 3 ? cur[i - 3] : 0, b = prev[i], c = i >= 3 ? prev[i - 3] : 0;
        let p = 0;
        if (f === 1) p = a; else if (f === 2) p = b; else if (f === 3) p = (a + b) >> 1;
        else if (f === 4) { const q = a + b - c, pa = Math.abs(q - a), pb = Math.abs(q - b), pc = Math.abs(q - c); p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
        const v = (cur[i] - p) & 255;
        line[i + 1] = v;
        score += v < 128 ? v : 256 - v;
      }
      if (score < bestScore) { best = line; bestScore = score; }
    }
    rows.push(best);
    prev = cur;
  }
  return zlib.deflateSync(Buffer.concat(rows), { level: 9 });
}

// frames: [{ pixels, delay (ms) }], all W x H. Writes an endlessly looping APNG.
function writeApng(file, W, H, frames) {
  const parts = [Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])];
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4);
  ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
  parts.push(chunk("IHDR", ihdr));
  const actl = Buffer.alloc(8);
  actl.writeUInt32BE(frames.length, 0); // plays: 0 = forever
  parts.push(chunk("acTL", actl));
  let seq = 0, before = null;
  frames.forEach((frame, i) => {
    // Only the box around what changed since the previous frame.
    let x0 = 0, y0 = 0, x1 = W - 1, y1 = H - 1;
    if (before) {
      x0 = W; y0 = H; x1 = -1; y1 = -1;
      for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
        const o = (y * W + x) * 3, p = frame.pixels;
        const d = Math.max(Math.abs(p[o] - before[o]), Math.abs(p[o + 1] - before[o + 1]), Math.abs(p[o + 2] - before[o + 2]));
        if (d === 0) continue;
        // The browser re-renders gradients with slightly different pixels each frame; keep
        // the previous pixel for those invisible changes so they don't count as changed.
        if (d <= 4) { p[o] = before[o]; p[o + 1] = before[o + 1]; p[o + 2] = before[o + 2]; continue; }
        if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
      }
      if (x1 < 0) { x0 = 0; y0 = 0; x1 = 0; y1 = 0; } // nothing changed: a 1-pixel frame holds the time
    }
    const w = x1 - x0 + 1, h = y1 - y0 + 1;
    const fctl = Buffer.alloc(26);
    fctl.writeUInt32BE(seq++, 0);
    fctl.writeUInt32BE(w, 4); fctl.writeUInt32BE(h, 8);
    fctl.writeUInt32BE(x0, 12); fctl.writeUInt32BE(y0, 16);
    fctl.writeUInt16BE(Math.min(65535, Math.round(frame.delay)), 20);
    fctl.writeUInt16BE(1000, 22); // delay in milliseconds
    fctl[24] = 0; fctl[25] = 0;   // keep the canvas; replace the area
    parts.push(chunk("fcTL", fctl));
    const data = encodeArea(frame.pixels, W, x0, y0, w, h);
    if (i === 0) parts.push(chunk("IDAT", data));
    else {
      const seqBuf = Buffer.alloc(4);
      seqBuf.writeUInt32BE(seq++, 0);
      parts.push(chunk("fdAT", Buffer.concat([seqBuf, data])));
    }
    before = frame.pixels;
  });
  parts.push(chunk("IEND", Buffer.alloc(0)));
  fs.writeFileSync(file, Buffer.concat(parts));
}

// ---------- the demo ----------

(async () => {
  const browser = await openBrowser();
  const { send } = browser;
  try {
    await send("Emulation.setDeviceMetricsOverride", { ...SIZE, deviceScaleFactor: 1, mobile: false });
    await send("Page.enable");
    const url = `${deckUrl}${deckUrl.includes("device=") ? "" : "&device=tablet"}`;
    await send("Page.navigate", { url });
    await sleep(2500); // fonts and the layout

    // Record every frame the browser paints, with its time.
    const shots = [];
    browser.on("Page.screencastFrame", (p) => {
      shots.push({ data: Buffer.from(p.data, "base64"), time: p.metadata.timestamp });
      send("Page.screencastFrameAck", { sessionId: p.sessionId }).catch(() => {});
    });
    const record = () => send("Page.startScreencast", { format: "png", maxWidth: FRAME.width, maxHeight: FRAME.height, everyNthFrame: 1 });

    const evaluate = async (expr) => (await send("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true })).result.value;
    // Centre of the first visible element matching a selector.
    const centre = (selector, index = 0) => evaluate(`(() => {
      const els = [...document.querySelectorAll(${JSON.stringify(selector)})].filter((e) => e.offsetParent !== null || e.getClientRects().length);
      const r = els[${index}]?.getBoundingClientRect();
      return r ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null;
    })()`);
    // A soft fingertip where the "finger" touches, so the viewer can follow the taps.
    const finger = (p, on) => evaluate(`(() => {
      let f = document.getElementById("demoFinger");
      if (!f) {
        f = document.createElement("div");
        f.id = "demoFinger";
        f.style.cssText = "position:fixed;z-index:99;width:44px;height:44px;margin:-22px 0 0 -22px;border-radius:50%;pointer-events:none;background:rgba(255,255,255,.28);border:2px solid rgba(255,255,255,.85);box-shadow:0 0 18px rgba(255,255,255,.5);transition:opacity .25s,transform .25s";
        document.body.appendChild(f);
      }
      f.style.left = "${p.x}px"; f.style.top = "${p.y}px";
      f.style.opacity = ${on ? 1 : 0}; f.style.transform = "scale(${on ? 1 : 1.4})";
    })()`);
    async function press(selector, holdMs = 140, index = 0) {
      const p = await centre(selector, index);
      if (!p) { console.warn(`Not found: ${selector}`); return; }
      await finger(p, true);
      await send("Input.dispatchMouseEvent", { type: "mouseMoved", x: p.x, y: p.y });
      await send("Input.dispatchMouseEvent", { type: "mousePressed", x: p.x, y: p.y, button: "left", clickCount: 1 });
      await sleep(holdMs);
      await send("Input.dispatchMouseEvent", { type: "mouseReleased", x: p.x, y: p.y, button: "left", clickCount: 1 });
      await finger(p, false);
    }

    // The intro's starfield changes every pixel of every frame, which would make the file
    // huge, so the recording starts in the deck.
    await sleep(1800);
    await press("#intro .intro-tap"); await sleep(2000);
    await finger({ x: -100, y: -100 }, false);
    await sleep(400);
    console.log("Recording...");
    await record();
    await sleep(700);
    await press('[aria-label="Landing Gear"]'); await sleep(1500);
    await press('[aria-label="VTOL"]'); await sleep(900);
    await press('[aria-label="Shields +"]'); await sleep(500);
    await press('[aria-label="Shields +"]'); await sleep(900);
    await press('[aria-label="Toggle Flight Ready"]'); await sleep(1200);
    await press('[aria-label="Engage Quantum"]', 1700); await sleep(1100);
    await press(".hud-pager .next", 120, 0); await sleep(1000);  // left half: next panel
    await press('[aria-label="Choose theme"]'); await sleep(900);
    await press('.card[data-theme="drake"]'); await sleep(1200);
    await press('[aria-label="SCM/NAV"]'); await sleep(600);
    await press('[aria-label="Toggle Flight Ready"]'); await sleep(1000);
    await press('[aria-label="Engage Quantum"]', 1500); await sleep(1800);
    await send("Page.stopScreencast");
    await sleep(300);

    // At most FPS frames a second, each shown until the next; the last holds before the loop.
    const thinned = [];
    for (const s of shots) if (!thinned.length || s.time - thinned[thinned.length - 1].time >= 1 / FPS) thinned.push(s);
    console.log(`Encoding ${thinned.length} of ${shots.length} frames...`);
    const frames = thinned.map((s, i) => ({
      pixels: decodePng(s.data).pixels,
      delay: i + 1 < thinned.length ? Math.max(20, (thinned[i + 1].time - s.time) * 1000) : 2000,
    }));
    const { width, height } = decodePng(shots[0].data);
    fs.mkdirSync(path.dirname(OUT), { recursive: true });
    writeApng(OUT, width, height, frames);
    console.log(`Wrote ${path.relative(ROOT, OUT)}: ${width}x${height}, ${frames.length} frames, ${(fs.statSync(OUT).size / 1048576).toFixed(1)} MB.`);
  } finally {
    browser.close();
  }
})().catch((err) => { console.error(err); process.exit(1); });
