import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { URL } from "node:url";
import test from "node:test";
import ts from "typescript";
import { agentChinese, agentText } from "../src/agent-translations";

test("every agent UI translation has Chinese text and preserves template fields", () => {
  for (const [english, chinese] of Object.entries(agentChinese)) {
    assert.match(chinese, /[\u4e00-\u9fff]/u, english);
    assert.equal(agentText("en", english), english);
    assert.deepEqual(chinese.match(/\{\w+\}/g)?.sort(), english.match(/\{\w+\}/g)?.sort(), english);
  }
});

test("display translation preserves user text, brands, IDs and dynamic values", () => {
  for (const value of ["My personal goal", "用户自己写的内容", "Gmail", "waiting_input", "health"]) {
    assert.equal(agentText("zh", value), value);
  }
  assert.equal(agentText("zh", "Open goal: {name}", { name: "My plan" }), "打开目标：My plan");
  assert.equal(agentText("zh", "{done}/{total} steps", { done: 2, total: 5 }), "2/5 步");
  assert.equal(agentText("en", "Show {count} more", { count: 4 }), "Show 4 more");
});

test("four agent pages and their forms have no untranslated static JSX copy", () => {
  const scopes = new Set(["AgentStatus", "TaskCard", "AgentActivityScreen", "EvidenceList", "TaskDetail",
    "IdeasScreen", "TaskLink", "IdeaCard", "GoalsScreen", "GoalForm", "GoalCard", "MonitorForm",
    "MonitorCard", "AppsScreen", "MemoryRow", "ActivityScreen", "ConnectionsScreen"]);
  const attributes = new Set(["title", "detail", "label", "placeholder", "accessibilityLabel", "subtitle"]);
  const allowed = new Set(["https://example.com/product"]);
  for (const file of ["agent-ui.tsx", "screens.tsx"]) {
    const source = ts.createSourceFile(file, readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    function visit(node: ts.Node, scope = "") {
      if (ts.isFunctionDeclaration(node)) scope = node.name?.text || "";
      if (scopes.has(scope)) {
        const literal = ts.isJsxText(node) ? node.text.trim()
          : ts.isJsxAttribute(node) && attributes.has(node.name.getText(source)) && node.initializer && ts.isStringLiteral(node.initializer)
            ? node.initializer.text : "";
        assert.ok(!/[A-Za-z]{2}/.test(literal) || allowed.has(literal), `${file}:${scope}: ${literal}`);
        if (ts.isCallExpression(node) && node.expression.getText(source) === "tr" && node.arguments[0] && ts.isStringLiteral(node.arguments[0]))
          assert.ok(Object.hasOwn(agentChinese, node.arguments[0].text), `Missing dictionary entry: ${node.arguments[0].text}`);
      }
      ts.forEachChild(node, child => visit(child, scope));
    }
    visit(source);
  }
});
