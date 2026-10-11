import assert from "node:assert/strict";
import { test } from "node:test";
import type { Config, IslandBotModel } from "../apps/server/src/config.ts";
import { SparkService } from "../apps/server/src/spark.ts";

const bot: IslandBotModel = { model: "jev-step", baseUrl: "http://127.0.0.1:18091/v1", apiKey: "bl_key" };

function setup() {
  const calls: { url: string; auth: string | null; body?: unknown }[] = [];
  const fetcher = (async (url: string | URL, init?: RequestInit) => {
    const headers = new Headers(init?.headers);
    calls.push({ url: String(url), auth: headers.get("authorization"),
      body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const path = new URL(String(url)).pathname;
    const json = (value: unknown, status = 200) =>
      new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
    if (path === "/health") return json({ ok: true, botlan: 1 });
    if (path === "/botlan/exec") return json({ exit_code: 0, output: "GB10\n", timed_out: false, ms: 12 });
    return json({ error: "not found" }, 404);
  }) as typeof fetch;
  const config = { resolveIslandBot: (owner: string) => (owner === "island:a" ? bot : undefined) } as unknown as Config;
  return { spark: new SparkService(config, fetcher), calls };
}

const waitFor = async (check: () => boolean) => {
  for (let i = 0; i < 100 && !check(); i++) await new Promise((r) => setTimeout(r, 5));
  assert.ok(check(), "condition not reached");
};

test("a Spark command runs only after the owner approves it", async () => {
  const { spark, calls } = setup();
  assert.equal(await spark.isSpark("island:a"), true);
  assert.equal(await spark.isSpark("island:b"), false);
  const cloud = new SparkService({ resolveIslandBot: () => ({ ...bot, baseUrl: "https://api.example.com/v1" }) } as unknown as Config,
    (async () => { throw new Error("public hosts must not be probed"); }) as typeof fetch);
  assert.equal(await cloud.isSpark("island:a"), false);
  const running = spark.run("island:a", { command: "nvidia-smi -L", reason: "check GPU" });
  await waitFor(() => spark.approvals("island:a").length === 1);
  assert.equal(calls.filter((c) => c.url.endsWith("/botlan/exec")).length, 0, "nothing runs before approval");
  const [pending] = spark.approvals("island:a");
  assert.equal(pending.command, "nvidia-smi -L");
  assert.deepEqual(spark.approvals("island:b"), [], "other Bots do not see the approval");
  assert.throws(() => spark.decide("island:b", pending.id, true), /no longer pending/);
  spark.decide("island:a", pending.id, true);
  const result = await running;
  assert.equal(result.ran, true);
  assert.equal(result.exit_code, 0);
  const exec = calls.find((c) => c.url.endsWith("/botlan/exec"));
  assert.equal(exec?.auth, "Bearer bl_key");
  assert.deepEqual(exec?.body, { command: "nvidia-smi -L" });
  assert.deepEqual(spark.approvals("island:a"), []);
});

test("a new zone's key is held for one host claim and never returned to the page", async () => {
  const fetcher = (async (url: string | URL) => {
    const path = new URL(String(url)).pathname;
    if (path === "/botlan/zones")
      return new Response(JSON.stringify({ zone: { id: "zvision", name: "Vision" }, api_key: "blz_secret" }), { status: 201 });
    return new Response("{}", { status: 404 });
  }) as typeof fetch;
  const config = { resolveIslandBot: () => bot } as unknown as Config;
  const spark = new SparkService(config, fetcher);
  const created = await spark.createZone("island:a", { name: "Vision", mem_gb: 8, cpu_pct: 200 });
  assert.doesNotMatch(JSON.stringify(created), /blz_secret/);
  assert.deepEqual(spark.claim(created.claimId), { name: "Vision", baseUrl: bot.baseUrl, model: bot.model, apiKey: "blz_secret" });
  assert.equal(spark.claim(created.claimId), undefined, "a claim is single-use");
});

test("a denied or aborted Spark command never reaches the gateway", async () => {
  const { spark, calls } = setup();
  const denied = spark.run("island:a", { command: "rm -rf ~/x", reason: "cleanup" });
  await waitFor(() => spark.approvals("island:a").length === 1);
  spark.decide("island:a", spark.approvals("island:a")[0].id, false);
  assert.equal((await denied).ran, false);
  const abort = new AbortController();
  const aborted = spark.run("island:a", { command: "uptime", reason: "load" }, abort.signal);
  await waitFor(() => spark.approvals("island:a").length === 1);
  abort.abort();
  assert.equal((await aborted).ran, false);
  assert.equal(calls.filter((c) => c.url.endsWith("/botlan/exec")).length, 0);
});
