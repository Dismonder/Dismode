import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../public/", import.meta.url));
const errors = [];
const documents = new Map();
const referenceQueue = [];
const uiNames = ["overview", "library", "session", "history", "diagnostics", "overlay", "system-optimizer", "memory-optimizer", "social-preview"];
const brandRoot = fileURLToPath(new URL("../../../assets/brand/ui/", import.meta.url));
let total = 0;
let references = 0;
let svgCount = 0;
const fail = (file, message) => errors.push(`${path.relative(root, file)}: ${message}`);

async function filesIn(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map((entry) => {
    const file = path.join(directory, entry.name);
    return entry.isDirectory() ? filesIn(file) : [file];
  }));
  return nested.flat();
}

function decode(value) {
  return value.replace(/&#(x[\da-f]+|\d+);|&(amp|quot|apos|lt|gt);/gi, (_, number, name) => {
    if (number) return String.fromCodePoint(number[0].toLowerCase() === "x" ? parseInt(number.slice(1), 16) : Number(number));
    return { amp: "&", quot: '"', apos: "'", lt: "<", gt: ">" }[name.toLowerCase()];
  });
}

// Tokenizacja wystarcza dla statycznego HTML tego projektu, bez pakietów npm.
function tagsIn(html) {
  const tags = [];
  for (const match of html.replace(/<!--[\s\S]*?-->/g, "").matchAll(/<([a-z][\w:-]*)\b((?:"[^"]*"|'[^']*'|[^'">])*)>/gi)) {
    const attrs = new Map();
    for (const attr of match[2].matchAll(/([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) {
      attrs.set(attr[1].toLowerCase(), decode(attr[2] ?? attr[3] ?? attr[4] ?? ""));
    }
    tags.push({ name: match[1].toLowerCase(), attrs });
  }
  return tags;
}

const files = await filesIn(root);
const brandNames = [...uiNames, "readme-hero"];
const svgElements = new Set(["svg", "title", "desc", "defs", "lineargradient", "radialgradient", "stop", "pattern", "g", "rect", "text", "tspan", "path", "circle", "ellipse", "line", "polyline", "polygon", "clippath", "mask", "image", "use"]);
async function checkSvg(file) {
  let source;
  try { source = await readFile(file, "utf8"); }
  catch (error) { fail(file, `brak wymaganego SVG: ${error.code}`); return; }
  svgCount++;
  if (!/^<svg\b[^>]*xmlns="http:\/\/www\.w3\.org\/2000\/svg"/.test(source.trim()) || !/<\/svg>\s*$/.test(source)) fail(file, "brak samodzielnego korzenia SVG");
  if (/<!(?:DOCTYPE|ENTITY)/i.test(source)) fail(file, "deklaracje XML zewnętrznych zasobów");
  if (!/<title\b[^>]*>[^<]+<\/title>/.test(source) || !/<desc\b[^>]*>[^<]+<\/desc>/.test(source)) fail(file, "brak title lub desc");
  const ids = new Set();
  const svgReferences = [];
  for (const { name, attrs } of tagsIn(source)) {
    if (!svgElements.has(name)) fail(file, `SVG zawiera niedozwolony element: ${name}`);
    if (attrs.has("id")) {
      if (ids.has(attrs.get("id"))) fail(file, `powtórzone SVG id: ${attrs.get("id")}`);
      ids.add(attrs.get("id"));
    }
    if (name === "svg" && (!/^\d+$/.test(attrs.get("width") ?? "") || !/^\d+$/.test(attrs.get("height") ?? "") || !attrs.has("viewbox"))) fail(file, "brak natywnych wymiarów SVG");
    for (const [attribute, value] of attrs) {
      if (/^on/i.test(attribute) || attribute === "style") fail(file, `SVG: aktywny / inline atrybut ${attribute}`);
      if (attribute === "href" || attribute === "xlink:href") {
        if (value.startsWith("#")) svgReferences.push(value.slice(1));
        else if (!/^data:image\/(?:png|jpeg|webp);base64,[a-z0-9+/=]+$/i.test(value) || value.length > 410_000) fail(file, "SVG odwołuje się do zasobu zewnętrznego lub zbyt dużego obrazu");
      }
      for (const match of value.matchAll(/url\(\s*([^)]*)\)/gi)) {
        if (!match[1].startsWith("#")) fail(file, "SVG: zewnętrzny url()");
        else svgReferences.push(match[1].slice(1));
      }
    }
  }
  svgReferences.forEach((id) => { if (!ids.has(id)) fail(file, `SVG: brak lokalnego zasobu ${id}`); });
}
for (const name of brandNames) await checkSvg(path.join(brandRoot, name + ".svg"));
for (const name of uiNames) {
  const target = path.join(root, "assets", "ui", name + ".svg");
  await checkSvg(target);
  try {
    if (!(await readFile(target)).equals(await readFile(path.join(brandRoot, name + ".svg")))) fail(target, "kopia SVG różni się od assets/brand/ui");
  } catch { /* Brak pliku zgłaszany przez checkSvg. */ }
}
// Dodatkowe SVG w public/assets/ui również muszą być pasywne.
for (const file of files.filter((file) => path.dirname(file) === path.join(root, "assets", "ui") && file.endsWith(".svg") && !uiNames.includes(path.basename(file, ".svg")))) await checkSvg(file);
for (const file of files) {
  if (!/\.(html|css|js)$/i.test(file)) continue;
  const buffer = await readFile(file);
  total += buffer.length;
  const source = buffer.toString("utf8");
  if (/\.js$/i.test(file)) {
    for (const match of source.matchAll(/(?:\bfrom\s*|\bimport\s*(?:\(\s*)?)(["'])([^"'\s]+)\1/g)) {
      if (!match[2].startsWith("./")) fail(file, `import musi być lokalny: ${match[2]}`);
      referenceQueue.push({ file, value: match[2] });
    }
  }
  if (/\.css$/i.test(file)) {
    for (const match of source.matchAll(/url\(\s*["']?([^"')\s]+)["']?\s*\)/gi)) referenceQueue.push({ file, value: match[1] });
  }
  if (!/\.html$/i.test(file)) continue;
  const tags = tagsIn(source);
  const ids = new Set();
  let lastHeading = 0;
  let h1Count = 0;
  for (const { name, attrs } of tags) {
    if (attrs.has("id")) {
      const id = attrs.get("id");
      if (ids.has(id)) fail(file, `powtórzone id: ${id}`);
      ids.add(id);
    }
    if (name === "style") fail(file, "inline <style>");
    for (const [attr, value] of attrs) {
      if (attr === "style" || /^on/i.test(attr)) fail(file, `zakazany atrybut: ${attr}`);
      if (attr === "href" || attr === "src") referenceQueue.push({ file, value });
      if (attr === "srcdoc") fail(file, "inline srcdoc");
    }
    if (name === "script" && (!attrs.get("src") || /^(?:[a-z][\w+.-]*:|\/\/)/i.test(attrs.get("src")))) fail(file, "skrypt musi być lokalnym plikiem");
    if (/^h[1-6]$/.test(name)) {
      const level = Number(name[1]);
      if (level === 1) h1Count++;
      if (level > lastHeading + 1) fail(file, `przeskok nagłówków h${lastHeading} → h${level}`);
      lastHeading = level;
    }
    if (name === "img" && !attrs.has("alt")) fail(file, "obraz bez alt");
    if (name === "meta" && attrs.get("property") === "og:image") referenceQueue.push({ file, value: attrs.get("content") ?? "" });
  }
  for (const match of source.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script\s*>/gi)) {
    if (match[1].trim()) fail(file, "inline zawartość <script>");
  }
  if (!tags.some(({ name, attrs }) => name === "html" && attrs.get("lang") === "pl")) fail(file, "brak lang=pl");
  if (!tags.some(({ name, attrs }) => name === "meta" && attrs.get("name") === "description" && attrs.get("content")?.trim())) fail(file, "brak meta description");
  if (h1Count !== 1) fail(file, `liczba h1: ${h1Count}, oczekiwano 1`);
  if (!tags.some(({ name, attrs }) => name === "a" && attrs.get("class")?.split(/\s+/).includes("skip-link"))) fail(file, "brak skip-link");
  documents.set(file, { tags, ids });
}

for (const { file, value } of referenceQueue) {
  references++;
  if (/^(?:https?:|mailto:|tel:|data:|\/\/)/i.test(value)) continue;
  if (!value || /^[a-z][\w+.-]*:/i.test(value)) {
    fail(file, `pusty lub niedozwolony adres: ${value}`);
    continue;
  }
  let url;
  try {
    const base = "https://site.local/" + path.relative(root, file).split(path.sep).join("/");
    url = new URL(value, base);
    const pathname = decodeURIComponent(url.pathname);
    let target = path.resolve(root, "." + pathname);
    const relative = path.relative(root, target);
    if (relative.startsWith("..") || path.isAbsolute(relative)) throw new Error("adres poza public");
    if ((await stat(target)).isDirectory()) target = path.join(target, "index.html");
    if (!(await stat(target)).isFile()) throw new Error("nie jest plikiem");
    if (url.hash && !documents.get(target)?.ids.has(decodeURIComponent(url.hash.slice(1)))) fail(file, `nieistniejąca kotwica: ${value}`);
  } catch (error) {
    fail(file, `nieprawidłowe odwołanie ${value}: ${error.code ?? error.message}`);
  }
}

for (const [file, { tags, ids }] of documents) {
  for (const { attrs } of tags) {
    for (const attribute of ["aria-labelledby", "aria-describedby"]) {
      for (const id of (attrs.get(attribute) ?? "").split(/\s+/).filter(Boolean)) {
        if (!ids.has(id)) fail(file, `nieistniejące ${attribute}: ${id}`);
      }
    }
  }
}
const headers = await readFile(path.join(root, "_headers"), "utf8");
for (const directive of ["default-src 'self'", "script-src 'self'", "style-src 'self' https://fonts.googleapis.com", "img-src 'self' data:", "frame-ancestors 'none'"]) {
  if (!headers.includes(directive)) fail(path.join(root, "_headers"), `brak CSP: ${directive}`);
}
if (/'unsafe-inline'|'unsafe-eval'/.test(headers)) fail(path.join(root, "_headers"), "osłabiona CSP");
if (total > 160_000) errors.push(`Rozmiar ${total} B przekracza 160 000 B`);
if (errors.length) {
  console.error(errors.join("\n"));
  process.exitCode = 1;
} else {
  console.log(`OK: ${documents.size} strony HTML, ${references} odwołań; pliki i kotwice istnieją.`);
  console.log("OK: lang=pl, description, jeden h1 na stronę, hierarchia nagłówków, skip-link, ARIA i alt.");
  console.log("OK: brak inline script/style/handlerów, wyłącznie lokalne skrypty, CSP zachowana.");
  console.log(`OK: ${svgCount} samodzielnych SVG bez skryptów, foreignObject i zewnętrznych zasobów; kopie UI zgodne ze źródłem.`);
  console.log(`OK: HTML+CSS+JS = ${total} B (${(total / 1000).toFixed(1)} KB), limit 160 KB.`);
}
