import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const source = () =>
  readFileSync(fileURLToPath(new URL("../src/code-workbench.tsx", import.meta.url).href), "utf8");

test("Code workbench has no bundled default API credential", () => {
  const text = source();
  assert.match(text, /const \[customApiKey, setCustomApiKey\] = useState\(""\)/);
  assert.doesNotMatch(text, /\bsk-[A-Za-z0-9_-]{20,}/);
});

test("Opening Code never seeds a model credential into browser storage", () => {
  const text = source();
  const onMount = text.slice(text.indexOf("// Load saved model config"), text.indexOf("// Poll Computer Snapshot"));
  assert.ok(onMount.includes("localStorage.getItem"));
  assert.doesNotMatch(onMount, /localStorage\.setItem/);
});
