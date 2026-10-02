// WebGL2 / 2D bez zależności; gradient CSS działa także bez canvas.
export function startAurora(original) {
  if (!original) return () => {};
  let canvas = original;
  let renderer;
  let frame = 0;
  let visible = true;
  let disposed = false;
  let last = 0;
  const cleanups = [];
  let gl;
  try { gl = canvas.getContext("webgl2", { alpha: true, antialias: false, depth: false, stencil: false, powerPreference: "low-power" }); }
  catch { gl = null; }
  if (gl) {
    try {
      const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) { gl.deleteShader(shader); throw new Error("shader"); }
        return shader;
      };
      const vertex = compile(gl.VERTEX_SHADER, `#version 300 es
      in vec2 position; out vec2 uv;
      void main(){uv=position*.5+.5;gl_Position=vec4(position,0.,1.);}`);
      const fragment = compile(gl.FRAGMENT_SHADER, `#version 300 es
      precision mediump float;
      in vec2 uv; out vec4 color; uniform float time; uniform float aspect;
      void main(){
        vec2 p=vec2(uv.x*aspect,uv.y);
        float warp=.10*sin(p.x*3.2+time*.19)+.065*sin(p.x*7.4-time*.14);
        float ribbon=exp(-abs(p.y-.49-warp)*12.);
        float haze=exp(-abs(p.y-.63-warp*.6)*5.);
        float threads=.5+.5*sin(p.x*12.+p.y*7.+time*.26);
        vec3 cyan=vec3(0.,.843,.886), teal=vec3(.125,.51,.55);
        color=vec4(mix(teal,cyan,threads),(.11*ribbon+.045*haze)*(.6+.4*uv.x));
      }`);
      const program = gl.createProgram();
      gl.attachShader(program, vertex);
      gl.attachShader(program, fragment);
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error("program");
      const buffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,3,-1,-1,3]), gl.STATIC_DRAW);
      gl.useProgram(program);
      const position = gl.getAttribLocation(program, "position");
      gl.enableVertexAttribArray(position);
      gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
      const time = gl.getUniformLocation(program, "time");
      const aspect = gl.getUniformLocation(program, "aspect");
      renderer = (seconds) => {
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.uniform1f(time, seconds);
        gl.uniform1f(aspect, canvas.width / canvas.height);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
      };
      cleanups.push(() => { gl.deleteBuffer(buffer); gl.deleteProgram(program); gl.deleteShader(vertex); gl.deleteShader(fragment); });
    } catch { renderer = null; }
  }
  function fallback() {
    // Canvas z kontekstem GL nie może dostać kontekstu 2D.
    if (gl) { canvas = original.cloneNode(); original.replaceWith(canvas); }
    const context = canvas.getContext("2d");
    if (!context) return;
    renderer = (seconds) => {
      const w = canvas.width, h = canvas.height;
      context.clearRect(0, 0, w, h);
      const y = h * (.43 + Math.sin(seconds * .18) * .045);
      const gradient = context.createRadialGradient(w * .7, y, 0, w * .7, y, w * .55);
      gradient.addColorStop(0, "rgba(0,215,226,.14)");
      gradient.addColorStop(.5, "rgba(31,130,141,.06)");
      gradient.addColorStop(1, "rgba(0,0,0,0)");
      context.fillStyle = gradient;
      context.fillRect(0, 0, w, h);
    };
  }
  if (!renderer) fallback();
  function resize() {
    const box = canvas.getBoundingClientRect();
    if (!box.width || !box.height) return;
    const dpr = Math.min(devicePixelRatio || 1, 1.5);
    // Niska rozdzielczość tła + limit pola: shader nie konkuruje z treścią.
    const scale = Math.min(dpr, Math.sqrt(1_200_000 / (box.width * box.height)));
    canvas.width = Math.max(1, Math.round(box.width * scale));
    canvas.height = Math.max(1, Math.round(box.height * scale));
    renderer?.(performance.now() / 1000);
  }
  function tick(now) {
    frame = 0;
    if (disposed || document.hidden || !visible || !renderer) return;
    if (now - last >= 1000 / 30) { renderer(now / 1000); last = now; }
    frame = requestAnimationFrame(tick);
  }
  function resume() {
    cancelAnimationFrame(frame);
    frame = 0;
    if (!document.hidden && visible && !disposed && renderer) frame = requestAnimationFrame(tick);
  }
  function lost(event) {
    event.preventDefault();
    cancelAnimationFrame(frame);
    observer?.unobserve(canvas);
    fallback();
    observer?.observe(canvas);
    resize();
    resume();
  }
  original.addEventListener("webglcontextlost", lost);
  document.addEventListener("visibilitychange", resume);
  let observer;
  if ("IntersectionObserver" in window) {
    observer = new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; resume(); });
    observer.observe(canvas);
  }
  const sizes = "ResizeObserver" in window ? new ResizeObserver(resize) : null;
  sizes?.observe(original.parentElement);
  addEventListener("resize", resize);
  resize();
  resume();
  return () => {
    disposed = true;
    cancelAnimationFrame(frame);
    observer?.disconnect();
    sizes?.disconnect();
    document.removeEventListener("visibilitychange", resume);
    removeEventListener("resize", resize);
    original.removeEventListener("webglcontextlost", lost);
    cleanups.forEach((cleanup) => cleanup());
    if (canvas !== original) canvas.replaceWith(original);
    // Bez efektów canvas jest ukryty w CSS; gradient pozostaje.
  };
}
