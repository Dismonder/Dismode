"use strict";

(() => {
  const root = document.documentElement;
  const preference = matchMedia("(prefers-reduced-motion: reduce)");
  const toggle = document.querySelector(".motion-toggle");
  let choice = null;

  function readChoice() {
    try {
      const value = localStorage.getItem("dismode-motion");
      return value === "full" || value === "reduced" ? value : null;
    } catch {
      return null;
    }
  }

  function updateMotion() {
    // Preferencja systemowa obowiązuje również przy zapisanym „full”.
    const enabled = !preference.matches && choice !== "reduced";
    root.dataset.motion = enabled ? "full" : "reduced";
    if (!toggle) return;
    toggle.hidden = false;
    toggle.disabled = preference.matches;
    toggle.setAttribute("aria-pressed", String(enabled));
    toggle.querySelector("[data-motion-label]").textContent = preference.matches
      ? "Efekty: wył. przez system"
      : "Efekty: " + (enabled ? "wł." : "wył.");
  }

  choice = readChoice();
  updateMotion();
  toggle?.addEventListener("click", () => {
    choice = root.dataset.motion === "full" ? "reduced" : "full";
    try {
      localStorage.setItem("dismode-motion", choice);
    } catch {
      // Wybór działa w tej karcie również przy zablokowanym storage.
    }
    updateMotion();
  });
  preference.addEventListener("change", updateMotion);
  addEventListener("storage", (event) => {
    if (event.key === "dismode-motion" || event.key === null) {
      choice = readChoice();
      updateMotion();
    }
  });

  const hero = document.querySelector(".session-map");
  let heroVisible = true;
  function updatePause() {
    root.dataset.paused = String(document.hidden || !heroVisible);
  }
  document.addEventListener("visibilitychange", updatePause);
  if (hero && "IntersectionObserver" in window) {
    new IntersectionObserver(([entry]) => {
      heroVisible = entry.isIntersecting;
      updatePause();
    }).observe(hero);
  }
  updatePause();

  const links = [...document.querySelectorAll(".section-nav a")];
  const sections = links.map((link) => document.getElementById(link.hash.slice(1)));
  if (!links.length) return;
  let scheduled = false;

  function updateNavigation() {
    scheduled = false;
    const header = document.querySelector(".site-header");
    const line = (header?.getBoundingClientRect().height ?? 0) + 32;
    let current = -1;
    sections.forEach((section, index) => {
      if (section && section.getBoundingClientRect().top <= line) current = index;
    });
    links.forEach((link, index) => {
      if (index === current) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    });
  }

  function scheduleNavigation() {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(updateNavigation);
  }
  addEventListener("scroll", scheduleNavigation, { passive: true });
  addEventListener("resize", scheduleNavigation);
  addEventListener("hashchange", scheduleNavigation);
  updateNavigation();
})();
