const ALLOWED_CHANNELS = new Set(["preview", "stable"]);
const VERSION_PATTERN = /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/;
const PACKAGE_CHUNK_PATTERN = /^GameShift-Setup-[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?-win-x64\.part-[0-9]{4}\.bin$/;

const securityHeaders = Object.freeze({
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Content-Security-Policy":
    "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'",
});

function jsonResponse(body: unknown, status = 200): Response {
  const headers = new Headers(securityHeaders);
  headers.set("Content-Type", "application/json; charset=utf-8");
  headers.set("Cache-Control", "no-store");
  return Response.json(body, { status, headers });
}

function htmlResponse(title: string, content: string): Response {
  const headers = new Headers(securityHeaders);
  headers.set("Content-Type", "text/html; charset=utf-8");
  headers.set("Cache-Control", "public, max-age=3600");
  const body = `<!doctype html>
<html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${title} — GameShift</title><style>
:root{color-scheme:dark;font-family:Segoe UI,system-ui,sans-serif;background:#090d18;color:#f5f7ff}
body{margin:0;min-height:100vh;background:radial-gradient(circle at 15% 10%,#153a5a 0,transparent 32rem),#090d18}
main{max-width:960px;margin:auto;padding:48px 24px}.hero,.card{border:1px solid #27344d;background:#121a2aee;border-radius:28px;padding:28px;box-shadow:0 18px 60px #0007}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:16px;margin-top:16px}.card{border-radius:20px;padding:20px}h1{font-size:clamp(2rem,6vw,4rem);margin:.2em 0}h2{color:#55d9ff}p,li{color:#c6cede;line-height:1.65}a{color:#69e4ff}code{color:#9af5cd}nav{display:flex;gap:18px;flex-wrap:wrap;margin-bottom:24px}
</style></head><body><main><nav><a href="/">Produkt</a><a href="/support">Pomoc</a><a href="/privacy">Prywatność</a></nav>${content}</main></body></html>`;
  return new Response(body, { headers });
}

function productPage(productName: string): Response {
  return htmlResponse(
    "Produkt",
    `<section class="hero"><p>TECHNICAL PREVIEW</p><h1>${productName}</h1><p>Lokalny, odwracalny optymalizator sesji gry dla komputerów stacjonarnych z Windows 11.</p></section>
<section class="grid"><article class="card"><h2>Pomiar</h2><p>Rzeczywisty frametime i FPS pochodzą z PresentMon. Brak danych nie jest zastępowany wynikiem szacowanym.</p></article><article class="card"><h2>Bezpieczeństwo</h2><p>Zmiany procesu są weryfikowane i journalowane, a aktualizacja jest blokowana do zakończenia recovery.</p></article><article class="card"><h2>Aktualizacje</h2><p>Pakiety i manifesty są weryfikowane kryptograficznie przed uruchomieniem instalatora.</p></article></section>`,
  );
}

function supportPage(): Response {
  return htmlResponse(
    "Pomoc",
    `<section class="hero"><h1>Pomoc GameShift</h1><p>Przed aktualizacją lub odinstalowaniem zakończ aktywną sesję GameShift i użyj funkcji <strong>Przywróć wszystko teraz</strong>. Nie usuwaj ręcznie journalu odzyskiwania.</p><p>Wydanie badawczo-rozwojowe nie ma jeszcze komercyjnego centrum pomocy. Raport diagnostyczny pozostaje lokalny, dopóki użytkownik sam go nie wyeksportuje.</p></section>`,
  );
}

function privacyPage(): Response {
  return htmlResponse(
    "Prywatność",
    `<section class="hero"><h1>Prywatność</h1><p>GameShift analizuje lokalnie procesy, gry, wykorzystanie zasobów i wyniki sesji. Dane te nie są automatycznie wysyłane na serwer aktualizacji.</p><p>Sprawdzenie aktualizacji wysyła zwykłe żądanie HTTPS. Cloudflare może przetworzyć standardowe metadane sieciowe, takie jak adres IP, czas, ścieżka i User-Agent. GameShift nie dołącza listy gier, procesów, sprzętu ani wyników FPS.</p><p>Automatyczne sprawdzanie można wyłączyć; ręczne sprawdzenie pozostaje dostępne.</p></section>`,
  );
}

export function isAllowedUpdateAssetPath(pathname: string): boolean {
  const channelMatch = /^\/v1\/channels\/([^/]+)\/manifest\.json$/.exec(pathname);
  if (channelMatch) {
    const channel = channelMatch[1];
    return Boolean(channel && ALLOWED_CHANNELS.has(channel));
  }

  const packageMatch = /^\/v1\/packages\/([^/]+)\/([^/]+)$/.exec(pathname);
  if (!packageMatch) {
    return false;
  }

  const version = packageMatch[1];
  const fileName = packageMatch[2];
  if (
    !version ||
    !fileName ||
    !VERSION_PATTERN.test(version) ||
    !PACKAGE_CHUNK_PATTERN.test(fileName) ||
    !fileName.startsWith(`GameShift-Setup-${version}-win-x64.part-`)
  ) {
    return false;
  }

  return true;
}

async function serveUpdateAsset(request: Request, env: Env): Promise<Response> {
  const assetResponse = await env.ASSETS.fetch(request);
  if (assetResponse.status === 404) {
    return jsonResponse({ error: "not_found" }, 404);
  }

  const headers = new Headers(assetResponse.headers);
  for (const [name, value] of Object.entries(securityHeaders)) {
    headers.set(name, value);
  }
  const pathname = new URL(request.url).pathname;
  headers.set(
    "Cache-Control",
    pathname.endsWith("/manifest.json")
      ? "public, max-age=300, must-revalidate"
      : "public, max-age=31536000, immutable",
  );
  return new Response(request.method === "HEAD" ? null : assetResponse.body, {
    status: assetResponse.status,
    statusText: assetResponse.statusText,
    headers,
  });
}

async function handleRequest(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);

  if (request.method !== "GET" && request.method !== "HEAD") {
    const response = jsonResponse({ error: "method_not_allowed" }, 405);
    response.headers.set("Allow", "GET, HEAD");
    return response;
  }

  if (url.pathname === "/health") {
    return jsonResponse({
      status: "ok",
      service: "gameshift-update-service",
      environment: env.ENVIRONMENT,
      timestampUtc: new Date().toISOString(),
    });
  }
  if (url.pathname === "/") {
    return request.method === "HEAD" ? new Response(null, productPage(env.PRODUCT_NAME)) : productPage(env.PRODUCT_NAME);
  }
  if (url.pathname === "/support") {
    return request.method === "HEAD" ? new Response(null, supportPage()) : supportPage();
  }
  if (url.pathname === "/privacy") {
    return request.method === "HEAD" ? new Response(null, privacyPage()) : privacyPage();
  }

  if (!isAllowedUpdateAssetPath(url.pathname)) {
    return jsonResponse({ error: "not_found" }, 404);
  }
  return serveUpdateAsset(request, env);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const path = new URL(request.url).pathname;
    try {
      const response = await handleRequest(request, env);
      console.log(JSON.stringify({ message: "request", method: request.method, path, status: response.status }));
      return response;
    } catch (error) {
      console.error(
        JSON.stringify({
          message: "request_failed",
          method: request.method,
          path,
          error: error instanceof Error ? error.message : "unknown_error",
        }),
      );
      return jsonResponse({ error: "internal_server_error" }, 500);
    }
  },
} satisfies ExportedHandler<Env>;
