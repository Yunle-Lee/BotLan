import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

test("Code workbench has no bundled default API credential", () => {
  const source = readFileSync(new URL("../src/code-workbench.tsx", import.meta.url), "utf8");
  assert.match(source, /const \[customApiKey, setCustomApiKey\] = useState\(""\)/);
  assert.doesNotMatch(source, /\bsk-[A-Za-z0-9_-]{20,}/);
});

test("Opening Code never seeds a model credential into browser storage", () => {
  const source = readFileSync(new URL("../src/code-workbench.tsx", import.meta.url), "utf8");
  const onMount = source.slice(source.indexOf("// Load saved model config"), source.indexOf("// Poll Computer Snapshot"));
  assert.ok(onMount.includes("localStorage.getItem"));
  assert.doesNotMatch(onMount, /localStorage\.setItem/);
});
