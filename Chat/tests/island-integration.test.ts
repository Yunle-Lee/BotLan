import assert from "node:assert/strict";
import { test } from "node:test";
import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { mkdtemp, mkdir } from "node:fs/promises";
import { resolve, join } from "node:path";
import { randomBytes } from "node:crypto";
import { once } from "node:events";

test("embedded Bots isolate credentials, conversations and runtime threads", { timeout: 120000 }, async () => {
  const fixtures = resolve("../ProjectSupport/ChatIntegration/TestData");
  await mkdir(fixtures, { recursive: true });
  const dataDir = await mkdtemp(join(fixtures, "server-"));
  const calls: { key: string; model: string }[] = [];
  const modelServer = createServer(async (req, res) => {
    const chunks = [];
    for await (const part of req) chunks.push(part);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    calls.push({ key: req.headers.authorization || "", model: body.model });
    res.writeHead(200, { "Content-Type": "text/event-stream" });
    for (const choice of [
      { index: 0, delta: { role: "assistant", content: `reply-${body.model}` }, finish_reason: null },
      { index: 0, delta: {}, finish_reason: "stop" },
    ]) res.write(`data: ${JSON.stringify({ id: "test", object: "chat.completion.chunk", created: 1, model: body.model, choices: [choice] })}\n\n`);
    res.end("data: [DONE]\n\n");
  });
  modelServer.listen(0, "127.0.0.1");
  await once(modelServer, "listening");
  const baseUrl = `http://127.0.0.1:${(modelServer.address() as { port: number }).port}/v1`;
  const hostKey = randomBytes(32).toString("hex");
  const child = spawn(process.execPath, [process.env.ISLAND_TEST_BUNDLED_SERVER === "1"
    ? "dist/island-server/island-server.mjs" : "dist/island-server/apps/server/src/island-entry.js"], {
    cwd: process.cwd(), windowsHide: true,
    env: { ...process.env, ISLAND_HOST_KEY: hostKey, ISLAND_PARENT_PID: String(process.pid),
      ISLAND_DATA_DIR: dataDir, TASK_WORKER_ENABLED: "false", DO_NOT_TRACK: "1", COPILOTKIT_TELEMETRY_DISABLED: "true" },
    stdio: ["pipe", "pipe", "pipe"],
  });
  let errors = "";
  child.stderr.on("data", (chunk) => { errors = (errors + chunk.toString()).slice(-6000); });
  try {
    const address = await new Promise<string>((resolveReady, reject) => {
      let output = "";
      const timeout = setTimeout(() => reject(new Error("Backend readiness timed out: " + errors)), 75000);
      child.stdout.on("data", (chunk) => {
        output += chunk.toString();
        const match = output.match(/ISLAND_READY (http:\/\/127\.0\.0\.1:\d+)/);
        if (match) { clearTimeout(timeout); resolveReady(match[1]); }
      });
      child.once("exit", (code) => { clearTimeout(timeout); reject(new Error(`Backend exit ${code}: ${errors}`)); });
    });
    const bots = ["A", "B"].map((id) => ({ id, name: `Fixture ${id}`, model: `model-${id}`, baseUrl, apiKey: `fixture-key-${id}` }));
    async function session(id: string) {
      const response = await fetch(address + "/island/session", { method: "POST",
        headers: { "content-type": "application/json", "x-island-host-key": hostKey },
        body: JSON.stringify({ bots, selectedId: id }) });
      assert.equal(response.status, 200, await response.clone().text());
      return (await response.json() as { token: string }).token;
    }
    const tokenA = await session("A");
    const tokenB = await session("B");
    const request = (token: string, path: string, body?: unknown, method = "POST") => fetch(address + path, {
      method: body === undefined ? "GET" : method,
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    assert.equal((await fetch(address + "/index.html")).status, 200);
    assert.equal((await fetch(address + "/island/session", { method: "POST", body: "{}" })).status, 403);
    assert.equal((await fetch(address + "/island/session", { method: "POST", headers: { origin: address, "x-island-host-key": hostKey }, body: "{}" })).status, 403);
    const messages = [{ id: "a-message", role: "user", content: "A private conversation" }];
    assert.equal((await request(tokenA, "/api/conversation", { messages }, "PUT")).status, 200);
    assert.deepEqual((await (await request(tokenB, "/api/conversation")).json() as { messages: unknown[] }).messages, []);
    assert.deepEqual((await (await request(tokenA, "/api/conversation")).json() as { messages: unknown[] }).messages, messages);
    assert.equal((await (await request(tokenA, "/api/agent")).json() as { identity: { name: string } }).identity.name, "Fixture A");
    assert.equal((await (await request(tokenB, "/api/agent")).json() as { identity: { name: string } }).identity.name, "Fixture B");
    const run = (id: string) => ({ threadId: `island:${id}:local-main`, runId: `run-${id}`,
      messages: [{ id: `message-${id}`, role: "user", content: "reply briefly" }], state: {}, tools: [], context: [], forwardedProps: {} });
    assert.equal((await request(tokenA, "/api/copilotkit/agent/default/run", run("B"))).status, 403);
    for (const [id, token] of [["A", tokenA], ["B", tokenB]]) {
      const response = await request(token, "/api/copilotkit/agent/default/run", run(id));
      assert.equal(response.status, 200, await response.clone().text());
      const events = await response.text();
      assert.match(events, new RegExp(`reply-model-${id}`), events);
      assert.doesNotMatch(events, /RUN_ERROR/);
    }
    assert.deepEqual(calls, [{ key: "Bearer fixture-key-A", model: "model-A" }, { key: "Bearer fixture-key-B", model: "model-B" }]);
    console.log("PASS: shared page, isolated history/identity/model/key, blocked cross-Bot run, real SSE replies from LOCAL mock only");
  } finally {
    if (child.exitCode === null) {
      const exited = once(child, "exit");
      child.stdin.write("shutdown\n");
      const force = setTimeout(() => child.kill(), 12000);
      await exited;
      clearTimeout(force);
    }
    modelServer.close();
  }
  assert.equal(child.exitCode, 0, errors);
});
