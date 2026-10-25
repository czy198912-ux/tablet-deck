// Builds the starter layouts for different kinds of flying from the HUD layouts, by
// putting the panels for that kind of flying first, and the ship layouts, which start with
// a panel made for that ship. Run it after changing layouts/hud.json or
// layouts/hud-phone.json (tools/make-phone-layout.js), so they keep up.
//
//   node tools/make-starter-layouts.js
//
// Writes layouts/<id>.json and layouts/<id>-phone.json for each starter and ship below.
// The buttons, keys and voice phrases are the HUD's own (ship panels copy them under new
// ids), so the keybind profile is unchanged.
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..");
const read = (file) => JSON.parse(fs.readFileSync(path.join(ROOT, "layouts", file), "utf8"));

// The panels each half starts on, in order; the half's other panels follow.
const STARTERS = [
  {
    id: "combat", name: "Combat",
    tablet: [["power-landing", "shields", "targets", "flight-assist"], ["combat", "qt-systems", "modes", "crew"]],
    phone: [["landing", "power", "shields", "targets", "flight-assist"], ["combat", "systems", "emergency", "modes", "crew"]],
  },
  {
    id: "mining-salvage", name: "Mining & Salvage",
    tablet: [["mining", "salvage", "power-landing", "flight-assist"], ["modes", "qt-systems", "crew", "on-foot"]],
    phone: [["mining", "salvage", "landing", "power"], ["modes", "systems", "emergency", "crew", "on-foot"]],
  },
  {
    id: "hauling-exploration", name: "Hauling & Exploration",
    tablet: [["power-landing", "flight-assist", "targets"], ["qt-systems", "crew", "on-foot", "emotes"]],
    phone: [["landing", "power", "flight-assist", "targets"], ["systems", "emergency", "crew", "on-foot", "emotes-1", "emotes-2"]],
  },
];

// Ship layouts: a panel (two on phones) of that ship's controls, made from the HUD's own
// buttons, comes first; the starter's panels and then everything else follow. Each group
// is a header and a grid of button ids from layouts/hud.json.
const SHIPS = [
  {
    id: "prospector", name: "Prospector", prefix: "pr", like: "mining-salvage",
    tablet: [{ title: "Prospector", groups: [
      ["Laser", 2, ["mn-mode", "md-scan", "mn-fire", "mn-type", "mn-power-down", "mn-power-up"]],
      ["Modules", 3, ["mn-module-1", "mn-module-2", "mn-module-3"]],
      ["Ship", 2, ["qt-flight-ready", "pl-gear", "fa-lights", "md-flight", "qt-engage", "fa-exit"]],
    ] }],
    phone: [
      { title: "Prospector", groups: [
        ["Laser", 3, ["mn-mode", "mn-fire", "mn-type", "mn-power-down", "mn-power-up", "md-scan"]],
        ["Modules", 3, ["mn-module-1", "mn-module-2", "mn-module-3"]],
      ] },
      { title: "Prospector Flight", groups: [
        [null, 3, ["qt-flight-ready", "pl-gear", "fa-lights", "md-flight", "qt-engage", "fa-exit"]],
      ] },
    ],
  },
  {
    id: "vulture", name: "Vulture", prefix: "vu", like: "mining-salvage",
    tablet: [{ title: "Vulture", groups: [
      ["Salvage", 2, ["md-salvage", "md-scan", "sv-mod-left", "sv-mod-right", "sv-spacing-down", "sv-spacing-up"]],
      ["Ship", 2, ["qt-flight-ready", "pl-gear", "pl-vtol", "fa-lights", "qt-engage", "md-flight"]],
      ["Doors", 3, ["cr-doors-open", "cr-doors-close", "fa-exit"]],
    ] }],
    phone: [
      { title: "Vulture", groups: [
        ["Salvage", 3, ["md-salvage", "sv-mod-left", "sv-mod-right", "md-scan", "sv-spacing-down", "sv-spacing-up"]],
      ] },
      { title: "Vulture Flight", groups: [
        [null, 3, ["qt-flight-ready", "pl-gear", "pl-vtol", "fa-lights", "qt-engage", "md-flight", "cr-doors-open", "cr-doors-close", "fa-exit"]],
      ] },
    ],
  },
];

// Every panel stays: each half shows the starter's panels first, then the rest of that
// half's panels in the HUD's order, so nothing (emotes, on foot...) is ever out of reach.
// Panels in "extra" (ship panels) aren't the HUD's; they go first and are added.
function starter(base, name, halves, note, extra = {}) {
  const regions = base.regions.map((r, i) => {
    for (const id of halves[i]) if (!r.panels.includes(id) && !extra[id]) throw new Error(`Panel "${id}" isn't in the ${r.id} half of ${base.name}`);
    return { ...r, panels: [...halves[i], ...r.panels.filter((id) => !halves[i].includes(id))] };
  });
  return { name, notes: note, canvas: base.canvas, chrome: base.chrome, regions, panels: { ...extra, ...base.panels } };
}

// Every button in a layout by id (grid buttons included).
function buttonsById(layout) {
  const byId = new Map();
  (function scan(node) {
    if (Array.isArray(node)) { node.forEach(scan); return; }
    if (!node || typeof node !== "object") return;
    if (typeof node.id === "string" && typeof node.keys === "string") byId.set(node.id, node);
    for (const v of Object.values(node)) if (v && typeof v === "object") scan(v);
  })(layout.panels);
  return byId;
}

// A ship panel: headers and grids stacked down the region's body, rows sized to fit.
// Buttons are copies under "<prefix>-<id>", since a layout can't use an id twice; with the
// same keys they share voice phrases and lit states with the originals.
function shipPanel(spec, prefix, byId, width, bodyHeight, phone) {
  const HEADER = phone ? 20 : 24, GAP = phone ? 10 : 20;
  const groups = spec.groups.map(([header, cols, ids]) => ({ header, cols, rows: Math.ceil(ids.length / cols), ids }));
  const rows = groups.reduce((n, g) => n + g.rows, 0);
  const fixed = groups.reduce((n, g) => n + (g.header ? HEADER : 0), 0) + GAP * (groups.length - 1) + (phone ? 0 : 8);
  const rowHeight = Math.min(phone ? 60 : 100, Math.floor((bodyHeight - fixed) / rows));
  const elements = [];
  let y = phone ? 0 : 8;
  for (const g of groups) {
    if (g.header) { elements.push({ type: "header", text: g.header, x: 2, y }); y += HEADER; }
    const buttons = g.ids.map((id) => {
      const src = byId.get(id);
      if (!src) throw new Error(`No button "${id}" in the HUD layout`);
      const { type, x, y: _y, w, h, ...rest } = JSON.parse(JSON.stringify(src)); // the grid places it
      return { ...rest, id: `${prefix}-${id}` };
    });
    // Short rows put the icon beside the label (compact), or the label won't fit under it.
    const compact = phone || rowHeight < 96;
    elements.push({ type: "grid", x: 0, y, w: width, h: rowHeight * g.rows, cols: g.cols, ...(compact ? { compact: true } : {}), buttons });
    y += rowHeight * g.rows + GAP;
  }
  return { title: spec.title, elements };
}

const tablet = read("hud.json"), phone = read("hud-phone.json");
const tabletButtons = buttonsById(tablet);
// The space a panel gets: the region minus its pager.
const body = (layout) => ({ w: layout.regions[0].w, h: layout.regions[0].h - (layout.canvas[1] < 500 ? 44 : 52) });

const note = "Generated by tools/make-starter-layouts.js from the HUD layout; edit that script, not this file.";
const outputs = [];
for (const s of STARTERS) {
  outputs.push([`${s.id}.json`, starter(tablet, s.name, s.tablet, note)]);
  outputs.push([`${s.id}-phone.json`, starter(phone, `${s.name} (iPhone)`, s.phone, note)]);
}
for (const ship of SHIPS) {
  const like = STARTERS.find((s) => s.id === ship.like);
  for (const [base, specs, halves, suffix, label] of [
    [tablet, ship.tablet, like.tablet, "", ship.name],
    [phone, ship.phone, like.phone, "-phone", `${ship.name} (iPhone)`],
  ]) {
    const isPhone = base.canvas[1] < 500;
    const size = body(base);
    const extra = {};
    specs.forEach((spec, i) => {
      extra[`${ship.id}${i ? `-${i + 1}` : ""}`] = shipPanel(spec, ship.prefix, tabletButtons, size.w, size.h, isPhone);
    });
    const left = [...Object.keys(extra), ...halves[0]];
    outputs.push([`${ship.id}${suffix}.json`, starter(base, label, [left, halves[1]], note, extra)]);
  }
}
for (const [file, layout] of outputs) {
  fs.writeFileSync(path.join(ROOT, "layouts", file), JSON.stringify(layout, null, 2) + "\n");
  console.log(`Wrote layouts/${file}: ${layout.regions.map((r) => r.panels.length).join(" + ")} panels.`);
}
