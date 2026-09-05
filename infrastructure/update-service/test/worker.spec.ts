import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it, vi } from "vitest";
import worker, { isAllowedUpdateAssetPath } from "../src/index";

const IncomingRequest = Request<unknown, IncomingRequestCfProperties>;

async function fetchWorker(path: string, init?: RequestInit<IncomingRequestCfProperties>): Promise<Response> {
  const request = new IncomingRequest(`https://updates.example.test${path}`, init);
  return worker.fetch(request, env);
}

describe("GameShift update service", () => {
  afterEach(() => vi.restoreAllMocks());

  it("serves the staged channel manifest through the real asset binding", async () => {
    const response = await fetchWorker("/v1/channels/preview/manifest.json");
    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("public, max-age=300, must-revalidate");
    const manifest = await response.json<{ schemaVersion: number; channel: string; signature: string }>();
    expect(manifest.schemaVersion).toBe(1);
    expect(manifest.channel).toBe("preview");
    expect(manifest.signature.length).toBeGreaterThan(0);
    const head = await fetchWorker("/v1/channels/preview/manifest.json", { method: "HEAD" });
    expect(head.status).toBe(200);
    expect(await head.text()).toBe("");
    expect(head.headers.get("etag")).toBe(response.headers.get("etag"));
  });

  it.each(["/health", "/missing", "/v1/channels/preview/manifest.json"])(
    "returns no body for HEAD %s, including errors",
    async (path) => {
      if (path.includes("manifest")) {
        vi.spyOn(env.ASSETS, "fetch").mockResolvedValueOnce(new Response(null, { status: 404 }));
      }
      const response = await fetchWorker(path, { method: "HEAD" });
      expect(await response.text()).toBe("");
      expect(response.headers.get("cache-control")).toBe("no-store");
    },
  );

  it.each([500, 503, 403, 302])("does not cache asset failure %s as an immutable package", async (status) => {
    vi.spyOn(env.ASSETS, "fetch").mockResolvedValueOnce(new Response("upstream failure", { status }));
    const response = await fetchWorker("/v1/packages/0.4.0/GameShift-Setup-0.4.0-win-x64.part-0001.bin");
    expect(response.status).toBe(502);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.json()).toEqual({ error: "asset_unavailable" });
  });

  it.each([
    ["/v1/channels/preview/manifest.json", "public, max-age=300, must-revalidate"],
    ["/v1/packages/0.4.0/GameShift-Setup-0.4.0-win-x64.part-0001.bin", "public, max-age=31536000, immutable"],
  ])("preserves successful assets and their cache policy: %s", async (path, cachePolicy) => {
    vi.spyOn(env.ASSETS, "fetch").mockResolvedValueOnce(new Response("verified-content", {
      headers: { ETag: '"release-hash"' },
    }));
    const response = await fetchWorker(path);
    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe(cachePolicy);
    expect(response.headers.get("etag")).toBe('"release-hash"');
    expect(response.headers.get("x-content-type-options")).toBe("nosniff");
    expect(await response.text()).toBe("verified-content");
  });

  it("preserves 304 and partial-content responses", async () => {
    const path = "/v1/packages/0.4.0/GameShift-Setup-0.4.0-win-x64.part-0001.bin";
    vi.spyOn(env.ASSETS, "fetch")
      .mockResolvedValueOnce(new Response(null, { status: 304 }))
      .mockResolvedValueOnce(new Response("part", { status: 206, headers: { "Content-Range": "bytes 0-3/100" } }));
    const unchanged = await fetchWorker(path, { headers: { "If-None-Match": '"release-hash"' } });
    expect(unchanged.status).toBe(304);
    expect(await unchanged.text()).toBe("");
    const partial = await fetchWorker(path, { headers: { Range: "bytes=0-3" } });
    expect(partial.status).toBe(206);
    expect(partial.headers.get("content-range")).toBe("bytes 0-3/100");
    expect(await partial.text()).toBe("part");
  });

  it("does not cache a rejected range and preserves its total size", async () => {
    vi.spyOn(env.ASSETS, "fetch").mockResolvedValueOnce(new Response(null, {
      status: 416, headers: { "Content-Range": "bytes */100" },
    }));
    const response = await fetchWorker("/v1/packages/0.4.0/GameShift-Setup-0.4.0-win-x64.part-0001.bin");
    expect(response.status).toBe(416);
    expect(response.headers.get("content-range")).toBe("bytes */100");
    expect(response.headers.get("cache-control")).toBe("no-store");
  });

  it("returns a bodyless uncached error when a HEAD asset request throws", async () => {
    vi.spyOn(env.ASSETS, "fetch").mockRejectedValueOnce(new Error("asset binding unavailable"));
    const response = await fetchWorker("/v1/channels/preview/manifest.json", { method: "HEAD" });
    expect(response.status).toBe(500);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.text()).toBe("");
  });

  it("publishes a no-store health endpoint without user telemetry", async () => {
    const response = await fetchWorker("/health");
    const body = await response.json<{ status: string; service: string }>();

    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(body).toEqual({
      status: "ok",
      service: "gameshift-update-service",
      environment: "development",
      timestampUtc: expect.any(String),
    });
  });

  it("allow-lists only public update channels", () => {
    expect(isAllowedUpdateAssetPath("/v1/channels/preview/manifest.json")).toBe(true);
    expect(isAllowedUpdateAssetPath("/v1/channels/stable/manifest.json")).toBe(true);
    expect(isAllowedUpdateAssetPath("/v1/channels/internal/manifest.json")).toBe(false);
  });

  it("allow-lists only version-matched installer chunks", () => {
    expect(
      isAllowedUpdateAssetPath(
        "/v1/packages/0.1.1/GameShift-Setup-0.1.1-win-x64.part-0001.bin",
      ),
    ).toBe(true);
    expect(
      isAllowedUpdateAssetPath(
        "/v1/packages/0.1.1/GameShift-Setup-0.1.2-win-x64.part-0001.bin",
      ),
    ).toBe(false);
    expect(isAllowedUpdateAssetPath("/v1/packages/0.1.1/tool.exe")).toBe(false);
  });

  it("rejects arbitrary object keys and all write methods", async () => {
    const traversal = await fetchWorker("/v1/packages/0.1.1/..%2Fsecret.pem");
    const wrongName = await fetchWorker("/v1/packages/0.1.1/tool.exe");
    const write = await fetchWorker("/v1/channels/preview/manifest.json", {
      method: "PUT",
      body: "malicious",
    });

    expect(traversal.status).toBe(404);
    expect(wrongName.status).toBe(404);
    expect(write.status).toBe(405);
    expect(write.headers.get("allow")).toBe("GET, HEAD");
  });

  it("does not expose arbitrary static assets", async () => {
    const response = await fetchWorker("/.git/config");
    expect(response.status).toBe(404);
  });
});
