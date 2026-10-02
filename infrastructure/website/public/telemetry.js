// Wyłącznie dane syntetyczne; ta demonstracja nie mierzy wydajności komputera.
export function startTelemetry(canvas) {
  if (!canvas) return () => {};
  const context = canvas.getContext("2d");
  if (!context) return () => {};
  const container = canvas.closest(".telemetry-demo");
  container?.classList.add("demo-running");
  let frame = 0, visible = true, stopped = false, last = 0, sample = 0;
  const samples = Array.from({ length: 90 }, (_, i) => 11 + Math.sin(i * .6) * 1.2);
  const value = document.querySelector("[data-demo-frame]");
  function draw() {
    const box = canvas.getBoundingClientRect();
    const dpr = Math.min(devicePixelRatio || 1, 1.5);
    const width = Math.max(1, Math.round(box.width * dpr));
    const height = Math.max(1, Math.round(box.height * dpr));
    if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    const w = width / dpr, h = height / dpr;
    context.clearRect(0, 0, w, h);
    context.strokeStyle = "#33454d";
    context.fillStyle = "#9da5aa";
    context.font = '11px "Segoe UI", sans-serif';
    [0, 10, 20, 30].forEach((ms) => {
      const y = h - 24 - ms / 30 * (h - 40);
      context.beginPath(); context.moveTo(34, y); context.lineTo(w - 8, y); context.stroke();
      context.fillText(ms + " ms", 0, y - 3);
    });
    context.strokeStyle = "#00d7e2";
    context.lineWidth = 1.6;
    context.beginPath();
    samples.forEach((ms, i) => {
      const x = 34 + i / 89 * (w - 42), y = h - 24 - ms / 30 * (h - 40);
      if (i) context.lineTo(x, y); else context.moveTo(x, y);
    });
    context.stroke();
  }
  function tick(now) {
    frame = 0;
    if (stopped || document.hidden || !visible) return;
    if (now - last > 100) {
      const ms = 11 + Math.sin(sample * .48) * 1.1 + Math.sin(sample * 1.7) * .5 + (sample % 57 === 0 ? 9 : 0);
      samples.shift(); samples.push(ms); sample++;
      draw();
      if (value) value.textContent = ms.toFixed(2).replace(".", ",") + " ms";
      last = now;
    }
    frame = requestAnimationFrame(tick);
  }
  function resume() {
    cancelAnimationFrame(frame);
    frame = 0;
    if (!document.hidden && visible && !stopped) frame = requestAnimationFrame(tick);
  }
  const observer = "IntersectionObserver" in window ? new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; resume(); }) : null;
  observer?.observe(canvas);
  document.addEventListener("visibilitychange", resume);
  addEventListener("resize", draw);
  draw(); resume();
  return () => {
    stopped = true;
    cancelAnimationFrame(frame);
    observer?.disconnect();
    document.removeEventListener("visibilitychange", resume);
    removeEventListener("resize", draw);
    container?.classList.remove("demo-running");
    if (value) value.textContent = "11,00 ms";
  };
}
