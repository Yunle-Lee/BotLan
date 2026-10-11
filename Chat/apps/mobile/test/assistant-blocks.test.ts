import assert from "node:assert/strict";
import { test } from "node:test";
import { codeBlocks, fenceLanguage, isLongOutput, withTaskLists } from "../src/assistant-markdown.ts";

const fence = "```";

test("fenced code blocks keep their language label and exact code", () => {
  const md = [
    "Run this:", "", `${fence}ts title=x`, "const a = 1;", fence, "",
    fence, "plain", fence, "", "    indented", "",
  ].join("\n");
  assert.deepEqual(codeBlocks(md), [
    { language: "TypeScript", code: "const a = 1;" },
    { language: "", code: "plain" },
    { language: "", code: "indented" },
  ]);
  assert.equal(fenceLanguage("PowerShell"), "PowerShell");
  assert.equal(fenceLanguage("rust"), "rust");
  assert.equal(fenceLanguage("<script>"), "");
  assert.equal(fenceLanguage(""), "");
});

test("task lists become ballot boxes, except inside code", () => {
  const md = ["- [ ] todo", "- [x] done", "1. [X] numbered", fence, "- [ ] code", fence].join("\n");
  assert.equal(
    withTaskLists(md),
    ["- ☐ todo", "- ☑ done", "1. ☑ numbered", fence, "- [ ] code", fence].join("\n"),
  );
});

test("only long replies collapse", () => {
  assert.equal(isLongOutput("short"), false);
  assert.equal(isLongOutput("x".repeat(3000)), true);
  assert.equal(isLongOutput(Array(60).fill("line").join("\n")), true);
});
