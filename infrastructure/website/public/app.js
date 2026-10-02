"use strict";

(() => {
  // Nawigacja i instrukcja nie zależą od modułów animacji.
  const links = [...document.querySelectorAll(".section-nav a")];
  const sections = links.map((link) => document.getElementById(link.hash.slice(1)));
  let scheduled = false;
  function updateNavigation() {
    scheduled = false;
    const line = (document.querySelector(".site-header")?.getBoundingClientRect().height ?? 0) + 32;
    let current = -1;
    sections.forEach((section, index) => {
      if (section && section.getBoundingClientRect().top <= line) current = index;
    });
    // Krótka ostatnia sekcja może nigdy nie dojść do linii pod nagłówkiem.
    if (window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2) current = links.length - 1;
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

  const controls = document.querySelector(".step-controls");
  const steps = [...document.querySelectorAll(".install-steps article")];
  if (!controls || !steps.length) return;
  const buttons = [...controls.querySelectorAll("[data-step]")];
  const output = controls.querySelector("[data-step-status]");
  let current = 0;
  function show(index, animate = true) {
    const update = () => {
      current = Math.max(0, Math.min(steps.length - 1, index));
      steps.forEach((step, i) => {
        if (i === current) step.setAttribute("data-active", "true");
        else step.removeAttribute("data-active");
      });
      buttons.forEach((button, i) => {
        if (i === current) button.setAttribute("aria-current", "step");
        else button.removeAttribute("aria-current");
      });
      output.textContent = `Krok ${current + 1} z ${steps.length}`;
      controls.querySelector("[data-step-prev]").disabled = current === 0;
      controls.querySelector("[data-step-next]").disabled = current === steps.length - 1;
      controls.querySelector("progress").value = current + 1;
    };
    if (animate && window.dismodeMotion) window.dismodeMotion.transition(update);
    else update();
  }
  controls.hidden = false;
  buttons.forEach((button, index) => button.addEventListener("click", () => show(index)));
  controls.querySelector("[data-step-prev]").addEventListener("click", () => show(current - 1));
  controls.querySelector("[data-step-next]").addEventListener("click", () => show(current + 1));
  show(0, false);
})();
