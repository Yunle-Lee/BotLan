import assert from "node:assert/strict";
import { test } from "node:test";
import { ConversationQueue } from "../src/conversation-queue.ts";

test("follow-ups sent during a reply run once, in order, after persistence finishes", async () => {
  const queue = new ConversationQueue();
  const gate = deferred();
  const received: string[] = [];
  queue.enqueue({ id: "1", text: "First" });
  const send = async ({ id }: { id: string }) => {
    received.push(id);
    if (id === "1") await gate.promise;
  };
  const running = queue.flush(send);
  queue.enqueue({ id: "2", text: "Second" });
  queue.enqueue({ id: "3", text: "Third" });
  await queue.flush(send);
  assert.deepEqual(received, ["1"]);
  queue.remove("2");
  gate.resolve();
  await running;
  assert.deepEqual(received, ["1", "3"]);
  assert.equal(queue.getSnapshot().running, false);
});

test("stop pauses queued work and a failed reply never silently retries", async () => {
  const queue = new ConversationQueue();
  queue.enqueue({ id: "1", text: "First" });
  queue.enqueue({ id: "2", text: "Second" });
  await assert.rejects(
    queue.flush(async () => {
      throw new Error("Connection lost");
    }),
    /Connection lost/,
  );
  assert.equal(queue.getSnapshot().paused, true);
  assert.deepEqual(
    queue.getSnapshot().pending.map((m) => m.id),
    ["2"],
  );
  const received: string[] = [];
  await queue.flush(async ({ id }) => {
    received.push(id);
  });
  assert.equal(received.length, 0);
  queue.resume();
  await queue.flush(async ({ id }) => {
    received.push(id);
  });
  assert.deepEqual(received, ["2"]);
});

test("a stop while a reply is running leaves later messages available to resume", async () => {
  const queue = new ConversationQueue();
  const gate = deferred();
  queue.enqueue({ id: "1", text: "First" });
  queue.enqueue({ id: "2", text: "Second" });
  const running = queue.flush(() => gate.promise);
  queue.pause();
  gate.resolve();
  await running;
  assert.deepEqual(
    queue.getSnapshot().pending.map((m) => m.id),
    ["2"],
  );
});

function deferred() {
  let resolve: () => void = () => {};
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
