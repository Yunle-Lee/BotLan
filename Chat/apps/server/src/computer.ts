import { spawn } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { posix } from "node:path";
import { z } from "zod";
import type {
  ComputerCommand,
  ComputerDirectory,
  ComputerSnapshot,
} from "../../../packages/domain/src/computer.ts";
import type { Config } from "./config.ts";
import type { Store } from "./db.ts";
import { AppError } from "./errors.ts";

const hash = (value: string) => createHash("sha256").update(value).digest("hex");
const outputLimit = 128 * 1024;
const fileLimit = 256 * 1024;
const controlTimeout = 10000;
const leaseDuration = 180000;
export const computerCommandSchema = z.object({
  command: z.string().trim().min(1).max(16000),
  cwd: z.string().default("/workspace"),
});
export const computerPathSchema = z.object({ path: z.string().min(1).max(2048) });
export const computerWriteSchema = computerPathSchema.extend({ text: z.string().max(fileLimit) });
export interface DockerResult {
  stdout: string;
  stderr: string;
  exitCode: number | null;
  timedOut: boolean;
  interrupted: boolean;
  truncated: boolean;
}
export type DockerRunner = (
  args: string[],
  options: { timeoutMs: number; input?: string; signal?: AbortSignal; maxOutputBytes?: number },
) => Promise<DockerResult>;

// The only host process this provider can launch is Docker. User input is an argv
// element or stdin, never a host shell program. Do not add a shell fallback here.
export const runDocker: DockerRunner = (args, options) =>
  new Promise((resolve) => {
    const limit = Math.min(options.maxOutputBytes ?? outputLimit, 15 * 1024 * 1024);
    const result: DockerResult = {
      stdout: "",
      stderr: "",
      exitCode: null,
      timedOut: false,
      interrupted: false,
      truncated: false,
    };
    const stdout: Buffer[] = [],
      stderr: Buffer[] = [];
    let count = 0,
      settled = false;
    const env: Record<string, string> = {};
    for (const key of [
      "PATH",
      "HOME",
      "DOCKER_HOST",
      "DOCKER_CONTEXT",
      "DOCKER_CONFIG",
      "DOCKER_TLS_VERIFY",
      "DOCKER_CERT_PATH",
    ])
      if (process.env[key]) env[key] = process.env[key];
    const child = spawn("docker", args, { shell: false, stdio: ["pipe", "pipe", "pipe"], env });
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      options.signal?.removeEventListener("abort", abort);
      result.stdout = Buffer.concat(stdout).toString("utf8");
      result.stderr = Buffer.concat(stderr).toString("utf8");
      resolve(result);
    };
    const capture = (chunks: Buffer[], chunk: Buffer) => {
      const remaining = Math.max(0, limit - count);
      if (chunk.length > remaining) result.truncated = true;
      if (remaining) chunks.push(chunk.subarray(0, remaining));
      count += Math.min(remaining, chunk.length);
    };
    const abort = () => {
      result.interrupted = true;
      child.kill("SIGKILL");
      finish();
    };
    const timer = setTimeout(() => {
      result.timedOut = true;
      child.kill("SIGKILL");
      finish();
    }, options.timeoutMs);
    child.stdout.on("data", (chunk: Buffer) => capture(stdout, chunk));
    child.stderr.on("data", (chunk: Buffer) => capture(stderr, chunk));
    child.on("error", () => {
      capture(
        stderr,
        Buffer.from("Docker CLI could not be started. Install Docker and start its engine."),
      );
      finish();
    });
    child.on("close", (code) => {
      result.exitCode = code;
      finish();
    });
    child.stdin.on("error", () => {
      /* A failed Docker process may close stdin before consuming it. Its exit is retained. */
    });
    options.signal?.addEventListener("abort", abort, { once: true });
    if (options.signal?.aborted) abort();
    else child.stdin.end(options.input ?? "");
  });

export function computerIdentity(config: Config, owner: string) {
  const deployment = hash(config.computerDeploymentId ?? config.publicUrl).slice(0, 16);
  const ownerHash = hash(owner).slice(0, 24);
  const name = `agent-${deployment}-${ownerHash}`;
  return {
    container: `${name}-computer`,
    volume: `${name}-workspace`,
    labels: {
      "dev.agent.managed": "computer-v1",
      "dev.agent.deployment": deployment,
      "dev.agent.owner": ownerHash,
    } as Record<string, string>,
  };
}
export function workspacePath(path: string): string {
  if (
    path.includes("\0") ||
    path.length > 2048 ||
    !path.startsWith("/workspace") ||
    path.split("/").includes("..")
  )
    throw new AppError("Choose an absolute path inside /workspace", 422);
  const normalized = posix.normalize(path);
  if (normalized !== "/workspace" && !normalized.startsWith("/workspace/"))
    throw new AppError("Choose an absolute path inside /workspace", 422);
  return normalized;
}
const inspectionSchema = z.object({
  Id: z.string(),
  Name: z.string(),
  Config: z.object({
    Image: z.string(),
    User: z.string(),
    Labels: z.record(z.string(), z.string()).nullable(),
    Env: z.array(z.string()),
    Entrypoint: z.array(z.string()).nullable(),
    Cmd: z.array(z.string()).nullable(),
    WorkingDir: z.string(),
  }),
  HostConfig: z.object({
    ReadonlyRootfs: z.boolean(),
    Privileged: z.boolean(),
    CapDrop: z.array(z.string()).nullable(),
    CapAdd: z.array(z.string()).nullable(),
    SecurityOpt: z.array(z.string()).nullable(),
    NetworkMode: z.string(),
    Memory: z.number(),
    MemorySwap: z.number(),
    PidsLimit: z.number().nullable(),
    NanoCpus: z.number(),
    Binds: z.array(z.string()).nullable(),
    Devices: z.array(z.unknown()).nullable(),
    DeviceRequests: z.array(z.unknown()).nullable(),
    PortBindings: z.record(z.string(), z.unknown()).nullable(),
    PidMode: z.string(),
    IpcMode: z.string(),
    Tmpfs: z.record(z.string(), z.string()).nullable(),
    RestartPolicy: z.object({ Name: z.string() }),
  }),
  Mounts: z.array(
    z.object({
      Type: z.string(),
      Name: z.string().optional(),
      Destination: z.string(),
      RW: z.boolean(),
    }),
  ),
  NetworkSettings: z.object({ Networks: z.record(z.string(), z.unknown()) }),
  State: z.object({ Running: z.boolean() }),
});
type Inspection = z.infer<typeof inspectionSchema>;
type Lease = {
  id: string;
  token: string;
  expiresAt: number;
  stopping: boolean;
  stopInFlight: boolean;
  stopAttempt: string;
  stopConfirmed: boolean;
  executorDone: boolean;
  operation: "command" | "operation";
};
export class ComputerService {
  constructor(
    readonly db: Store,
    readonly config: Config,
    private readonly docker: DockerRunner = runDocker,
  ) {}
  private enabled() {
    if (!this.config.computerEnabled)
      throw new AppError(
        "Computer is not configured. Enable COMPUTER_ENABLED and build the local computer image.",
        503,
      );
  }
  private image() {
    const image = this.config.computerImage ?? "agent-computer:local";
    if (!/^[a-zA-Z0-9][a-zA-Z0-9._/:@-]{0,250}$/.test(image))
      throw new AppError("COMPUTER_IMAGE is invalid", 503);
    return image;
  }
  private async checked(args: string[]) {
    const result = await this.docker(args, { timeoutMs: controlTimeout });
    if (result.timedOut)
      throw new AppError("Docker did not respond within 10 seconds. Check the Docker engine.", 503);
    if (result.interrupted || result.exitCode !== 0 || result.truncated)
      throw new AppError(
        "Docker operation failed. Check that the engine is running and the computer image is built locally.",
        503,
      );
    return result.stdout;
  }
  private async inspect(owner: string): Promise<Inspection | undefined> {
    const identity = computerIdentity(this.config, owner);
    const found = (
      await this.checked([
        "container",
        "ls",
        "--all",
        "--filter",
        `name=^/${identity.container}$`,
        "--format",
        "{{.ID}}",
      ])
    ).trim();
    if (!found) return undefined;
    const raw = JSON.parse(await this.checked(["container", "inspect", identity.container]));
    const result = z.array(inspectionSchema).length(1).safeParse(raw);
    if (!result.success)
      throw new AppError("Computer isolation inspection failed; refusing to attach", 409);
    const c = result.data[0],
      h = c.HostConfig;
    const empty = (list: unknown[] | null) => !list?.length;
    const safe =
      c.Name === `/${identity.container}` &&
      c.Config.Image === this.image() &&
      c.Config.User === "1000:1000" &&
      c.Config.WorkingDir === "/workspace" &&
      Object.entries(identity.labels).every(([key, value]) => c.Config.Labels?.[key] === value) &&
      c.Config.Env.every((value) =>
        ["PATH", "HOME", "LANG", "NODE_VERSION", "YARN_VERSION"].includes(value.split("=")[0]),
      ) &&
      JSON.stringify(c.Config.Entrypoint) === '["/usr/bin/sleep"]' &&
      JSON.stringify(c.Config.Cmd) === '["infinity"]' &&
      h.ReadonlyRootfs &&
      !h.Privileged &&
      h.CapDrop?.includes("ALL") &&
      empty(h.CapAdd) &&
      h.SecurityOpt?.length === 1 &&
      h.SecurityOpt.includes("no-new-privileges") &&
      h.NetworkMode === "none" &&
      h.Memory > 0 &&
      h.Memory <= 536870912 &&
      h.MemorySwap === h.Memory &&
      h.PidsLimit !== null &&
      h.PidsLimit > 0 &&
      h.PidsLimit <= 128 &&
      h.NanoCpus > 0 &&
      h.NanoCpus <= 1000000000 &&
      empty(h.Binds) &&
      empty(h.Devices) &&
      empty(h.DeviceRequests) &&
      !Object.keys(h.PortBindings ?? {}).length &&
      h.PidMode === "" &&
      h.IpcMode === "private" &&
      h.RestartPolicy.Name === "no" &&
      Object.keys(h.Tmpfs ?? {}).length === 1 &&
      h.Tmpfs?.["/tmp"] === "rw,nosuid,nodev,noexec,size=67108864,mode=1777" &&
      c.Mounts.length === 1 &&
      c.Mounts[0].Type === "volume" &&
      c.Mounts[0].Name === identity.volume &&
      c.Mounts[0].Destination === "/workspace" &&
      c.Mounts[0].RW &&
      Object.keys(c.NetworkSettings.Networks).every((network) => network === "none");
    if (!safe)
      throw new AppError(
        "Computer ownership or isolation does not match this deployment; refusing to attach",
        409,
      );
    await this.verifyVolume(owner);
    return c;
  }
  private async verifyVolume(owner: string) {
    const identity = computerIdentity(this.config, owner);
    const parsed = z
      .array(
        z.object({
          Name: z.string(),
          Labels: z.record(z.string(), z.string()).nullable(),
          Driver: z.string(),
          Options: z.record(z.string(), z.unknown()).nullable(),
          Scope: z.string(),
        }),
      )
      .length(1)
      .safeParse(JSON.parse(await this.checked(["volume", "inspect", identity.volume])));
    if (!parsed.success) throw new AppError("Computer workspace ownership inspection failed", 409);
    const v = parsed.data[0];
    if (
      v.Name !== identity.volume ||
      v.Driver !== "local" ||
      v.Scope !== "local" ||
      Object.keys(v.Options ?? {}).length ||
      !Object.entries(identity.labels).every(([key, value]) => v.Labels?.[key] === value)
    )
      throw new AppError("Computer workspace ownership or isolation does not match", 409);
  }
  private async acquire(owner: string, operation: Lease["operation"] = "operation") {
    this.enabled();
    const previous = await this.db.get<Lease>(owner, "computer-state", "lease");
    const lease = {
      id: "lease",
      token: randomUUID(),
      expiresAt: Date.now() + leaseDuration,
      stopping: false,
      stopInFlight: false,
      stopAttempt: "",
      stopConfirmed: false,
      executorDone: operation !== "command",
      operation,
    };
    if (previous && previous.expiresAt > Date.now())
      throw new AppError("Computer is busy. Wait for the current operation to finish.", 409);
    const claimed = previous
      ? await this.db.compareAndSwap<Lease>(
          owner,
          "computer-state",
          "lease",
          { token: previous.token, expiresAt: previous.expiresAt },
          lease,
        )
      : await this.db.insertIfAbsent(owner, "computer-state", lease);
    if (!claimed)
      throw new AppError("Computer is busy. Wait for the current operation to finish.", 409);
    return lease;
  }
  private async exclusive<T>(
    owner: string,
    operation: (lease: Lease) => Promise<T>,
    kind: Lease["operation"] = "operation",
  ) {
    const lease = await this.acquire(owner, kind);
    try {
      return await operation(lease);
    } finally {
      // A stopped command must acknowledge completion before a new lifecycle can
      // begin. A delayed Docker client can otherwise exec into a restarted box.
      if (kind === "command")
        await this.db.compareAndSwap(
          owner,
          "computer-state",
          "lease",
          { token: lease.token },
          { executorDone: true },
        );
      await this.db.compareAndSwap(
        owner,
        "computer-state",
        "lease",
        { token: lease.token, stopping: false },
        { expiresAt: 0 },
      );
      await this.releaseStopped(owner, lease.token);
    }
  }
  private async releaseStopped(owner: string, token: string) {
    await this.db.compareAndSwap(
      owner,
      "computer-state",
      "lease",
      { token, stopping: true, stopConfirmed: true, executorDone: true, stopInFlight: false },
      { expiresAt: 0 },
    );
  }
  private async commands(owner: string) {
    const commands = await this.db.list<ComputerCommand>(owner, "computer-commands");
    const lease = await this.db.get<Lease>(owner, "computer-state", "lease");
    if (!lease || lease.expiresAt <= Date.now()) {
      for (const command of commands)
        if (command.status === "running") {
          const saved = await this.db.compareAndSwap<ComputerCommand>(
            owner,
            "computer-commands",
            command.id,
            { status: "running" },
            {
              status: "interrupted",
              completedAt: new Date().toISOString(),
              stderr:
                "Execution was interrupted. Its outcome is unknown; inspect files before running it again.",
            },
          );
          if (saved) Object.assign(command, saved);
        }
    }
    return commands.sort((a, b) => b.startedAt.localeCompare(a.startedAt)).slice(0, 100);
  }
  async snapshot(owner: string): Promise<ComputerSnapshot> {
    const base = {
      enabled: Boolean(this.config.computerEnabled),
      provider: "docker" as const,
      workspacePath: "/workspace" as const,
      network: "disabled" as const,
      commands: await this.commands(owner),
    };
    if (!base.enabled)
      return {
        ...base,
        status: "unconfigured",
        message:
          "Enable the Docker computer on the server to use its terminal and workspace files.",
      };
    try {
      return {
        ...base,
        status: (await this.inspect(owner))?.State.Running ? "running" : "stopped",
      };
    } catch (error) {
      return {
        ...base,
        status: "error",
        message:
          error instanceof AppError
            ? error.message
            : "Computer inspection failed. Check Docker setup.",
      };
    }
  }
  async start(owner: string) {
    await this.exclusive(owner, async () => {
      const identity = computerIdentity(this.config, owner);
      const existing = await this.inspect(owner);
      if (existing) {
        if (!existing.State.Running) await this.checked(["container", "start", identity.container]);
        return;
      }
      const labels = Object.entries(identity.labels).flatMap(([key, value]) => [
        "--label",
        `${key}=${value}`,
      ]);
      const volume = (
        await this.checked([
          "volume",
          "ls",
          "--filter",
          `name=^${identity.volume}$`,
          "--format",
          "{{.Name}}",
        ])
      ).trim();
      if (!volume) await this.checked(["volume", "create", ...labels, identity.volume]);
      await this.verifyVolume(owner);
      await this.checked([
        "container",
        "create",
        "--pull",
        "never",
        "--name",
        identity.container,
        ...labels,
        "--user",
        "1000:1000",
        "--workdir",
        "/workspace",
        "--read-only",
        "--cap-drop",
        "ALL",
        "--security-opt",
        "no-new-privileges",
        "--network",
        "none",
        "--ipc",
        "private",
        "--memory",
        "512m",
        "--memory-swap",
        "512m",
        "--cpus",
        "1",
        "--pids-limit",
        "128",
        "--restart",
        "no",
        "--tmpfs",
        "/tmp:rw,nosuid,nodev,noexec,size=67108864,mode=1777",
        "--mount",
        `type=volume,source=${identity.volume},target=/workspace`,
        "--env",
        "HOME=/workspace",
        "--env",
        "LANG=C.UTF-8",
        "--entrypoint",
        "/usr/bin/sleep",
        this.image(),
        "infinity",
      ]);
      await this.inspect(owner);
      await this.checked(["container", "start", identity.container]);
    });
    return this.snapshot(owner);
  }
  async stop(owner: string) {
    this.enabled();
    let lease = await this.db.get<Lease>(owner, "computer-state", "lease");
    if (!lease || lease.expiresAt <= Date.now()) lease = await this.acquire(owner);
    else if ((lease.operation !== "command" && !lease.stopping) || lease.stopInFlight)
      throw new AppError("Computer is busy with another operation. Try Stop again shortly.", 409);
    const attempt = randomUUID();
    const stopping = await this.db.compareAndSwap<Lease>(
      owner,
      "computer-state",
      "lease",
      {
        token: lease.token,
        stopping: lease.stopping,
        ...(lease.stopAttempt !== undefined ? { stopAttempt: lease.stopAttempt } : {}),
      },
      {
        stopping: true,
        stopInFlight: true,
        stopAttempt: attempt,
        stopConfirmed: false,
        expiresAt: Date.now() + leaseDuration,
      },
    );
    if (!stopping) throw new AppError("Computer is busy with another Stop request", 409);
    try {
      // Record intent before Docker Stop so a concurrently exiting command
      // cannot report success over the user's interruption.
      for (const command of await this.db.list<ComputerCommand>(owner, "computer-commands"))
        if (command.status === "running")
          await this.db.compareAndSwap(
            owner,
            "computer-commands",
            command.id,
            { status: "running" },
            {
              status: "interrupted",
              completedAt: new Date().toISOString(),
              stderr: "Stopped by the user. Inspect the workspace before repeating this command.",
            },
          );
      if ((await this.inspect(owner))?.State.Running)
        await this.checked([
          "container",
          "stop",
          "--time",
          "2",
          computerIdentity(this.config, owner).container,
        ]);
      await this.db.compareAndSwap(
        owner,
        "computer-state",
        "lease",
        { token: lease.token, stopAttempt: attempt },
        { stopInFlight: false, stopConfirmed: true },
      );
      await this.releaseStopped(owner, lease.token);
    } catch (error) {
      // Keep the command quarantine, but allow an explicit retry after a
      // transient Docker failure. Only an in-flight Stop excludes another Stop.
      await this.db.compareAndSwap(
        owner,
        "computer-state",
        "lease",
        { token: lease.token, stopAttempt: attempt },
        { stopInFlight: false, stopConfirmed: false },
      );
      throw error;
    }
    return this.snapshot(owner);
  }
  private async running(owner: string) {
    this.enabled();
    if (!(await this.inspect(owner))?.State.Running)
      throw new AppError("Start the computer before using its terminal or files", 409);
    return computerIdentity(this.config, owner).container;
  }
  async execute(
    owner: string,
    raw: unknown,
    options: { idempotencyKey?: string; signal?: AbortSignal } = {},
  ): Promise<ComputerCommand> {
    this.enabled();
    const args = computerCommandSchema.parse(raw),
      cwd = workspacePath(args.cwd);
    const id = options.idempotencyKey
      ? hash(`computer-command:${options.idempotencyKey}`)
      : randomUUID();
    const previous = await this.db.get<ComputerCommand>(owner, "computer-commands", id);
    if (previous) {
      if (previous.command !== args.command || previous.cwd !== cwd)
        throw new AppError("This operation ID already belongs to a different command", 409);
      await this.commands(owner);
      return (await this.db.get<ComputerCommand>(owner, "computer-commands", id)) ?? previous;
    }
    return this.exclusive(
      owner,
      async (lease) => {
        const container = await this.running(owner);
        if (options.signal?.aborted)
          throw new AppError("Computer command was interrupted before execution", 409);
        const command: ComputerCommand = {
          id,
          command: args.command,
          cwd,
          status: "running",
          stdout: "",
          stderr: "",
          truncated: false,
          startedAt: new Date().toISOString(),
        };
        const saved = await this.db.insertIfAbsent(owner, "computer-commands", command);
        if (!saved) {
          const existing = await this.db.get<ComputerCommand>(owner, "computer-commands", id);
          if (existing) return existing;
          throw new AppError("Computer receipt could not be saved", 500);
        }
        const active = await this.db.get<Lease>(owner, "computer-state", "lease");
        if (
          !active ||
          active.token !== lease.token ||
          active.stopping ||
          active.expiresAt <= Date.now()
        )
          return this.db.put(owner, "computer-commands", {
            ...command,
            status: "interrupted",
            stderr: "Stopped before execution",
            completedAt: new Date().toISOString(),
          });
        let result: DockerResult;
        try {
          result = await this.docker(
            [
              "exec",
              "--user",
              "1000:1000",
              "--workdir",
              cwd,
              container,
              "/usr/bin/timeout",
              "--signal=TERM",
              "--kill-after=2s",
              "30s",
              "/bin/bash",
              "--noprofile",
              "--norc",
              "-c",
              args.command,
            ],
            { timeoutMs: 35000, signal: options.signal },
          );
        } catch {
          result = {
            stdout: "",
            stderr: "Docker execution was interrupted; inspect the workspace before retrying.",
            exitCode: null,
            interrupted: true,
            timedOut: false,
            truncated: false,
          };
        }
        const timedOut = result.timedOut || result.exitCode === 124;
        // A lost Docker client cannot cancel exec reliably. Stop the whole sandbox
        // to ensure no unknown command continues after the lease is released.
        if (result.timedOut || result.interrupted) {
          const attempt = randomUUID();
          const cleanup = await this.db.compareAndSwap<Lease>(
            owner,
            "computer-state",
            "lease",
            { token: lease.token, stopping: false },
            {
              stopping: true,
              stopInFlight: true,
              stopAttempt: attempt,
              stopConfirmed: false,
              expiresAt: Date.now() + leaseDuration,
            },
          );
          // An explicit Stop may already own cleanup. In either case the lease
          // cannot release until cleanup is confirmed and this executor is done.
          if (cleanup) {
            try {
              await this.checked(["container", "stop", "--time", "2", container]);
              await this.db.compareAndSwap(
                owner,
                "computer-state",
                "lease",
                { token: lease.token, stopAttempt: attempt },
                { stopInFlight: false, stopConfirmed: true },
              );
            } catch {
              await this.db.compareAndSwap(
                owner,
                "computer-state",
                "lease",
                { token: lease.token, stopAttempt: attempt },
                { stopInFlight: false, stopConfirmed: false },
              );
              result.stderr +=
                "\nCould not confirm container stop. The computer remains locked; retry Stop after checking Docker.";
            }
          }
        }
        const final: ComputerCommand = {
          ...command,
          status: result.interrupted
            ? "interrupted"
            : timedOut
              ? "timed_out"
              : result.exitCode === 0
                ? "succeeded"
                : "failed",
          ...(result.exitCode !== null ? { exitCode: result.exitCode } : {}),
          stdout: result.stdout,
          stderr: result.stderr,
          truncated: result.truncated,
          completedAt: new Date().toISOString(),
        };
        const finished = await this.db.compareAndSwap<ComputerCommand>(
          owner,
          "computer-commands",
          id,
          { status: "running" },
          { ...final },
        );
        if (finished) return finished;
        const interrupted = await this.db.get<ComputerCommand>(owner, "computer-commands", id);
        return this.db.put(owner, "computer-commands", {
          ...final,
          status: "interrupted",
          stderr: [result.stderr, interrupted?.stderr].filter(Boolean).join("\n"),
        });
      },
      "command",
    );
  }
  private async file<T>(
    owner: string,
    operation: string,
    rawPath: string,
    text?: string,
    base64?: string,
  ): Promise<T> {
    const path = workspacePath(rawPath);
    if (text !== undefined && Buffer.byteLength(text) > fileLimit)
      throw new AppError("Text files must be 256 KB or smaller", 413);
    return this.exclusive(owner, async () => {
      const container = await this.running(owner);
      const result = await this.docker(
        [
          "exec",
          "-i",
          "--user",
          "1000:1000",
          container,
          "/usr/bin/timeout",
          "--kill-after=1s",
          "8s",
          "/usr/bin/python3",
          "-I",
          "/opt/agent/files.py",
        ],
        {
          timeoutMs: 10000,
          input: JSON.stringify({ operation, path, text, base64 }),
          maxOutputBytes: operation === "read_pdf" ? 15 * 1024 * 1024 : 2 * 1024 * 1024,
        },
      );
      if (result.exitCode !== 0 || result.timedOut || result.interrupted || result.truncated)
        throw new AppError(
          "Computer file operation failed. Check the path, permissions and file size; symlinks cannot be opened.",
          422,
        );
      try {
        return JSON.parse(result.stdout) as T;
      } catch {
        throw new AppError("Computer returned an invalid file response", 502);
      }
    });
  }
  list(owner: string, path = "/workspace") {
    return this.file<ComputerDirectory>(owner, "list", path);
  }
  read(owner: string, path: string) {
    return this.file<{ path: string; text: string }>(owner, "read", path);
  }
  write(owner: string, path: string, text: string) {
    return this.file<{ path: string }>(owner, "write", path, text);
  }
  mkdir(owner: string, path: string) {
    return this.file<{ path: string }>(owner, "mkdir", path);
  }
  async writePdf(owner: string, path: string, bytes: Uint8Array) {
    if (bytes.length > 10 * 1024 * 1024 || Buffer.from(bytes.subarray(0, 5)).toString() !== "%PDF-")
      throw new AppError("Choose a PDF of 10 MB or smaller", 422);
    return this.file<{ path: string }>(
      owner,
      "write_pdf",
      path,
      undefined,
      Buffer.from(bytes).toString("base64"),
    );
  }
  async pdfBytes(owner: string, path: string) {
    const result = await this.file<{ path: string; base64: string }>(owner, "read_pdf", path);
    return { name: posix.basename(path), bytes: Buffer.from(result.base64, "base64") };
  }
}
