import assert from "node:assert/strict";
import test from "node:test";
import { browserAddress } from "../src/browser-address.ts";

test("the browser address bar accepts domains and keeps explicit web URLs", () => {
  assert.equal(browserAddress(" copilotkit.ai "), "https://copilotkit.ai/");
  assert.equal(
    browserAddress("news.ycombinator.com/newest"),
    "https://news.ycombinator.com/newest",
  );
  assert.equal(browserAddress("http://example.com/?q=one#two"), "http://example.com/?q=one#two");
});
test("the address bar rejects unsupported schemes, credentials, and malformed inputs", () => {
  for (const input of [
    "",
    "file:///etc/passwd",
    "javascript:alert(1)",
    "https://user:secret@example.com",
    "hello world",
  ])
    assert.throws(() => browserAddress(input), /Enter a website address/);
});
