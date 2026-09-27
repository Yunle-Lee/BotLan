import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { intelligenceKeyRequiredMessage, required } from "../config.ts";
import { createStore } from "../db.ts";
import { createDemoModel, demoModel } from "./model.ts";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const intelligenceApiKey = required("CPK_INTELLIGENCE_API_KEY", intelligenceKeyRequiredMessage);
const dataDir = join(root, "artifacts", "demo", "api");
const runtimeDir = join(root, "artifacts", "demo", "runtime");
const port = Number(process.env.DEMO_API_PORT ?? "8788");
if (!Number.isInteger(port) || port < 1 || port > 65535)
  throw new Error("DEMO_API_PORT must be an integer from 1 to 65535");
const publicUrl = `http://127.0.0.1:${port}`;
const workerUrl = process.env.DEMO_WORKER_URL ?? "http://127.0.0.1:8791";
const workerToken = process.env.DEMO_WORKER_TOKEN ?? "agent-local-demo-worker-token-2026";
const sessionToken = "agent-local-demo-session-token-2026";
const worker = new URL(workerUrl);
if (!(["127.0.0.1", "localhost", "[::1]"].includes(worker.hostname) && worker.protocol === "http:"))
  throw new Error("DEMO_WORKER_URL must point to a local HTTP browser worker");
if (workerToken.length < 32) throw new Error("DEMO_WORKER_TOKEN must be at least 32 characters");

await mkdir(runtimeDir, { recursive: true });
// A fresh cwd prevents config.ts from loading the project's private .env.
const cwd = await mkdtemp(join(runtimeDir, "process-"));
const db = await createStore({ dataDir: join(dataDir, "postgres") });
try {
  await db.put("system", "sessions", {
    id: createHash("sha256").update(sessionToken).digest("hex"),
    owner: "local-user",
    expiresAt: Date.now() + 24 * 60 * 60 * 1000,
  });
} finally {
  await db.close();
}
const mock = createDemoModel({
  latency: Number(process.env.DEMO_MODEL_CHUNK_DELAY_MS ?? "80"),
  firstByteDelay: Number(process.env.DEMO_MODEL_FIRST_BYTE_DELAY_MS ?? "1500"),
});
await mock.start();
const browser = process.env.DEMO_WORKER_URL
  ? undefined
  : spawn(
      process.execPath,
      ["--experimental-strip-types", join(root, "apps", "worker", "src", "demo-entry.ts")],
      {
        cwd,
        stdio: "inherit",
        env: {
          PATH: process.env.PATH,
          HOME: process.env.HOME,
          TMPDIR: process.env.TMPDIR,
          PORT: "8791",
          WORKER_TOKEN: workerToken,
          WORKER_DATA_DIR: join(root, "artifacts", "demo", "browser-profiles"),
        },
      },
    );
// Start the normal API with an explicit environment: never copy real provider, Google, or DB keys.
const api = spawn(
  process.execPath,
  ["--import", import.meta.resolve("tsx"), fileURLToPath(new URL("../index.ts", import.meta.url))],
  {
    cwd,
    stdio: "inherit",
    env: {
      PATH: process.env.PATH,
      TZ: process.env.TZ ?? "America/Los_Angeles",
      WORKSPACE_MODE: "sample",
      AGENT_BACKEND: "model",
      MODEL: demoModel,
      OPENAI_API_KEY: "local-aimock-demo-only",
      OPENAI_BASE_URL: `${mock.url}/v1`,
      PORT: String(port),
      HOST: "127.0.0.1",
      PUBLIC_API_URL: publicUrl,
      DATA_DIR: dataDir,
      BROWSER_WORKER_URL: workerUrl,
      WORKER_TOKEN: workerToken,
      TASK_WORKER_ENABLED: "true",
      COMPUTER_ENABLED: "false",
      ALLOWED_ORIGINS:
        process.env.DEMO_ALLOWED_ORIGINS ?? "http://localhost:8081,http://127.0.0.1:8081",
      DO_NOT_TRACK: "1",
      COPILOTKIT_TELEMETRY_DISABLED: "true",
      CPK_INTELLIGENCE_API_KEY: intelligenceApiKey,
    },
  },
);
console.log(
  "AI Studio recording demo: AI Mock scripts the model; browser visits use the real worker.",
);
console.log(`Demo API: ${publicUrl}; browser worker: ${workerUrl}`);
console.log(`Isolated demo data: ${dataDir}`);
console.log(`Start the app with EXPO_PUBLIC_API_URL=${publicUrl} pnpm dev:web`);
console.log(
  "The demo's public local session token is documented in apps/server/src/demo/entry.ts.",
);

let stopping = false;
const stop = () => {
  if (stopping) return;
  stopping = true;
  api.kill("SIGTERM");
  browser?.kill("SIGTERM");
  const deadline = setTimeout(() => {
    api.kill("SIGKILL");
    browser?.kill("SIGKILL");
  }, 10_000);
  deadline.unref();
};
process.on("SIGINT", stop);
process.on("SIGTERM", stop);
api.on("error", (error) => {
  console.error(`Demo API could not start: ${error.message}`);
  process.exitCode = 1;
});
browser?.on("error", (error) => {
  console.error(`Demo browser worker could not start: ${error.message}`);
  process.exitCode = 1;
  stop();
});
browser?.on("close", (code) => {
  if (!stopping) {
    console.error(`Demo browser worker stopped with code ${code ?? 1}`);
    process.exitCode = 1;
    stop();
  }
});
api.on("close", async (code) => {
  browser?.kill("SIGTERM");
  await mock.stop();
  await rm(cwd, { recursive: true, force: true });
  process.exitCode ||= stopping ? 0 : (code ?? 1);
});
