const reducedMotion = document.documentElement.dataset.motion === "reduced";
const finePointer = matchMedia("(pointer: fine)").matches;

/* ---------- przełącznik efektów: zapamiętany wybór wygrywa z systemem ---------- */

function startMotionToggle() {
  const toggle = document.querySelector(".motion-toggle");
  toggle.setAttribute("aria-pressed", String(!reducedMotion));
  toggle.textContent = reducedMotion ? "Efekty: wył." : "Efekty: wł.";
  toggle.addEventListener("click", () => {
    try {
      localStorage.setItem("dismode-motion", reducedMotion ? "full" : "reduced");
    } catch (error) {
      console.warn("Nie udało się zapamiętać wyboru efektów:", error);
    }
    location.reload();
  });
}

/* ---------- tło hero: shader WebGL2 "warp" ---------- */

function startWarp() {
  const canvas = document.getElementById("warp");
  const gl = canvas.getContext("webgl2", { antialias: false, alpha: false });
  if (!gl || reducedMotion) {
    return;
  }

  const vertex = `#version 300 es
    in vec2 p;
    void main() { gl_Position = vec4(p, 0.0, 1.0); }`;

  // Smugi prędkości w barwach logo: każda warstwa to pas szumu
  // rozciągnięty w poziomie i przesuwany w stronę kursora.
  const fragment = `#version 300 es
    precision highp float;
    uniform vec2 res;
    uniform float time;
    uniform vec2 mouse;
    uniform float scroll;
    out vec4 color;

    float hash(vec2 p) {
      return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
    }

    float noise(vec2 p) {
      vec2 i = floor(p), f = fract(p);
      vec2 u = f * f * (3.0 - 2.0 * f);
      return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x),
                 mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
    }

    void main() {
      vec2 uv = (gl_FragCoord.xy - 0.5 * res) / res.y;
      vec2 m = (mouse - 0.5) * vec2(res.x / res.y, 1.0);
      uv += m * 0.08;

      vec3 deep = vec3(0.02, 0.03, 0.07);
      vec3 blue = vec3(0.18, 0.48, 1.0);
      vec3 violet = vec3(0.54, 0.36, 1.0);
      vec3 cyan = vec3(0.31, 0.85, 1.0);
      vec3 col = deep;

      for (int layer = 0; layer < 5; layer++) {
        float fl = float(layer);
        float speed = 0.25 + fl * 0.18;
        vec2 q = vec2(uv.x * (1.2 + fl * 0.4) - time * speed, uv.y * (14.0 + fl * 9.0) + fl * 7.3);
        float streak = noise(q) * noise(q * vec2(0.35, 1.0) + fl);
        streak = smoothstep(0.42, 0.95, streak);
        float band = exp(-pow((uv.y - 0.12 * sin(time * 0.2 + fl)) * (1.6 + fl * 0.3), 2.0));
        vec3 tint = mix(violet, mix(blue, cyan, fl / 4.0), fl / 4.0);
        col += tint * streak * band * (0.55 - fl * 0.07);
      }

      float glow = exp(-length(uv - m * 0.6) * 2.2);
      col += blue * glow * 0.18;
      col *= 1.0 - scroll * 0.55;
      col *= smoothstep(1.35, 0.25, length(uv));
      color = vec4(col, 1.0);
    }`;

  function compile(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      throw new Error(gl.getShaderInfoLog(shader) ?? "shader");
    }
    return shader;
  }

  const program = gl.createProgram();
  try {
    gl.attachShader(program, compile(gl.VERTEX_SHADER, vertex));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, fragment));
  } catch (error) {
    console.warn("Shader tła wyłączony:", error);
    return;
  }
  gl.linkProgram(program);
  gl.useProgram(program);

  const buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  const location = gl.getAttribLocation(program, "p");
  gl.enableVertexAttribArray(location);
  gl.vertexAttribPointer(location, 2, gl.FLOAT, false, 0, 0);

  const uniforms = {
    res: gl.getUniformLocation(program, "res"),
    time: gl.getUniformLocation(program, "time"),
    mouse: gl.getUniformLocation(program, "mouse"),
    scroll: gl.getUniformLocation(program, "scroll"),
  };

  const mouse = { x: 0.5, y: 0.5, tx: 0.5, ty: 0.5 };
  addEventListener("pointermove", (event) => {
    mouse.tx = event.clientX / innerWidth;
    mouse.ty = 1 - event.clientY / innerHeight;
  }, { passive: true });

  function resize() {
    const scale = Math.min(devicePixelRatio, 1.5) * 0.75;
    canvas.width = Math.round(innerWidth * scale);
    canvas.height = Math.round(innerHeight * scale);
    gl.viewport(0, 0, canvas.width, canvas.height);
  }
  resize();
  addEventListener("resize", resize);

  let running = true;
  document.addEventListener("visibilitychange", () => {
    running = !document.hidden;
    if (running) {
      requestAnimationFrame(frame);
    }
  });

  function frame(now) {
    if (!running) {
      return;
    }
    mouse.x += (mouse.tx - mouse.x) * 0.05;
    mouse.y += (mouse.ty - mouse.y) * 0.05;
    gl.uniform2f(uniforms.res, canvas.width, canvas.height);
    gl.uniform1f(uniforms.time, now / 1000);
    gl.uniform2f(uniforms.mouse, mouse.x, mouse.y);
    gl.uniform1f(uniforms.scroll, Math.min(scrollY / innerHeight, 1));
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
}

/* ---------- nagłówek, który się "rozszyfrowuje" ---------- */

function scramble(element, delay) {
  const target = element.dataset.text;
  const glyphs = "▚▞▙▟◢◣◤◥01#%&$@";
  const duration = 900;
  const start = performance.now() + delay;

  function tick(now) {
    const progress = Math.max(0, Math.min(1, (now - start) / duration));
    let out = "";
    for (let i = 0; i < target.length; i++) {
      const revealAt = i / target.length;
      out += target[i] === " " || progress > revealAt
        ? target[i]
        : glyphs[Math.floor(Math.random() * glyphs.length)];
    }
    element.textContent = out;
    if (progress < 1) {
      requestAnimationFrame(tick);
    }
  }
  requestAnimationFrame(tick);
}

/* ---------- przechylenie logo i magnetyczne przyciski ---------- */

function startTilt() {
  for (const stage of document.querySelectorAll(".tilt")) {
    stage.addEventListener("pointermove", (event) => {
      const rect = stage.getBoundingClientRect();
      const x = (event.clientX - rect.left) / rect.width - 0.5;
      const y = (event.clientY - rect.top) / rect.height - 0.5;
      stage.style.transform = `perspective(900px) rotateY(${x * 18}deg) rotateX(${-y * 18}deg)`;
    });
    stage.addEventListener("pointerleave", () => {
      stage.style.transform = "";
    });
  }

  for (const button of document.querySelectorAll(".magnetic")) {
    button.addEventListener("pointermove", (event) => {
      const rect = button.getBoundingClientRect();
      const x = event.clientX - rect.left - rect.width / 2;
      const y = event.clientY - rect.top - rect.height / 2;
      button.style.transform = `translate(${x * 0.18}px, ${y * 0.3}px)`;
    });
    button.addEventListener("pointerleave", () => {
      button.style.transform = "";
    });
  }
}

/* ---------- poświata za kursorem na kartach ---------- */

function startSpotlight() {
  for (const card of document.querySelectorAll(".card")) {
    card.addEventListener("pointermove", (event) => {
      const rect = card.getBoundingClientRect();
      card.style.setProperty("--mx", `${event.clientX - rect.left}px`);
      card.style.setProperty("--my", `${event.clientY - rect.top}px`);
    });
  }
}

/* ---------- zapas dla przeglądarek bez animation-timeline ---------- */

function startRevealFallback() {
  if (CSS.supports("animation-timeline: view()")) {
    return;
  }
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (entry.isIntersecting) {
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      }
    }
  }, { threshold: 0.15 });
  for (const element of document.querySelectorAll(".reveal")) {
    observer.observe(element);
  }
}

/* ---------- instrukcja: przełączanie kroków przez View Transitions ---------- */

function startGuide() {
  const tabs = [...document.querySelectorAll(".guide-tabs [role=tab]")];
  const panel = document.querySelector(".guide-panel");
  const templates = [...panel.querySelectorAll("template")];

  function render(step) {
    const template = templates.find((item) => item.dataset.step === String(step));
    panel.replaceChildren(template.content.cloneNode(true), ...templates);
    for (const tab of tabs) {
      tab.setAttribute("aria-selected", String(tab.dataset.step === String(step)));
    }
  }

  function select(step) {
    if (!document.startViewTransition || reducedMotion) {
      render(step);
      return;
    }
    document.startViewTransition(() => render(step));
  }

  for (const tab of tabs) {
    tab.addEventListener("click", () => select(tab.dataset.step));
    tab.addEventListener("keydown", (event) => {
      const index = tabs.indexOf(tab);
      const next = event.key === "ArrowDown" || event.key === "ArrowRight"
        ? index + 1
        : event.key === "ArrowUp" || event.key === "ArrowLeft" ? index - 1 : null;
      if (next === null) {
        return;
      }
      event.preventDefault();
      const target = tabs[(next + tabs.length) % tabs.length];
      target.focus();
      select(target.dataset.step);
    });
  }
  render(0);
}

/* ---------- wykres czasu klatki (ilustracja) ---------- */

function startFrametime() {
  const canvas = document.getElementById("frametime");
  const readout = document.getElementById("fps-readout");
  const context = canvas.getContext("2d");
  const samples = [];
  let visible = false;

  new IntersectionObserver(([entry]) => {
    visible = entry.isIntersecting;
  }).observe(canvas);

  function resize() {
    const ratio = devicePixelRatio;
    canvas.width = canvas.clientWidth * ratio;
    canvas.height = canvas.clientHeight * ratio;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
  }
  resize();
  addEventListener("resize", resize);

  let phase = 0;
  function nextSample() {
    phase += 0.02;
    const base = 6.9 + Math.sin(phase) * 0.35;
    const spike = Math.random() < 0.015 ? 6 + Math.random() * 6 : 0;
    return base + Math.random() * 0.6 + spike;
  }

  function frame() {
    requestAnimationFrame(frame);
    if (!visible) {
      return;
    }
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    const capacity = Math.ceil(width / 3);
    // Wykres jest pełny od pierwszej klatki, zamiast rosnąć od zera.
    do {
      samples.push(nextSample());
    } while (samples.length < capacity);
    while (samples.length > capacity) {
      samples.shift();
    }

    context.clearRect(0, 0, width, height);
    context.strokeStyle = "rgba(120, 140, 255, 0.12)";
    context.lineWidth = 1;
    for (const ms of [8.33, 16.67]) {
      const y = height - (ms / 20) * height;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(width, y);
      context.stroke();
    }

    const gradient = context.createLinearGradient(0, 0, width, 0);
    gradient.addColorStop(0, "#8a5cff");
    gradient.addColorStop(0.5, "#2f7bff");
    gradient.addColorStop(1, "#4fd8ff");
    context.strokeStyle = gradient;
    context.lineWidth = 2;
    context.beginPath();
    samples.forEach((ms, index) => {
      const x = index * 3;
      const y = height - (Math.min(ms, 20) / 20) * height;
      index ? context.lineTo(x, y) : context.moveTo(x, y);
    });
    context.stroke();

    if (Math.round(phase * 50) % 10 === 0) {
      const recent = samples.slice(-30);
      const average = recent.reduce((sum, value) => sum + value, 0) / recent.length;
      readout.textContent = `${Math.round(1000 / average)} FPS · ${average.toFixed(1)} ms`;
    }
  }
  if (!reducedMotion) {
    requestAnimationFrame(frame);
  }
}

startMotionToggle();
startWarp();
startGuide();
startRevealFallback();
startFrametime();
if (!reducedMotion) {
  document.querySelectorAll(".scramble").forEach((element, index) => scramble(element, 200 + index * 350));
  if (finePointer) {
    startTilt();
    startSpotlight();
  }
}
