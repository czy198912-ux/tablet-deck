// Builds the "Tablet Deck Keyboard" Star Citizen control profile from the layouts, so
// every button with an scAction has a keyboard bind without any third-party profile.
//
//   node tools/make-profile.js           write keybinds/TabletDeck-StarCitizen.xml
//   node tools/make-profile.js --check   exit 1 if that file is missing or out of date
//
// Each button's `keys` becomes the bind for its `scAction`. Star Citizen looks actions
// up per actionmap, so every action must be listed in ACTIONMAPS below; an unknown
// action stops the build instead of being guessed into the wrong map.
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..");
const OUT = path.join(ROOT, "keybinds", "TabletDeck-StarCitizen.xml");
const PROFILE_NAME = "Tablet Deck Keyboard";

// Star Citizen actionmap(s) for each action the layouts use. Some actions exist in more
// than one map (doors work in ships and ground vehicles) and are bound in each.
const ACTIONMAPS = {
  // On foot
  drop: ["player"],
  inspect: ["player"],
  walk: ["player"],
  ...Object.fromEntries([
    "agree", "angry", "atease", "attention", "blah", "bored", "bow", "burp", "cheer", "chicken",
    "clap", "come", "cry", "dance", "disagree", "failure", "flex", "flirt", "gasp", "gloat",
    "greet", "laugh", "launch", "point", "rude", "salute", "sit", "sleep", "smell", "taunt",
    "threaten", "wait", "wave", "whistle",
  ].map((e) => [`emote_${e}`, ["player_emotes"]])),

  // Seat (operator modes, remote turrets)
  v_enter_remote_turret_1: ["seat_general"],
  v_enter_remote_turret_2: ["seat_general"],
  v_enter_remote_turret_3: ["seat_general"],
  v_set_flight_mode: ["seat_general"],
  v_set_guns_mode: ["seat_general"],
  v_set_mining_mode: ["seat_general"],
  v_set_missile_mode: ["seat_general"],
  v_set_quantum_mode: ["seat_general"],
  v_set_salvage_mode: ["seat_general"],
  v_set_scan_mode: ["seat_general"],

  // Doors and views
  v_close_all_doors: ["spaceship_general", "vehicle_general"],
  v_lock_all_doors: ["spaceship_general", "vehicle_general"],
  v_open_all_doors: ["spaceship_general", "vehicle_general"],
  v_unlock_all_doors: ["spaceship_general", "vehicle_general"],
  v_toggle_all_doors: ["spaceship_general"],
  v_view_look_behind: ["vehicle_general"],

  // Flight
  v_ifcs_speed_limiter_reset_scm: ["spaceship_movement"],
  v_ifcs_toggle_esp: ["spaceship_movement"],
  v_ifcs_toggle_gforce_safety: ["spaceship_movement"],
  v_ifcs_toggle_speed_limiter: ["spaceship_movement"],
  v_master_mode_set_nav: ["spaceship_movement"],
  v_master_mode_set_scm: ["spaceship_movement"],
  v_speed_range_down: ["spaceship_movement"],
  v_speed_range_up: ["spaceship_movement"],
  v_target_match_vel: ["spaceship_movement"],

  // Shields and countermeasures
  v_shield_raise_level_back: ["spaceship_defensive"],
  v_shield_raise_level_down: ["spaceship_defensive"],
  v_shield_raise_level_forward: ["spaceship_defensive"],
  v_shield_raise_level_left: ["spaceship_defensive"],
  v_shield_raise_level_right: ["spaceship_defensive"],
  v_shield_raise_level_up: ["spaceship_defensive"],
  v_shield_reset_level: ["spaceship_defensive"],
  v_weapon_countermeasure_decoy_launch_panic: ["spaceship_defensive"],

  // Weapons and missiles
  v_weapon_pip_toggle_lead_lag: ["spaceship_weapons"],
  v_weapon_decrease_max_missiles: ["spaceship_missiles"],
  v_weapon_launch_missile_cinematic: ["spaceship_missiles"],

  // Targeting
  v_target_cycle_all_back: ["spaceship_targeting_advanced"],
  v_target_cycle_attacker_back: ["spaceship_targeting_advanced"],
  v_target_cycle_friendly_back: ["spaceship_targeting_advanced"],
  v_target_cycle_hostile_back: ["spaceship_targeting_advanced"],
  v_target_cycle_in_view_back: ["spaceship_targeting_advanced"],
  v_target_cycle_subitem_back: ["spaceship_targeting_advanced"],

  // Salvage
  v_salvage_cycle_modifiers_left: ["spaceship_salvage"],
  v_salvage_cycle_modifiers_right: ["spaceship_salvage"],
  v_salvage_decrease_beam_spacing: ["spaceship_salvage"],
  v_salvage_increase_beam_spacing: ["spaceship_salvage"],

  // Mining
  v_decrease_mining_throttle: ["spaceship_mining"],
  v_increase_mining_throttle: ["spaceship_mining"],
  v_mining_use_consumable1: ["spaceship_mining"],
  v_mining_use_consumable2: ["spaceship_mining"],
  v_mining_use_consumable3: ["spaceship_mining"],
  v_toggle_mining_laser_fire: ["spaceship_mining"],
  v_toggle_mining_laser_type: ["spaceship_mining"],
};

// Maps that are active together. A key combo bound to two actions in the same context
// would fire both in-game, so that fails the build; across contexts it is only reported.
const CONTEXTS = {
  player: "on foot",
  player_emotes: "on foot",
  seat_general: "vehicle",
  spaceship_general: "vehicle",
  spaceship_movement: "vehicle",
  spaceship_defensive: "vehicle",
  spaceship_weapons: "vehicle",
  spaceship_missiles: "vehicle",
  spaceship_targeting_advanced: "vehicle",
  spaceship_salvage: "vehicle",
  spaceship_mining: "vehicle",
  vehicle_general: "vehicle",
};

// Star Citizen keyboard key names (the same names server.js sends).
const KEY_NAMES = new Set([
  ..."abcdefghijklmnopqrstuvwxyz0123456789",
  ...Array.from({ length: 24 }, (_, i) => `f${i + 1}`),
  ...Array.from({ length: 10 }, (_, i) => `np_${i}`),
  "np_period", "np_add", "np_subtract", "np_multiply", "np_divide", "np_enter",
  "lalt", "ralt", "lctrl", "rctrl", "lshift", "rshift",
  "space", "tab", "enter", "escape", "backspace", "insert", "delete", "home", "end", "pgup", "pgdn",
  "up", "down", "left", "right", "minus", "equals", "lbracket", "rbracket", "semicolon", "apostrophe",
  "grave", "backslash", "comma", "period", "slash", "capslock", "numlock", "scrolllock", "print",
]);

function fail(lines) {
  console.error([].concat(lines).join("\n"));
  process.exit(1);
}

// Returns { action: { keys, from: ["layout:button", ...] } } for every button with an scAction.
function collectBinds() {
  const binds = {};
  const errors = [];
  const dir = path.join(ROOT, "layouts");
  for (const file of fs.readdirSync(dir).filter((f) => f.endsWith(".json")).sort()) {
    const layout = JSON.parse(fs.readFileSync(path.join(dir, file), "utf8"));
    (function walk(node) {
      if (Array.isArray(node)) { node.forEach(walk); return; }
      if (!node || typeof node !== "object") return;
      if (typeof node.id === "string" && typeof node.keys === "string" && node.scAction) {
        const where = `${file}:${node.id}`;
        const keys = node.keys.trim().toLowerCase();
        const bad = keys.split("+").filter((k) => !KEY_NAMES.has(k));
        if (!keys || bad.length) errors.push(`${where}: unknown key "${bad.join("+") || keys}" in "${node.keys}"`);
        const bind = binds[node.scAction];
        if (!bind) binds[node.scAction] = { keys, from: [where] };
        else if (bind.keys !== keys) errors.push(`${where}: ${node.scAction} is "${keys}" here but "${bind.keys}" in ${bind.from[0]}`);
        else bind.from.push(where);
      }
      for (const v of Object.values(node)) if (v && typeof v === "object") walk(v);
    })(layout);
  }
  const unknown = Object.keys(binds).filter((a) => !ACTIONMAPS[a]);
  for (const a of unknown) {
    errors.push(`${binds[a].from[0]}: no actionmap known for "${a}". Add it to ACTIONMAPS in tools/make-profile.js ` +
      `(find the map in StarCitizen\\LIVE\\user\\client\\0\\Profiles\\default\\actionmaps.xml after rebinding it in-game).`);
  }
  if (errors.length) fail(["Can't build the keybind profile:", ...errors.map((e) => `  ${e}`)]);
  return binds;
}

// Reports combos shared by different actions; returns the ones that clash in one context.
function findConflicts(binds) {
  const byCombo = {};
  for (const [action, { keys }] of Object.entries(binds)) {
    // Modifier order doesn't matter to the game: lalt+n == n+lalt.
    const combo = keys.split("+").sort().join("+");
    (byCombo[combo] = byCombo[combo] || []).push(action);
  }
  const clashes = [];
  for (const [combo, actions] of Object.entries(byCombo)) {
    if (actions.length < 2) continue;
    const contexts = actions.map((a) => new Set(ACTIONMAPS[a].map((m) => CONTEXTS[m] || m)));
    const shared = contexts.some((c, i) => contexts.slice(i + 1).some((d) => [...c].some((x) => d.has(x))));
    const keys = binds[actions[0]].keys;
    if (shared) clashes.push(`  ${keys}: ${actions.join(", ")}`);
    else console.warn(`Note: ${keys} is used by ${actions.join(", ")} (different contexts, so no clash).`);
  }
  return clashes;
}

function buildXml(binds) {
  const maps = {};
  for (const [action, { keys }] of Object.entries(binds)) {
    for (const map of ACTIONMAPS[action]) (maps[map] = maps[map] || []).push([action, keys]);
  }
  const lines = [
    `<ActionMaps version="1" optionsVersion="2" rebindVersion="2" profileName="${PROFILE_NAME}">`,
    ` <CustomisationUIHeader label="${PROFILE_NAME}" description="" image="">`,
    "  <devices>",
    '   <keyboard instance="1"/>',
    "  </devices>",
    "  <categories />",
    " </CustomisationUIHeader>",
    " <modifiers />",
  ];
  for (const map of Object.keys(maps).sort()) {
    lines.push(` <actionmap name="${map}">`);
    for (const [action, keys] of maps[map].sort((a, b) => a[0].localeCompare(b[0]))) {
      lines.push(`  <action name="${action}">`, `   <rebind input="kb1_${keys}"/>`, "  </action>");
    }
    lines.push(" </actionmap>");
  }
  lines.push("</ActionMaps>");
  return lines.join("\r\n") + "\r\n";
}

const binds = collectBinds();
const clashes = findConflicts(binds);
if (clashes.length) fail(["Key conflicts (these would fire together in-game):", ...clashes]);
const xml = buildXml(binds);
const rel = path.relative(ROOT, OUT).replace(/\\/g, "/");
const count = Object.keys(binds).length;

if (process.argv.includes("--check")) {
  // Ignore line endings: git or a ZIP download may have turned CRLF into LF.
  const lf = (s) => s.replace(/\r\n/g, "\n");
  const current = fs.existsSync(OUT) ? fs.readFileSync(OUT, "utf8") : null;
  if (current === null || lf(current) !== lf(xml)) fail(`${rel} is out of date; run: node tools/make-profile.js`);
  console.log(`${rel} is up to date (${count} actions).`);
} else {
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, xml);
  console.log(`Wrote ${rel}: "${PROFILE_NAME}", ${count} actions.`);
}
