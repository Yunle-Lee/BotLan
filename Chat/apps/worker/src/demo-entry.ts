import { createWorkerServer } from "./server.ts";

// Launched only by pnpm dev:demo, with an explicit local environment and isolated profiles.
const worker = await createWorkerServer({
  token: process.env.WORKER_TOKEN ?? "",
  dataDir: process.env.WORKER_DATA_DIR ?? "artifacts/demo/browser-profiles",
  maxSessions: 6,
  idleTimeoutMs: 30 * 60_000,
});
worker.server.listen(Number(process.env.PORT ?? "8791"), "127.0.0.1", () => {
  console.log(`Demo browser worker ready on port ${process.env.PORT ?? "8791"}`);
});
let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  const deadline = setTimeout(() => process.exit(1), 10_000);
  deadline.unref();
  await worker.close();
  process.exit(0);
}
process.on("SIGTERM", () => void stop());
process.on("SIGINT", () => void stop());
