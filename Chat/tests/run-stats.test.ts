import assert from "node:assert/strict";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { test } from "node:test";
import { type BaseEvent, EventType } from "@ag-ui/core";
import { lastValueFrom, toArray } from "rxjs";
import { RUN_STATS_EVENT, tanstackAgent } from "../apps/server/src/engine/tanstack-agent.ts";

// A llama-server-shaped stream: text deltas, then the usage chunk stream_options asks for.
function mockModel() {
  return createServer((req, res) => {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", async () => {
      const request = JSON.parse(body);
      res.writeHead(200, { "content-type": "text/event-stream" });
      const chunk = (delta: object, extra: object = {}) =>
        res.write(`data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", created: 1, model: "m",
          choices: [{ index: 0, delta, finish_reason: null }], ...extra })}\n\n`);
      for (const word of ["Hello", " from", " the", " Spark"]) {
        chunk({ content: word });
        await new Promise((r) => setTimeout(r, 40));
      }
      res.write(`data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", created: 1, model: "m",
        choices: [{ index: 0, delta: {}, finish_reason: "stop" }] })}\n\n`);
      if (request.stream_options?.include_usage)
        res.write(`data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", created: 1, model: "m", choices: [],
          usage: { prompt_tokens: 1200, completion_tokens: 8, total_tokens: 1208 } })}\n\n`);
      res.end("data: [DONE]\n\n");
    });
  });
}

test("each run reports output speed and context size once, before RUN_FINISHED", async () => {
  const server = mockModel().listen(0, "127.0.0.1");
  await new Promise((r) => server.once("listening", r));
  try {
    const { port } = server.address() as AddressInfo;
    const agent = tanstackAgent({
      islandBot: { model: "jev-step", baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: "k" },
      model: "openai/unused",
      maxSteps: 2,
      tools: [],
      prompt: "test",
    });
    const events: BaseEvent[] = await lastValueFrom(agent.run({
      threadId: "t", runId: "r1", state: {}, tools: [], context: [], forwardedProps: {},
      messages: [{ id: "u1", role: "user", content: "hi" }],
    }).pipe(toArray()));
    const stats = events.filter((e) => e.type === EventType.CUSTOM && (e as { name?: string }).name === RUN_STATS_EVENT);
    assert.equal(stats.length, 1, "exactly one stats event");
    const value = (stats[0] as unknown as { value: Record<string, number | null> }).value;
    assert.equal(value.outputTokens, 8);
    assert.equal(value.contextTokens, 1208);
    assert.equal(value.steps, 1);
    assert.ok(value.tokensPerSecond !== null && value.tokensPerSecond > 10 && value.tokensPerSecond < 200,
      `speed counts generation time only (got ${value.tokensPerSecond})`);
    const at = events.indexOf(stats[0]);
    assert.equal(events[at + 1]?.type, EventType.RUN_FINISHED, "stats arrive right before RUN_FINISHED");
  } finally {
    server.close();
  }
});
