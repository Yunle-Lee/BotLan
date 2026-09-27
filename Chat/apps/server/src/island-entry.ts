import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { createInterface } from "node:readline";
import { serve } from "@hono/node-server";
import { serveStatic } from "@hono/node-server/serve-static";
import { Hono } from "hono";
import { bodyLimit } from "hono/body-limit";
import { z } from "zod";
import { createApp } from "./app.ts";
import { readConfig, type IslandBotModel } from "./config.ts";
import { createStore } from "./db.ts";

const hostKey = process.env.ISLAND_HOST_KEY;
if (!hostKey || hostKey.length < 32) throw new Error("IslandUI host authentication is required.");
const parentId = Number(process.env.ISLAND_PARENT_PID);
if (!Number.isSafeInteger(parentId) || parentId < 1) throw new Error("IslandUI parent PID is required.");
const dataDir = resolve(process.env.ISLAND_DATA_DIR || ".island-data");
await mkdir(dataDir, { recursive: true });
const encryptionPath = join(dataDir, "encryption-key");
let encryptionKey: string;
try { encryptionKey = await readFile(encryptionPath, "utf8"); }
catch (error) {
  if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) throw error;
  encryptionKey = randomBytes(32).toString("base64");
  await writeFile(encryptionPath, encryptionKey, { flag: "wx", mode: 0o600 });
}
const models = new Map<string, IslandBotModel>();
const config = {
  ...readConfig(),
  mode: "live" as const,
  host: "127.0.0.1", port: 0, publicUrl: "http://127.0.0.1:0",
  dataDir, databaseUrl: undefined, accessKey: hostKey, encryptionKey,
  agentBackend: "model" as const, model: "openai/unconfigured",
  resolveIslandBot: (owner: string) => models.get(owner),
  allowedOrigins: ["https://island-chat.local"],
};
const db = await createStore({ dataDir: join(dataDir, "postgres") });
await db.recoverInterruptedActions();
const services = await createApp(db, config);
const app = new Hono();
const webRoot = resolve("dist/island");
app.get("/", serveStatic({ path: join(webRoot, "index.html") }));
app.get("/index.html", serveStatic({ path: join(webRoot, "index.html") }));
app.get("/_expo/*", serveStatic({ root: webRoot }));
app.get("/assets/*", serveStatic({ root: webRoot }));
let workerStarted = false;
const digest = (text: string) => createHash("sha256").update(text).digest();
app.use("/island/*", bodyLimit({ maxSize: 1024 * 1024 }));
app.post("/island/session", async (c) => {
  // Credentials never enter the webpage or URLs; only the owning native process
  // knows this per-launch secret. Browser-originated registration is not allowed.
  if (c.req.header("origin") || !timingSafeEqual(digest(c.req.header("x-island-host-key") || ""), digest(hostKey)))
    return c.json({ error: "Forbidden" }, 403);
  const parsed = z.object({
    selectedId: z.string(),
    bots: z.array(z.object({
      id: z.string().min(1).max(160), name: z.string().max(500),
      model: z.string().min(1).max(500),
      baseUrl: z.url().refine((url) => ["http:", "https:"].includes(new URL(url).protocol)),
      apiKey: z.string().max(16384),
    })).min(1).max(1000),
  }).safeParse(await c.req.json());
  if (!parsed.success) return c.json({ error: "Invalid Bot configuration" }, 400);
  const { bots, selectedId } = parsed.data;
  if (!bots.some((bot) => bot.id === selectedId)) return c.json({ error: "Bot not found" }, 404);
  models.clear();
  for (const bot of bots)
    models.set(`island:${bot.id}`, { model: bot.model, baseUrl: bot.baseUrl.replace(/\/$/, ""), apiKey: bot.apiKey });
  for (const bot of bots) {
    const owner = `island:${bot.id}`;
    await services.agent.ensure(owner);
    const identity = await db.get<Record<string, unknown>>(owner, "agent-settings", "identity");
    await db.put(owner, "agent-settings", { ...identity, id: "identity", name: bot.name });
  }
  if (!workerStarted && config.taskWorkerEnabled !== false) {
    services.agent.start();
    workerStarted = true;
  }
  return c.json(await services.auth.session(hostKey, `island:${selectedId}`));
});
// Keep the standalone session route from creating a shared local-user workspace.
app.post("/api/session", (c) => c.json({ error: "Open a Bot from IslandUI." }, 403));
app.route("/", services.app);
const server = serve({ fetch: app.fetch, port: 0, hostname: "127.0.0.1" }, (address) => {
  config.port = address.port;
  config.publicUrl = `http://127.0.0.1:${address.port}`;
  console.log(`ISLAND_READY ${config.publicUrl}`);
});
let stopping = false;
async function shutdown() {
  if (stopping) return;
  stopping = true;
  clearInterval(parentWatch);
  server.close();
  const deadline = setTimeout(() => process.exit(1), 8000);
  deadline.unref();
  await services.agent.stop();
  await db.close();
  // Let libuv finish closing the WASM database's native async handles. Forcing
  // process.exit() here can assert in Node/Windows while a close is still queued.
  clearTimeout(deadline);
  input.close();
  process.stdin.destroy();
  if ("closeAllConnections" in server) server.closeAllConnections();
  process.exitCode = 0;
}
const parentWatch = setInterval(() => {
  try { process.kill(parentId, 0); } catch { void shutdown(); }
}, 2000);
parentWatch.unref();
const input = createInterface({ input: process.stdin });
input.on("line", (line) => { if (line === "shutdown") void shutdown(); });
input.on("close", () => { void shutdown(); });
process.on("SIGINT", () => { void shutdown(); });
process.on("SIGTERM", () => { void shutdown(); });
