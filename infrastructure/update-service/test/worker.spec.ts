import { env } from "cloudflare:workers";
import { createExecutionContext, waitOnExecutionContext } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import worker, { isAllowedUpdateAssetPath } from "../src/index";

const IncomingRequest = Request<unknown, IncomingRequestCfProperties>;

async function fetchWorker(path: string, init?: RequestInit): Promise<Response> {
  const request = new IncomingRequest(`https://updates.example.test${path}`, init);
  const context = createExecutionContext();
  const response = await worker.fetch(request, env, context);
  await waitOnExecutionContext(context);
  return response;
}

describe("GameShift update service", () => {
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
