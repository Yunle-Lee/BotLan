import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { PDFDocument } from "pdf-lib";
import { createApp } from "../server/src/app.ts";
import { computerIdentity, runDocker } from "../server/src/computer.ts";
import type { Config } from "../server/src/config.ts";
import { createStore } from "../server/src/db.ts";

// Explicit opt-in script: uses a unique deployment and removes only its resources.
// Run: DOCKER_CONTEXT=<context> pnpm test:computer
// The Docker image must already be built; this test never pulls an image.
test("real isolated computer executes commands, persists files, bridges PDFs and stops active work", {
  timeout: 120000,
}, async () => {
  const directory = await mkdtemp(join(tmpdir(), "studio-computer-smoke-"));
  const db = await createStore();
  const config: Config = {
    mode: "sample",
    port: 8787,
    host: "127.0.0.1",
    publicUrl: "http://localhost:8787",
    dataDir: directory,
    agentBackend: "sample",
    intelligenceApiKey: "test-project-key-never-sent",
    googleRedirectUri: "http://localhost/callback",
    allowedOrigins: [],
    computerEnabled: true,
    computerImage: process.env.COMPUTER_IMAGE ?? "studio-computer:local",
    computerDeploymentId: `smoke-${randomUUID()}`,
  };
  const server = await createApp(db, config);
  const owner = "local-user",
    identity = computerIdentity(config, owner);
  try {
    const session = await server.auth.session();
    const headers = {
      Authorization: `Bearer ${session.token}`,
      "Content-Type": "application/json",
    };
    const post = async (path: string, body: unknown = {}) => {
      const response = await server.app.request(`/api/computer${path}`, {
        method: "POST",
        headers,
        body: JSON.stringify(body),
      });
      const result = await response.json();
      assert.ok(response.ok, `${path}: ${JSON.stringify(result)}`);
      return result;
    };
    assert.equal((await post("/start")).status, "running");
    const command = await post("/commands", {
      command:
        "id -u; node --version; python3 --version; git --version; printf 'persisted from bash\\n' > receipt.txt",
      cwd: "/workspace",
    });
    assert.equal(command.status, "succeeded");
    assert.match(command.stdout, /^1000\nv22\./);
    await post("/files/mkdir", { path: "/workspace/notes" });
    await post("/files/write", {
      path: "/workspace/notes/hello.txt",
      text: "Hello from the API ✓",
    });
    assert.equal(
      (await post("/files/read", { path: "/workspace/notes/hello.txt" })).text,
      "Hello from the API ✓",
    );
    const denied = await post("/commands", { command: "touch /etc/studio-must-fail" });
    assert.equal(denied.status, "failed");
    const network = await post("/commands", {
      command: "python3 -c 'import socket; socket.create_connection((\"1.1.1.1\", 443), 1)'",
    });
    assert.equal(network.status, "failed");
    const stdout = await post("/commands", { command: "python3 -c 'print(\"x\" * 300000)'" });
    assert.equal(stdout.status, "succeeded");
    assert.equal(stdout.truncated, true);
    assert.ok(Buffer.byteLength(stdout.stdout) <= 128 * 1024);
    await post("/commands", { command: "ln -s /etc /workspace/escape" });
    const escaped = await server.app.request("/api/computer/files/read", {
      method: "POST",
      headers,
      body: JSON.stringify({ path: "/workspace/escape/passwd" }),
    });
    assert.equal(escaped.status, 422);
    const pdf = await PDFDocument.create();
    pdf.addPage();
    const original = await server.files.import(owner, "smoke.pdf", await pdf.save(), "Smoke test");
    await post("/files/import", { fileId: original.id, path: "/workspace/source.pdf" });
    await post("/commands", { command: "cp source.pdf output.pdf" });
    const exported = await post("/files/export", { path: "/workspace/output.pdf" });
    assert.equal(exported.name, "output.pdf");
    assert.notEqual(exported.id, original.id);
    assert.deepEqual(
      await server.files.bytes(owner, exported.id),
      await server.files.bytes(owner, original.id),
    );
    await post("/stop");
    await post("/start");
    assert.equal(
      (await post("/files/read", { path: "/workspace/receipt.txt" })).text,
      "persisted from bash\n",
    );
    const pending = server.computer.execute(owner, {
      command: "printf ready > /workspace/.smoke-running; sleep 30",
    });
    const deadline = Date.now() + 10000;
    while (true) {
      const processes = await runDocker(
        ["exec", identity.container, "/usr/bin/cat", "/workspace/.smoke-running"],
        {
          timeoutMs: 3000,
        },
      );
      if (processes.exitCode === 0 && processes.stdout === "ready") break;
      assert.ok(Date.now() < deadline, "command process failed to begin");
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
    await server.computer.stop(owner);
    assert.equal((await pending).status, "interrupted");
    assert.equal((await server.computer.snapshot(owner)).status, "stopped");
    const saved = await server.computer.snapshot(owner);
    assert.ok(
      saved.commands.some((receipt) => receipt.id === command.id && receipt.status === "succeeded"),
    );
  } finally {
    const removed = await runDocker(["container", "rm", "--force", identity.container], {
      timeoutMs: 10000,
    });
    const volume = await runDocker(["volume", "rm", identity.volume], { timeoutMs: 10000 });
    await db.close();
    await rm(directory, { recursive: true, force: true });
    assert.equal(removed.exitCode, 0, "smoke container cleanup failed");
    assert.equal(volume.exitCode, 0, "smoke volume cleanup failed");
  }
});
