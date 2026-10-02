"use strict";

// Preferencja jest znana przed pierwszym rysowaniem; tylko ten plik ładuje efekty.
(() => {
  const root = document.documentElement;
  const system = matchMedia("(prefers-reduced-motion: reduce)");
  let choice = null;
  let stop = null;
  let generation = 0;
  let ready = false;
  let effectModule;
  const read = () => {
    try { return localStorage.getItem("dismode-motion"); } catch { return null; }
  };
  const enabled = () => !system.matches && choice !== "reduced";
  window.dismodeMotion = {
    enabled,
    transition(update) {
      if (enabled() && document.startViewTransition) {
        // Powtarzane kliknięcia kończą poprzednie przejście bez blokowania instrukcji.
        window.dismodeMotion.current?.skipTransition();
        const current = document.startViewTransition(update);
        window.dismodeMotion.current = current;
        current.finished.catch(() => {});
      } else update();
    }
  };
  async function apply() {
    const current = ++generation;
    const full = enabled();
    root.dataset.motion = full ? "full" : "reduced";
    root.dataset.effectsReady = "false";
    stop?.();
    stop = null;
    if (!full) window.dismodeMotion.current?.skipTransition();
    const toggle = document.querySelector(".motion-toggle");
    if (toggle) {
      toggle.hidden = false;
      toggle.disabled = system.matches;
      toggle.setAttribute("aria-pressed", String(full));
      toggle.querySelector("[data-motion-label]").textContent = system.matches
        ? "Efekty: wył. przez system" : "Efekty: " + (full ? "wł." : "wył.");
    }
    if (!full || !ready || !document.querySelector("#start")) return;
    try {
      effectModule ??= import("./effects.js");
      const module = await effectModule;
      if (current !== generation || !enabled()) return;
      stop = module.startEffects();
      root.dataset.effectsReady = "true";
    } catch {
      // Brak API / błąd ładowania nie chowa treści ani nie zatrzymuje instrukcji.
      root.dataset.effectsReady = "false";
      effectModule = undefined;
    }
  }
  choice = read();
  root.dataset.motion = enabled() ? "full" : "reduced";
  document.addEventListener("DOMContentLoaded", () => {
    ready = true;
    document.querySelector(".motion-toggle")?.addEventListener("click", () => {
      choice = enabled() ? "reduced" : "full";
      try { localStorage.setItem("dismode-motion", choice); } catch {}
      apply();
    });
    apply();
  }, { once: true });
  system.addEventListener("change", apply);
  addEventListener("storage", (event) => {
    if (event.key === "dismode-motion" || event.key === null) { choice = read(); apply(); }
  });
})();
