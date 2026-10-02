import { startAurora } from "./aurora.js";
import { startTelemetry } from "./telemetry.js";

export function startEffects() {
  const root = document.documentElement;
  const cleanups = [startAurora(document.querySelector(".hero-canvas")), startTelemetry(document.querySelector(".frametime-canvas"))];
  const animations = new Set();
  const animate = (element, frames, options) => {
    const animation = element.animate(frames, options);
    animations.add(animation);
    animation.finished.then(() => animations.delete(animation), () => animations.delete(animation));
    return animation;
  };
  function pause() {
    root.dataset.paused = String(document.hidden);
    animations.forEach((animation) => document.hidden ? animation.pause() : animation.play());
  }
  document.addEventListener("visibilitychange", pause);
  cleanups.push(() => document.removeEventListener("visibilitychange", pause));
  pause();

  // Zapisujemy jedynie klasy; bez inline style i bez naruszania CSP.
  const observed = [...document.querySelectorAll(".module-card, .section-heading, .news-grid article, .privacy-panel")];
  const phases = [...document.querySelectorAll(".phase-grid li")];
  const traces = [...document.querySelectorAll(".phase-trace")];
  const recovery = document.querySelector(".recovery-demo");
  const meters = [...document.querySelectorAll("[data-count]")];
  const entries = [...observed, ...phases, ...meters, ...(recovery ? [recovery] : [])];
  let observer;
  if ("IntersectionObserver" in window) {
    observer = new IntersectionObserver((batch) => batch.forEach(({ target, isIntersecting }) => {
      if (!isIntersecting) return;
      observer.unobserve(target);
      target.classList.add("is-revealed");
      if (observed.includes(target)) animate(target, [{ opacity: .4, transform: "translateY(18px)" }, { opacity: 1, transform: "translateY(0)" }], { duration: 650, easing: "cubic-bezier(.2,.7,.2,1)" });
      const index = phases.indexOf(target);
      if (index >= 0 && traces[index]) animate(traces[index], [{ strokeDashoffset: 1 }, { strokeDashoffset: 0 }], { duration: 700, delay: index * 140, fill: "backwards", easing: "ease-out" });
      if (target === recovery) target.classList.add("recovery-playing");
      if (target.hasAttribute("data-count")) {
        const final = Number(target.dataset.count);
        const start = performance.now();
        let frame;
        const update = (now) => {
          const progress = Math.min(1, (now - start) / 850);
          target.textContent = String(Math.round(final * (1 - (1 - progress) ** 3)));
          if (progress < 1) frame = requestAnimationFrame(update);
        };
        frame = requestAnimationFrame(update);
        cleanups.push(() => { cancelAnimationFrame(frame); target.textContent = String(final); });
      }
    }), { threshold: .16 });
    entries.forEach((entry) => observer.observe(entry));
    cleanups.push(() => observer.disconnect());
  }

  const fine = matchMedia("(hover: hover) and (pointer: fine)");
  const hero = document.querySelector(".session-map");
  const cards = [...document.querySelectorAll(".module-card")];
  const pointerAnimations = new Map();
  function setTransform(element, transform) {
    pointerAnimations.get(element)?.cancel();
    // Web Animations API nie zapisuje atrybutów style.
    const animation = element.animate([{ transform }, { transform }], { duration: 1, fill: "forwards" });
    pointerAnimations.set(element, animation);
  }
  function reset() {
    pointerAnimations.forEach((animation) => animation.cancel());
    pointerAnimations.clear();
  }
  function capability() { if (!fine.matches) reset(); }
  fine.addEventListener("change", capability);
  cleanups.push(() => fine.removeEventListener("change", capability));
  cards.forEach((card) => {
    const light = document.createElement("span");
    light.className = "card-spotlight";
    light.setAttribute("aria-hidden", "true");
    card.append(light);
    let frame = 0, latest;
    const move = (event) => {
      if (!fine.matches) return;
      latest = { x: event.clientX, y: event.clientY };
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        const box = card.getBoundingClientRect();
        const x = latest.x - box.left, y = latest.y - box.top;
        setTransform(card, `perspective(1100px) rotateX(${-(y / box.height - .5) * 4}deg) rotateY(${(x / box.width - .5) * 4}deg)`);
        setTransform(light, `translate(${x - 150}px,${y - 150}px)`);
      });
    };
    const leave = () => {
      cancelAnimationFrame(frame); frame = 0;
      [card, light].forEach((element) => { pointerAnimations.get(element)?.cancel(); pointerAnimations.delete(element); });
    };
    card.addEventListener("pointermove", move);
    card.addEventListener("pointerleave", leave);
    cleanups.push(() => { leave(); card.removeEventListener("pointermove", move); card.removeEventListener("pointerleave", leave); light.remove(); });
  });
  const heroArea = document.querySelector(".hero");
  let parallaxFrame = 0, pointer;
  const parallax = (event) => {
    if (!fine.matches || !hero) return;
    pointer = { x: event.clientX, y: event.clientY };
    if (parallaxFrame) return;
    parallaxFrame = requestAnimationFrame(() => {
      parallaxFrame = 0;
      const box = heroArea.getBoundingClientRect();
      setTransform(hero, `translate(${(pointer.x - box.left - box.width / 2) * .009}px,${(pointer.y - box.top - box.height / 2) * .009}px)`);
    });
  };
  const leaveHero = () => { cancelAnimationFrame(parallaxFrame); parallaxFrame = 0; pointerAnimations.get(hero)?.cancel(); pointerAnimations.delete(hero); };
  heroArea?.addEventListener("pointermove", parallax);
  heroArea?.addEventListener("pointerleave", leaveHero);
  cleanups.push(() => { leaveHero(); heroArea?.removeEventListener("pointermove", parallax); heroArea?.removeEventListener("pointerleave", leaveHero); reset(); });

  return () => {
    cleanups.reverse().forEach((cleanup) => cleanup());
    animations.forEach((animation) => animation.cancel());
    entries.forEach((entry) => entry.classList.remove("is-revealed", "recovery-playing"));
    delete root.dataset.paused;
  };
}
