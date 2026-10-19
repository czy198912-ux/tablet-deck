(() => {
  // Emblems are original badge designs on a 64x64 grid that suggest each maker's
  // character; they are not the manufacturers' real logos.
  // `F` = solid accent fill, `T` = group drawn in the theme's text colour.
  const F = `fill="currentColor" stroke="none"`;
  const T = (body) => `<g style="color:var(--text)">${body}</g>`;
  const THEMES = [
    { id: "rsi", name: "RSI", full: "Roberts Space Industries", tag: "Classic interstellar lines",
      // Orbital ascent: a ship rising through a planetary ring.
      emblem: `<circle cx="32" cy="32" r="27"/><circle cx="32" cy="32" r="20" opacity=".3"/>
        <path d="M32 8 40 34 32 28 24 34Z" ${F}/>
        <path d="M5 41c12-9 36-17 54-15" stroke-width="3.5"/>
        ${T(`<path d="M21 47h22"/><path d="M25 53h14" opacity=".6"/>`)}` },
    { id: "drake", name: "Drake", full: "Drake Interplanetary", tag: "Built rough, flown hard",
      // Talon: three claw slashes across a heavy hex nut.
      emblem: `<path d="M32 4 56 18v28L32 60 8 46V18Z" stroke-width="3.5"/>
        <path d="M32 12 49 22v20L32 52 15 42V22Z" opacity=".3"/>
        <path d="M19 15c9 8 12 18 10 33l-5-3c1-11-1-20-5-30Z" ${F}/>
        <path d="M29 13c9 9 12 19 10 35l-5-3c1-12-1-22-5-32Z" ${F}/>
        <path d="M40 17c6 7 9 16 7 27l-4-3c0-9-1-16-3-24Z" ${F} opacity=".75"/>` },
    { id: "aegis", name: "Aegis", full: "Aegis Dynamics", tag: "Military precision",
      // Winged spear: a central blade with swept delta wings.
      emblem: `<path d="M27 24 4 40h23Z" ${F} opacity=".55"/><path d="M37 24l23 16H37Z" ${F} opacity=".55"/>
        <path d="M27 31 12 44h15M37 31l15 13H37"/>
        <path d="M32 3 38 30 32 54 26 30Z" ${F}/>
        ${T(`<path d="M21 50h22M27 59h10"/>`)}` },
    { id: "anvil", name: "Anvil", full: "Anvil Aerospace", tag: "Frontline hardware",
      // Bulwark: a heavy shield with a command band and rank chevrons.
      emblem: `<path d="M32 4 56 12v18c0 14-10 24-24 30C18 54 8 44 8 30V12Z" stroke-width="3.5"/>
        <path d="M32 10 50 16v14c0 11-8 19-18 24-10-5-18-13-18-24V16Z" opacity=".3"/>
        <path d="M15 17h34v6H15Z" ${F}/>
        ${T(`<path d="M20 30l12 8 12-8M20 40l12 8 12-8" stroke-width="3.5"/>`)}` },
    { id: "origin", name: "Origin", full: "Origin Jumpworks", tag: "Luxury in the void",
      // Halo: a fine ring, a crescent and a single axis line.
      emblem: `<circle cx="32" cy="32" r="26" stroke-width="1.6"/>
        <path d="M40 10a22 22 0 1 0 0 44a30 30 0 0 1 0-44Z" ${F}/>
        <circle cx="45" cy="21" r="2.2" ${F}/>
        ${T(`<path d="M32 1v9M32 54v9" stroke-width="1.6"/>`)}` },
    { id: "misc", name: "MISC", full: "Musashi Industrial & Starflight", tag: "Industrial workhorses",
      // Cargo hex: a horizontal hexagon holding stacked cargo modules.
      emblem: `<path d="M18 8h28l14 24-14 24H18L4 32Z" stroke-width="3"/>
        <rect x="19" y="19" width="26" height="6" rx="1" ${F}/>
        <rect x="14" y="29" width="36" height="6" rx="1" ${F}/>
        <rect x="19" y="39" width="26" height="6" rx="1" ${F} opacity=".6"/>
        ${T(`<path d="M8 32h3M53 32h3"/>`)}` },
    { id: "crusader", name: "Crusader", full: "Crusader Industries", tag: "Open skies, big haulers",
      // Skysail: two wind-filled sails above a horizon.
      emblem: `<circle cx="32" cy="32" r="27"/>
        <path d="M19 46C23 28 35 16 51 12c-6 10-9 22-9 34Z" ${F}/>
        <path d="M13 46c3-10 8-17 15-22-2 7-3 14-3 22Z" ${F} opacity=".45"/>
        ${T(`<path d="M8 46h48"/><path d="M15 52h34" opacity=".4"/>`)}` },
    { id: "banu", name: "Banu", full: "Banu Souli", tag: "Alien craft, merchant spirit",
      // Bloom sigil: four petals and four buds around a seed, in a dotted ring.
      emblem: `<circle cx="32" cy="32" r="28" stroke-dasharray="1 5"/>
        ${[0, 90, 180, 270].map((r) => `<path d="M32 5c7 8 7 15 0 21-7-6-7-13 0-21Z" ${F} transform="rotate(${r} 32 32)"/>`).join("")}
        ${[45, 135, 225, 315].map((r) => `<path d="M32 12c4 5 4 9 0 13-4-4-4-8 0-13Z" ${F} opacity=".5" transform="rotate(${r} 32 32)"/>`).join("")}
        ${T(`<circle cx="32" cy="32" r="4.5"/>`)}` },
    { id: "aopoa", name: "Aopoa", full: "Aopoa", tag: "Xi'an elegance",
      // Twin crescents: two mirrored, flowing blades.
      emblem: `<path d="M12 52C9 31 22 13 45 8 32 18 27 31 29 52Z" ${F}/>
        <path d="M52 12c3 21-10 38-33 44 13-10 18-24 16-44Z" ${F} opacity=".5"/>
        ${T(`<circle cx="32" cy="32" r="2.6" fill="currentColor" stroke="none"/>`)}` },
    { id: "argo", name: "Argo", full: "Argo Astronautics", tag: "Utility first",
      // Liftframe: a load-bearing triangle with a crane hook and rivets.
      emblem: `<path d="M32 5 59 55H5Z" stroke-width="3.5"/><path d="M32 19 47 47H17Z" opacity=".3"/>
        <circle cx="14" cy="50" r="2.4" ${F}/><circle cx="50" cy="50" r="2.4" ${F}/>
        ${T(`<path d="M32 15v15a5 5 0 1 1-5 5" stroke-width="3.5"/>`)}` },
    { id: "cnou", name: "CO", full: "Consolidated Outland", tag: "Frontier newcomer",
      // Frontier sunrise: a bold open ring around a rising sun.
      emblem: `<path d="M48 14a24 24 0 1 0 0 36" stroke-width="6"/>
        <path d="M33 41a9 9 0 0 1 18 0Z" ${F}/>
        ${T(`<path d="M29 41h30"/><path d="M42 26v-4M32 30l-3-3M52 30l3-3" opacity=".7"/>`)}` },
    { id: "esperia", name: "Esperia", full: "Esperia", tag: "Reborn Tevarin designs",
      // Tevarin crescent: a sweeping blade under a four-point star.
      emblem: `<path d="M8 32A24 24 0 0 1 32 8" opacity=".4"/>
        <path d="M10 52C14 27 30 10 55 8 39 16 29 30 27 54Z" ${F}/>
        ${T(`<path d="M45 30l2.5 8 8 2.5-8 2.5-2.5 8-2.5-8-8-2.5 8-2.5Z" fill="currentColor" stroke="none"/>`)}` },
  ];
  const themeById = Object.fromEntries(THEMES.map((t) => [t.id, t]));
  const svg = (body) => `<svg viewBox="0 0 48 48" aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
  const emblemSvg = (body) => `<svg viewBox="0 0 64 64" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;

  const store = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch {} },
  };

  const $ = (id) => document.getElementById(id);
  const grid = $("grid");
  const tabs = $("tabs");
  let token = (new URLSearchParams(location.search).get("t") || store.get("token") || "").trim().toLowerCase();
  // Phones (short side under 500pt, e.g. iPhone 15 Pro at 393pt) get the phone layout;
  // ?device=tablet or ?device=phone in the address overrides the guess.
  const device = new URLSearchParams(location.search).get("device")
    || (Math.min(screen.width, screen.height) < 500 ? "phone" : "tablet");
  document.documentElement.dataset.device = device;
  let layout = null;
  let ws = null;
  let pageIndex = Number(store.get("page") || 0);

  // ---------- theme ----------

  function applyTheme(id) {
    const theme = themeById[id] || THEMES[0];
    document.documentElement.dataset.theme = theme.id;
    document.documentElement.dataset.maker = theme.full; // shown on the intro screen
    $("themeName").textContent = theme.name;
    for (const m of document.querySelectorAll("#brandMark, .js-emblem")) m.innerHTML = emblemSvg(theme.emblem);
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = getComputedStyle(document.documentElement).getPropertyValue("--bg").trim();
    store.set("theme", theme.id);
    for (const card of document.querySelectorAll(".card")) card.classList.toggle("current", card.dataset.theme === theme.id);
  }

  function buildPicker() {
    const cards = $("cards");
    cards.innerHTML = "";
    for (const t of THEMES) {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "card";
      card.dataset.theme = t.id;
      card.innerHTML = `
        <div class="top"><span class="emblem">${emblemSvg(t.emblem)}</span>
          <div><div class="name"></div><div class="tag"></div></div></div>
        <div class="key sample" data-color="">
          <span class="glyph">${window.deckIcon("quantum-mode")}</span><span class="label">Quantum Mode</span>
        </div>
        <div class="swatches"><i style="background:var(--accent)"></i><i style="background:var(--green)"></i><i style="background:var(--amber)"></i><i style="background:var(--red)"></i><i style="background:var(--blue)"></i></div>`;
      card.querySelector(".name").textContent = t.name;
      card.querySelector(".tag").textContent = `${t.full} · ${t.tag}`;
      card.onclick = () => { applyTheme(t.id); closePicker(); };
      cards.appendChild(card);
    }
  }
  const openPicker = () => { $("picker").classList.add("open"); applyTheme(document.documentElement.dataset.theme); };
  const closePicker = () => $("picker").classList.remove("open");
  $("themeBtn").onclick = openPicker;
  $("closePicker").onclick = closePicker;

  // ---------- connection ----------

  // Status shows in the header (grid layouts) and in canvas chrome, so update every copy.
  let online = { on: false, text: "Connecting…" };
  function setOnline(on, text) {
    online = { on, text };
    refreshStatus();
  }
  function refreshStatus() {
    document.body.classList.toggle("offline", !online.on);
    for (const d of document.querySelectorAll("#dot, .js-dot")) d.classList.toggle("on", online.on);
    for (const t of document.querySelectorAll("#statusText, .js-status")) t.textContent = online.text;
  }

  function send(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  // Short notice at the bottom of the screen, e.g. when the PC skipped a press.
  let toastTimer = null;
  function toast(text) {
    const el = $("toast");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove("show"), 1800);
  }

  function connect() {
    ws = new WebSocket(`ws://${location.host}/ws?t=${encodeURIComponent(token)}&device=${device}`);
    ws.onopen = () => setOnline(true, "Connected");
    ws.onmessage = (e) => {
      let msg;
      try { msg = JSON.parse(e.data); } catch { return; }
      if (msg.t === "blocked") { toast("Star Citizen not focused"); buzz([30, 60, 30]); }
      else if (msg.t === "hello") document.body.classList.toggle("no-voice", !msg.voice);
      else if (msg.t === "heard") toast(`Heard: ${msg.text || msg.label}`);
      else if (msg.t === "unheard") toast("Didn't catch that");
      else if (msg.t === "voice-off") toast(msg.reason);
    };
    ws.onclose = () => { setOnline(false, "Reconnecting…"); setTimeout(connect, 1000); };
    ws.onerror = () => ws.close();
  }
  setInterval(() => send({ t: "hb" }), 1500);

  // ---------- buttons ----------

  function renderTabs() {
    tabs.innerHTML = "";
    layout.pages.forEach((p, i) => {
      const b = document.createElement("button");
      b.type = "button";
      b.textContent = p.name;
      b.className = i === pageIndex ? "active" : "";
      b.onclick = () => { pageIndex = i; store.set("page", i); renderTabs(); renderPage(); };
      tabs.appendChild(b);
    });
  }

  const modeLabel = { tap: "", hold: "Hold", timed: "Long press" };
  const keyLabel = (keys) => keys.split("+").map((k) => k.replace(/^np_/, "num ").replace("period", ".")).join(" + ");

  function renderPage() {
    grid.innerHTML = "";
    for (const btn of layout.pages[pageIndex].buttons) {
      const el = document.createElement("button");
      el.type = "button";
      el.className = "key";
      el.dataset.color = btn.color || "";
      el.innerHTML = `<span class="glyph">${window.deckIcon(btn.icon || btn.id)}</span>
        <span class="label"></span>
        <span class="foot"><span class="mode"></span><span class="keycap"></span></span>`;
      el.querySelector(".label").textContent = btn.label;
      el.querySelector(".keycap").textContent = keyLabel(btn.keys);
      el.querySelector(".mode").textContent = modeLabel[btn.mode] || "";
      bindButton(el, btn);
      grid.appendChild(el);
    }
  }

  const buzz = (ms = 12) => { if (navigator.vibrate) navigator.vibrate(ms); };

  function bindButton(el, btn) {
    const fx = window.DeckFX.attach(el, btn);
    let pointer = null;
    el.addEventListener("pointerdown", (e) => {
      if (pointer !== null) return;
      pointer = e.pointerId;
      el.setPointerCapture(e.pointerId);
      el.classList.add("pressed");
      buzz();
      send({ t: btn.mode === "hold" ? "down" : "tap", id: btn.id });
      fx.down(e);
    });
    const end = (e) => {
      if (e.pointerId !== pointer) return;
      pointer = null;
      el.classList.remove("pressed");
      if (btn.mode === "hold") send({ t: "up", id: btn.id });
      fx.up();
    };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
    el.addEventListener("contextmenu", (e) => e.preventDefault());
  }

  // Hold-to-talk: the PC listens for a voice command while this is held down.
  function bindTalk(el) {
    let pointer = null;
    el.addEventListener("pointerdown", (e) => {
      if (pointer !== null) return;
      pointer = e.pointerId;
      el.setPointerCapture(e.pointerId);
      el.classList.add("listening");
      buzz();
      send({ t: "voice", on: true });
    });
    const end = (e) => {
      if (e.pointerId !== pointer) return;
      pointer = null;
      el.classList.remove("listening");
      send({ t: "voice", on: false });
    };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
    el.addEventListener("contextmenu", (e) => e.preventDefault());
  }
  bindTalk($("talk"));

  // ---------- pairing + load ----------

  function showPairing(wrong) {
    setOnline(false, "Not paired");
    tabs.innerHTML = "";
    grid.innerHTML = `
      <form id="pair">
        <p></p>
        <input id="code" autocomplete="off" autocapitalize="off" spellcheck="false" maxlength="32" placeholder="pairing code">
        <button type="submit">Connect</button>
      </form>`;
    grid.querySelector("p").textContent = wrong
      ? "That pairing code didn't match. Check the code shown in the Tablet Deck window on your PC."
      : "Enter the pairing code shown in the Tablet Deck window on your PC.";
    const input = $("code");
    input.value = token;
    input.focus();
    $("pair").onsubmit = (e) => {
      e.preventDefault();
      token = input.value.trim().toLowerCase();
      if (token) load();
    };
  }

  function load() {
    fetch(`/layout.json?t=${encodeURIComponent(token)}&device=${device}`)
      .then((r) => {
        if (r.status === 403) return Promise.reject("pair");
        return r.ok ? r.json() : Promise.reject(`Layout error (${r.status})`);
      })
      .then((l) => {
        layout = l;
        store.set("token", token);
        $("title").textContent = layout.name;
        document.title = layout.name;
        if (layout.canvas) {
          document.body.classList.add("canvas-mode");
          window.renderCanvas(layout, {
            send, bindButton, bindTalk, buzz, store, refreshStatus, openPicker, toggleFullscreen,
            deckIcon: window.deckIcon,
            emblemHtml: () => emblemSvg((themeById[document.documentElement.dataset.theme] || THEMES[0]).emblem),
          });
        } else {
          if (pageIndex >= layout.pages.length) pageIndex = 0;
          renderTabs();
          renderPage();
        }
        if (!ws) connect();
      })
      .catch((err) => {
        if (err === "pair") { showPairing(Boolean(token)); return; }
        grid.innerHTML = `<div id="msg"></div>`;
        $("msg").textContent = String(err);
        setOnline(false, "Error");
      });
  }

  function toggleFullscreen() {
    const d = document.documentElement;
    if (document.fullscreenElement) document.exitFullscreen();
    else (d.requestFullscreen || d.webkitRequestFullscreen)?.call(d);
  }
  $("fs").onclick = toggleFullscreen;

  buildPicker();
  applyTheme(store.get("theme") || "rsi");
  if (token) load(); else showPairing(false);
})();
