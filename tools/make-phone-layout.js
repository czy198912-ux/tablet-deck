// Builds layouts/deck-phone.json (iPhone landscape) from layouts/deck.json.
// Button definitions (keys, scAction, anim, colours) are copied from the tablet layout
// by id, so only positions live here. Re-run after changing the tablet layout:
//   node tools/make-phone-layout.js
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..");
const tablet = JSON.parse(fs.readFileSync(path.join(ROOT, "layouts/deck.json"), "utf8"));

// 734x372 = iPhone 15 Pro landscape (852x393 pt) minus the Dynamic Island insets
// (59 pt each side) and the home indicator (21 pt). Shown at 1:1 from the home screen.
const W = 734, H = 372;
const REGION = { y: 50, w: 359, h: 322, pager: 36 };
const BODY_H = REGION.h - REGION.pager - 8; // 278

// Index every element with an id (and menus by label) from the tablet layout.
const byId = new Map();
const menus = new Map();
(function scan(node) {
  if (Array.isArray(node)) { node.forEach(scan); return; }
  if (!node || typeof node !== "object") return;
  if (typeof node.id === "string") byId.set(node.id, node);
  if (node.type === "menu") menus.set(node.label, node);
  for (const v of Object.values(node)) if (v && typeof v === "object") scan(v);
})(tablet);

// Copy a tablet element by id and place it at a new box.
function at(id, x, y, w, h, extra = {}) {
  const src = byId.get(id);
  if (!src) throw new Error(`No element "${id}" in deck.json`);
  const copy = JSON.parse(JSON.stringify(src));
  // Grid buttons have no "type"; placed on their own they must be explicit buttons.
  return { type: "button", ...copy, x, y, w, h, ...extra };
}
const el = (obj) => obj;

// Re-flow a tablet grid panel into the phone body.
function gridPanel(id, cols, title) {
  const src = tablet.panels[id];
  const grid = src.elements.find((e) => e.type === "grid");
  return {
    title: title || src.title,
    elements: [{ type: "grid", x: 0, y: 0, w: REGION.w, h: BODY_H, cols, compact: true, buttons: grid.buttons }],
  };
}

// ---------- chrome ----------
const top = (id, i) => at(id, 440 + i * 50, 0, 44, 36);
const chrome = [
  { type: "action", action: "menu", icon: "emblem", x: 0, y: 0, w: 46, h: 36 },
  { type: "status", x: 56, y: 12 },
  top("top-atc", 0), top("top-chat", 1), top("top-contacts", 2), top("top-starmap", 3), top("top-mobiglas", 4),
  { type: "action", action: "theme", icon: "sliders", x: 690, y: 0, w: 44, h: 36 },
  { type: "rule", x: 0, y: 42, w: W },
  { type: "divider", x: 367, y: 52, h: 318 },
];

// ---------- hand-placed panels ----------
const landing = ["approach", "landing"], docking = ["approach", "docking"];
const stateHeader = tablet.panels["power-landing"].elements.find((e) => e.type === "state-header");
const sw = tablet.panels["power-landing"].elements.find((e) => e.type === "switch");

const panels = {
  landing: {
    title: "Landing",
    elements: [
      { ...stateHeader, x: 2, y: 2 },
      { ...sw, x: 259, y: 0, w: 100, h: 24 },
      at("pl-gear", 0, 32, 175, 62, { when: landing }),
      at("pl-vtol", 184, 32, 175, 62, { when: landing }),
      at("pl-autoland", 0, 102, 359, 54, { when: landing }),
      at("pl-request-dock", 0, 32, 175, 62, { when: docking }),
      at("pl-dock-vtol", 184, 32, 175, 62, { when: docking }),
      at("pl-dock-cam", 0, 102, 359, 54, { when: docking }),
      at("fa-lights", 0, 166, 175, 52, { id: "ph-lights", style: "std" }),
      at("fa-exit", 184, 166, 175, 52, { id: "ph-exit", style: "std" }),
      at("fa-limiter", 0, 226, 175, 52, { id: "ph-limiter", style: "std" }),
      at("fa-look-behind", 184, 226, 175, 52, { id: "ph-look-behind", style: "std" }),
    ],
  },

  power: {
    title: "Power",
    elements: [
      el({ type: "vlabel", text: "Max", x: 0, y: 0, w: 24, h: 42 }),
      el({ type: "vlabel", text: "Incr", x: 0, y: 48, w: 24, h: 42 }),
      el({ type: "vlabel", text: "Decr", x: 0, y: 130, w: 24, h: 42 }),
      el({ type: "vlabel", text: "Min", x: 0, y: 178, w: 24, h: 42 }),
      ...["wep", "thr", "shd"].flatMap((s, i) => {
        const x = 30 + i * 110;
        return [
          at(`pw-${s}-max`, x, 0, 104, 42),
          at(`pw-${s}-up`, x, 48, 104, 42),
          at(`pw-${s}-down`, x, 130, 104, 42),
          at(`pw-${s}-min`, x, 178, 104, 42),
        ];
      }),
      el({ type: "caption", icon: "power-weapons", text: "Weapons", color: "red", x: 30, y: 94, w: 104 }),
      el({ type: "caption", icon: "power-engines", text: "Thrusters", color: "blue", x: 140, y: 94, w: 104 }),
      el({ type: "caption", icon: "power-shields", text: "Shields", color: "green", x: 250, y: 94, w: 104 }),
      at("pw-reset", 0, 230, 359, 44),
    ],
  },

  systems: {
    title: "QT/Systems",
    elements: [
      el({ type: "header", text: "Ship_systems", x: 2, y: 2 }),
      at("qt-pwr", 0, 20, 85, 52), at("qt-wep", 91, 20, 85, 52), at("qt-thr", 182, 20, 85, 52), at("qt-shld", 273, 20, 86, 52),
      at("qt-flight-ready", 0, 82, 359, 54),
      at("qt-scm-nav", 0, 144, 175, 46),
      { ...JSON.parse(JSON.stringify(menus.get("Oprt Mode"))), x: 184, y: 144, w: 175, h: 46 },
      at("qt-engage", 34, 200, 291, 74),
    ],
  },

  emergency: {
    title: "Emergency",
    elements: [
      el({ type: "header", text: "Emergency", x: 2, y: 2 }),
      el({ type: "danger-zone", x: 0, y: 22, w: 359, h: 92 }),
      at("qt-eject", 12, 40, 236, 56),
      at("qt-self-destruct", 258, 43, 90, 50),
      at("cb-decoy", 0, 126, 175, 64, { id: "ph-decoy" }),
      at("cb-noise", 184, 126, 175, 64, { id: "ph-noise" }),
      at("cb-panic", 0, 198, 359, 74, { id: "ph-panic", style: "std" }),
    ],
  },
};

// ---------- re-flowed grid panels ----------
Object.assign(panels, {
  "flight-assist": gridPanel("flight-assist", 3),
  shields: gridPanel("shields", 3),
  targets: gridPanel("targets", 2),
  salvage: gridPanel("salvage", 2),
  modes: gridPanel("modes", 3),
  combat: gridPanel("combat", 2),
  crew: gridPanel("crew", 2),
  "on-foot": gridPanel("on-foot", 2),
});
// 34 emotes don't fit one phone panel; split them over two.
const emotes = tablet.panels.emotes.elements.find((e) => e.type === "grid").buttons;
const half = Math.ceil(emotes.length / 2);
panels["emotes-1"] = { title: "Emotes 1", elements: [{ type: "grid", x: 0, y: 0, w: REGION.w, h: BODY_H, cols: 4, compact: true, buttons: emotes.slice(0, half) }] };
panels["emotes-2"] = { title: "Emotes 2", elements: [{ type: "grid", x: 0, y: 0, w: REGION.w, h: BODY_H, cols: 4, compact: true, buttons: emotes.slice(half) }] };

const phone = {
  name: "Tablet Deck HUD (iPhone)",
  notes: "Generated by tools/make-phone-layout.js from deck.json; edit that script or the tablet layout, then re-run it. Canvas 734x372 = iPhone 15 Pro landscape safe area.",
  canvas: [W, H],
  chrome,
  regions: [
    { id: "left", x: 0, y: REGION.y, w: REGION.w, h: REGION.h, pager: REGION.pager,
      panels: ["landing", "power", "flight-assist", "shields", "targets", "salvage"] },
    { id: "right", x: 375, y: REGION.y, w: REGION.w, h: REGION.h, pager: REGION.pager,
      panels: ["systems", "emergency", "modes", "combat", "crew", "on-foot", "emotes-1", "emotes-2"] },
  ],
  panels,
};

// Every button id must be unique within a layout.
const seen = new Set();
(function check(node) {
  if (Array.isArray(node)) { node.forEach(check); return; }
  if (!node || typeof node !== "object") return;
  if (typeof node.id === "string" && typeof node.keys === "string") {
    if (seen.has(node.id)) throw new Error(`Duplicate id "${node.id}"`);
    seen.add(node.id);
  }
  for (const v of Object.values(node)) if (v && typeof v === "object") check(v);
})(phone);

const out = path.join(ROOT, "layouts/deck-phone.json");
fs.writeFileSync(out, JSON.stringify(phone, null, 2) + "\n");
console.log(`Wrote ${path.relative(ROOT, out)}: ${seen.size} buttons, ${Object.keys(panels).length} panels.`);
