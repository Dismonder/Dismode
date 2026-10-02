import { readdir, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

// Bez zależności i rasteryzacji: przeglądarka otwiera SVG w ich natywnej skali.
const directory = fileURLToPath(new URL("../assets/brand/ui/", import.meta.url));
const escape = (value) => value.replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));
const names = (await readdir(directory)).filter((name) => name.endsWith(".svg")).sort();
const items = await Promise.all(names.map(async (name) => {
  const source = await readFile(path.join(directory, name), "utf8");
  const width = source.match(/<svg\b[^>]*\bwidth="(\d+)"/)?.[1];
  const height = source.match(/<svg\b[^>]*\bheight="(\d+)"/)?.[1];
  if (!width || !height) throw new Error(`Brak wymiarów SVG: ${name}`);
  return `<section><h2><a href="${escape(name)}">${escape(name)}</a> <small>${width} × ${height}</small></h2><img src="${escape(name)}" alt="${escape(name)} — rekonstrukcja interfejsu" width="${width}" height="${height}"></section>`;
}));
await writeFile(path.join(directory, "index.html"), `<!doctype html>
<html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dismode — grafiki UI w skali 1:1</title>
<style>html{color-scheme:dark}body{margin:24px;background:#070b0e;color:#dde2e5;font:16px "Segoe UI Variable","Segoe UI",sans-serif}a{color:#00d7e2}h1{font-size:28px}h2{font-size:18px}small{color:#9da5aa;font-weight:400}section{margin-block:32px}img{display:block;max-width:none}p{max-width:80ch}a:focus-visible{outline:2px solid #00d7e2;outline-offset:4px}</style></head>
<body><h1>Dismode — grafiki UI</h1><p>Natywne wymiary, bez skalowania obrazów. Otwórz osobny SVG, aby wyrenderować PNG. Dane na ilustracjach są przykładowe, nie są benchmarkiem.</p>${items.join("\n")}</body></html>
`, "utf8");
console.log(`OK: assets/brand/ui/index.html — ${names.length} grafik w natywnej skali.`);
