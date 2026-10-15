// Press animations for deck buttons (adapted from the Key Press Animations sheet).
// DeckFX.attach(el, def) adds an effect layer to a button and returns
// { down(event), up() }, called by the button's pointer handlers.
// Effects are visual only: the deck can't see game state, so toggles such as
// DEPLOYED/RETRACTED simply flip on each press.
(() => {
  const CIRC = 251.33;
  const el = (tag, cls) => { const n = document.createElement(tag); if (cls) n.className = cls; return n; };
  const once = (parent, node) => {
    parent.append(node);
    node.addEventListener("animationend", () => node.remove(), { once: true });
    return node;
  };
  const retrigger = (t, cls) => { t.classList.remove(cls); void t.offsetWidth; t.classList.add(cls); };
  const flash = (t) => once(t._fx, el("span", "fx-flash"));
  const setState = (t, s) => { if (t._state) t._state.textContent = s; };

  // Runs onProgress(0..1) for ms, then onDone; returns a cancel function.
  function progress(ms, onProgress, onDone) {
    const t0 = performance.now();
    let raf;
    const step = (now) => {
      const p = Math.min(1, (now - t0) / ms);
      onProgress(p);
      if (p < 1) raf = requestAnimationFrame(step); else onDone();
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }

  const A = {
    // Default tap: flash + small kick.
    flash: {
      down(t) { retrigger(t, "fx-kick"); flash(t); },
    },

    ripple: {
      setup(t) { setState(t, "OFF"); },
      down(t, e) {
        const r = t.getBoundingClientRect();
        const sx = r.width / t.offsetWidth || 1;
        const x = ((e?.clientX ?? r.left + r.width / 2) - r.left) / sx;
        const y = ((e?.clientY ?? r.top + r.height / 2) - r.top) / sx;
        const on = t.classList.toggle("fx-on");
        for (let i = 0; i < 2; i++) {
          const s = el("span", "fx-ripple");
          s.style.left = `${x}px`; s.style.top = `${y}px`; s.style.animationDelay = `${i * 130}ms`;
          once(t._fx, s);
        }
        setState(t, on ? "ONLINE" : "OFF");
      },
    },

    gear: {
      setup(t) {
        for (let i = 0; i < 3; i++) {
          const s = el("span", "fx-strut");
          s.style.left = `${25 + i * 25}%`; s.style.setProperty("--i", i);
          t._fx.append(s);
        }
        setState(t, "RETRACTED");
      },
      down(t) {
        const on = t.classList.toggle("fx-on");
        setState(t, on ? "DEPLOYING…" : "RETRACTING…");
        clearTimeout(t._tm);
        t._tm = setTimeout(() => setState(t, on ? "DEPLOYED" : "RETRACTED"), 750);
      },
    },

    // Hold to fill the ring (the key is held the whole time); flashes when full.
    charge: {
      setup(t, def) {
        t._fx.innerHTML = `<svg class="fx-ring" viewBox="0 0 100 100"><circle class="track" cx="50" cy="50" r="40"/><circle class="prog" cx="50" cy="50" r="40" transform="rotate(-90 50 50)"/></svg>`;
        t._idle = def.mode === "hold" ? "HOLD TO ENGAGE" : "";
        setState(t, t._idle);
      },
      down(t, e, def) {
        const c = t.querySelector(".fx-ring .prog");
        c.classList.remove("drain");
        t.classList.add("fx-charging");
        setState(t, "SPOOLING…");
        t._cancel = progress(def.chargeMs || 1200, (p) => { c.style.strokeDashoffset = CIRC * (1 - p); }, () => {
          t._cancel = null;
          t.classList.remove("fx-charging");
          t.classList.add("fx-on");
          flash(t);
          setState(t, "ENGAGED");
        });
      },
      up(t) {
        if (t._cancel) { t._cancel(); t._cancel = null; }
        t.classList.remove("fx-charging");
        const c = t.querySelector(".fx-ring .prog");
        c.classList.add("drain");
        c.style.strokeDashoffset = CIRC;
        clearTimeout(t._tm);
        t._tm = setTimeout(() => { t.classList.remove("fx-on"); setState(t, t._idle); }, t.classList.contains("fx-on") ? 1500 : 0);
      },
    },

    burst: {
      down(t) {
        retrigger(t, "fx-kick");
        for (let i = 0; i < 14; i++) {
          const s = el("span", "fx-spark");
          const a = Math.random() * Math.PI * 2, d = 40 + Math.random() * 60;
          s.style.setProperty("--dx", `${Math.cos(a) * d}px`);
          s.style.setProperty("--dy", `${Math.sin(a) * d}px`);
          s.style.animationDuration = `${500 + Math.random() * 400}ms`;
          once(t._fx, s);
        }
      },
    },

    glow: {
      setup(t) { t._fx.append(el("span", "fx-beam")); setState(t, "OFF"); },
      down(t) {
        const on = t.classList.toggle("fx-on");
        if (on) retrigger(t, "fx-flicker"); else t.classList.remove("fx-flicker");
        setState(t, on ? "ON" : "OFF");
      },
    },

    recoil: {
      down(t) { retrigger(t, "fx-shot"); once(t._fx, el("span", "fx-muzzle")); },
    },

    // Speed streaks while held; a short burst for taps.
    streak: {
      spawn(t) {
        const s = el("span", "fx-streak");
        s.style.top = `${8 + Math.random() * 84}%`;
        s.style.width = `${30 + Math.random() * 50}px`;
        s.style.animationDuration = `${220 + Math.random() * 220}ms`;
        once(t._fx, s);
      },
      down(t, e, def) {
        t.classList.add("fx-boost");
        clearInterval(t._rep);
        t._rep = setInterval(() => this.spawn(t), 35);
        if (def.mode !== "hold") setTimeout(() => this.up(t), 420);
      },
      up(t) { clearInterval(t._rep); t.classList.remove("fx-boost"); },
    },

    // "set": true/false forces open/closed (Open/Close buttons); omitted toggles.
    doors: {
      setup(t, def) {
        t._fx.append(el("span", "fx-door l"), el("span", "fx-door r"));
        if (def.set === undefined) setState(t, "CLOSED");
      },
      down(t, e, def) {
        const on = def.set === undefined ? t.classList.toggle("fx-on") : def.set;
        if (def.set !== undefined) {
          // One-way buttons: start from the opposite position (no transition) so the
          // doors always visibly slide open or shut; open ones close again afterwards.
          clearTimeout(t._tm);
          t.classList.add("fx-notrans");
          t.classList.toggle("fx-on", !on);
          void t.offsetWidth;
          t.classList.remove("fx-notrans");
          t.classList.toggle("fx-on", on);
          if (on) t._tm = setTimeout(() => t.classList.remove("fx-on"), 1600);
          return;
        }
        setState(t, on ? "OPENING…" : "CLOSING…");
        clearTimeout(t._tm);
        t._tm = setTimeout(() => setState(t, on ? "OPEN" : "CLOSED"), 750);
      },
    },

    lock: {
      setup(t) {
        const corners = [[-1, -1, "border-right:0;border-bottom:0"], [1, -1, "border-left:0;border-bottom:0"],
          [-1, 1, "border-right:0;border-top:0"], [1, 1, "border-left:0;border-top:0"]];
        for (const [sx, sy, css] of corners) {
          const b = el("span", "fx-br");
          b.style.cssText = css;
          b.style.setProperty("--sx", sx); b.style.setProperty("--sy", sy);
          t._fx.append(b);
        }
        t._fx.append(el("span", "fx-dot"));
      },
      down(t) {
        clearTimeout(t._tm);
        t.classList.remove("fx-locked");
        retrigger(t, "fx-locking");
        setState(t, "ACQUIRING…");
        t._tm = setTimeout(() => {
          t.classList.add("fx-locked");
          setState(t, "LOCKED");
          t._tm = setTimeout(() => { t.classList.remove("fx-locked", "fx-locking"); setState(t, ""); }, 1400);
        }, 650);
      },
    },

    // Arming bar: fills while held (or for the timed hold), jolts and flashes when complete.
    hazard: {
      setup(t, def) {
        t._fx.append(el("span", "fx-hazard"));
        t._idle = def.mode === "hold" ? "HOLD TO ARM" : "";
        setState(t, t._idle);
      },
      down(t, e, def) {
        const bar = t._fx.querySelector(".fx-hazard");
        bar.classList.remove("drain");
        t.classList.add("fx-arming");
        setState(t, "ARMING…");
        t._cancel = progress(def.armMs || def.ms || 2000, (p) => { bar.style.transform = `scaleX(${p})`; }, () => {
          t._cancel = null;
          t.classList.remove("fx-arming");
          retrigger(t, "fx-ejected");
          flash(t);
          setState(t, "ARMED");
          if (def.mode !== "hold") this.reset(t);
        });
      },
      up(t, def) { if (def.mode === "hold") this.reset(t); },
      reset(t) {
        if (t._cancel) { t._cancel(); t._cancel = null; }
        t.classList.remove("fx-arming");
        const bar = t._fx.querySelector(".fx-hazard");
        bar.classList.add("drain");
        bar.style.transform = "scaleX(0)";
        clearTimeout(t._tm);
        t._tm = setTimeout(() => setState(t, t._idle), 600);
      },
    },

    // Thin progress line for timed holds (the server holds the key for `ms`).
    timed: {
      setup(t) { t._fx.append(el("span", "fx-timer")); },
      down(t, e, def) {
        const bar = t._fx.querySelector(".fx-timer");
        bar.classList.remove("drain");
        if (t._cancel) t._cancel();
        t._cancel = progress(def.ms || 600, (p) => { bar.style.transform = `scaleX(${p})`; }, () => {
          t._cancel = null;
          flash(t);
          bar.classList.add("drain");
          bar.style.transform = "scaleX(0)";
        });
      },
    },
  };

  // Sensible defaults when a button doesn't name an animation.
  const BY_ID = {
    power: "ripple", "flight-ready": "ripple", gear: "gear", lights: "glow", decoy: "burst", noise: "burst",
    doors: "doors", "quantum-jump": "charge", "power-weapons": "recoil",
  };
  const BY_ICON = { target: "lock", decoy: "burst", lights: "glow", missile: "recoil" };

  function pick(def) {
    if (def.anim) return def.anim;
    if (BY_ID[def.id]) return BY_ID[def.id];
    if (BY_ICON[def.icon]) return BY_ICON[def.icon];
    if (def.mode === "timed") return "timed";
    return "flash";
  }

  const STATEFUL = new Set(["ripple", "gear", "charge", "glow", "doors", "lock", "hazard"]);

  window.DeckFX = {
    attach(t, def) {
      const name = pick(def);
      const a = A[name] || A.flash;
      // "fxa-" (not "fx-"): particle classes such as fx-flash and fx-streak share these names.
      t.classList.add("fx-host", `fxa-${name}`);
      t._fx = el("span", "fx-layer");
      t.prepend(t._fx);
      if (STATEFUL.has(name)) { t._state = el("span", "fx-state"); t.append(t._state); }
      a.setup?.call(a, t, def);
      return {
        down: (e) => a.down?.call(a, t, e, def),
        up: () => a.up?.call(a, t, def),
      };
    },
    // One-off effects for controls that aren't buttons (the eject slider).
    pulse(t, kind) {
      // No fx-host here: its overflow clipping would cut off the slider knob.
      if (!t._fx) { t._fx = el("span", "fx-layer"); t.prepend(t._fx); }
      if (kind === "eject") { retrigger(t, "fx-ejected"); flash(t); }
      else flash(t);
    },
  };
})();
