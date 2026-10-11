import assert from "node:assert/strict";
import { test } from "node:test";
import { buildSteps, formatDuration, resultPreview, runSpan, stepDuration } from "../src/run-steps.ts";

const call = (id: string, name: string, args: unknown) => ({ id, function: { name, arguments: JSON.stringify(args) } });

const run = [
  { id: "u1", role: "user", content: "Check the GPU" },
  { id: "r1", role: "reasoning", content: "The user wants GPU status, so run nvidia-smi." },
  { id: "a1", role: "assistant", content: "", toolCalls: [call("t1", "run_spark_command", { command: "nvidia-smi" }), call("t2", "browse_web", { url: "https://x.dev" })] },
  { id: "tm1", role: "tool", toolCallId: "t1", content: JSON.stringify({ ran: true, exit_code: 0, output: "GPU 0: GB10  41C" }) },
  { id: "tm2", role: "tool", toolCallId: "t2", content: JSON.stringify({ error: "fetch failed" }) },
  { id: "a2", role: "assistant", content: "GPU is at 41°C." },
];

test("a finished run lists route, thinking, each tool and the answer in order", () => {
  const steps = buildSteps(run, 1, run.length, {
    route: { skill: "spark-ops", confidence: 0.91, ms: 38, used: true },
    times: { t1: { start: 1000, end: 2500 }, r1: { start: 500, end: 900 } },
  });
  assert.deepEqual(steps.map((s) => s.kind), ["route", "thinking", "tool", "tool", "answer"]);
  const [route, thinking, spark, web, answer] = steps;
  assert.equal(route.ms, 38);
  assert.match(route.summary, /spark-ops · 0\.91/);
  assert.equal(thinking.thought, run[1].content);
  assert.equal(spark.summary, "$ nvidia-smi");
  assert.equal(spark.status, "done");
  assert.equal(spark.preview, "GPU 0: GB10 41C");
  assert.match(spark.result ?? "", /"exit_code": 0/);
  assert.equal(stepDuration(spark, 0), 1500);
  assert.equal(web.status, "error");
  assert.equal(web.why, "fetch failed");
  assert.equal(answer.summary, "GPU is at 41°C.");
});

test("a live run marks open tool calls running, Spark commands awaiting approval as waiting", () => {
  const live = run.slice(0, 3);
  const steps = buildSteps(live, 1, live.length, {
    zh: true, running: true, pendingCommands: ["nvidia-smi"], times: { t2: { start: 100 } },
  });
  const tools = steps.filter((s) => s.kind === "tool");
  assert.equal(tools[0].status, "waiting");
  assert.equal(tools[0].why, "等待你批准");
  assert.equal(tools[0].title, "在 Spark 上执行");
  assert.equal(tools[1].status, "running");
  assert.equal(stepDuration(tools[1], 1600), 1500);
  assert.equal(steps.at(-1)?.kind, "tool", "no answer row before the answer exists");
});

test("a stopped run marks unfinished calls as failed; a tool-free reply has no steps", () => {
  const stopped = buildSteps(run.slice(0, 3), 1, 3);
  assert.ok(stopped.filter((s) => s.kind === "tool").every((s) => s.status === "error" && s.why === "did not finish"));
  const plain = [{ id: "u", role: "user", content: "hi" }, { id: "a", role: "assistant", content: "Hello" }];
  assert.deepEqual(buildSteps(plain, 1, 2), []);
});

test("durations format compactly and runs span first start to last end", () => {
  assert.equal(formatDuration(420), "420ms");
  assert.equal(formatDuration(3240), "3.2s");
  assert.equal(formatDuration(42_000), "42s");
  assert.equal(formatDuration(65_000), "1m 05s");
  assert.equal(formatDuration(undefined), "");
  assert.equal(formatDuration(-1), "");
  const steps = buildSteps(run, 1, run.length, { times: { r1: { start: 500, end: 900 }, t1: { start: 1000, end: 2500 } } });
  assert.equal(runSpan(steps, 9999), 2000);
});

test("result previews pick the useful field", () => {
  assert.equal(resultPreview(JSON.stringify({ summary: "3 unread" }), false), "3 unread");
  assert.equal(resultPreview(JSON.stringify({ results: [1, 2, 3] }), true), "3 条结果");
  assert.equal(resultPreview("plain text\nresult", false), "plain text result");
  assert.equal(resultPreview("", false), undefined);
});
