// One-off source migration. Emits a reviewable apply_patch; never writes source files.
import fs from "node:fs";
import path from "node:path";
import ts from "typescript";
const dictionary = ts.createSourceFile("translations.ts", fs.readFileSync("apps/mobile/src/agent-translations.ts", "utf8"), ts.ScriptTarget.Latest, true);
const words = new Set();
function keys(node) { if (ts.isPropertyAssignment(node) && ts.isStringLiteral(node.name)) words.add(node.name.text); ts.forEachChild(node, keys); }
keys(dictionary);
const targets = {
  "apps/mobile/src/agent-ui.tsx": new Set(["AgentStatus", "TaskCard", "AgentActivityScreen", "EvidenceList", "TaskDetail", "IdeasScreen", "TaskLink", "IdeaCard", "GoalsScreen", "GoalForm", "GoalCard", "MonitorForm", "MonitorCard", "AppsScreen", "MemoryRow"]),
  "apps/mobile/src/screens.tsx": new Set(["ActivityScreen", "ConnectionsScreen"]),
};
let patch = "*** Begin Patch\n";
for (const [file, functions] of Object.entries(targets)) {
  const source = fs.readFileSync(file, "utf8").replace(/\r\n/g, "\n");
  const ast = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const edits = [];
  const add = (start, end, text) => edits.push({ start, end, text });
  const wrap = node => add(node.getStart(ast), node.end, `tr(${node.getText(ast)})`);
  const attributes = new Set(["label", "title", "detail", "subtitle", "placeholder", "accessibilityLabel", "value"]);
  function displayed(node) {
    for (let p = node.parent; p; p = p.parent) {
      if (ts.isJsxExpression(p)) return !ts.isJsxAttribute(p.parent) || attributes.has(p.parent.name.getText(ast));
      if (ts.isFunctionDeclaration(p)) return false;
    }
    return false;
  }
  function walk(node) {
    if (ts.isJsxText(node)) {
      const raw = node.getText(ast), text = raw.replace(/\s+/g, " ").trim();
      if (words.has(text)) add(node.getStart(ast), node.end, raw.replace(/\S[\s\S]*\S|\S/, `{tr(${JSON.stringify(text)})}`));
    } else if (ts.isStringLiteral(node) && words.has(node.text)) {
      if (ts.isJsxAttribute(node.parent) && attributes.has(node.parent.name.getText(ast)))
        add(node.getStart(ast), node.end, `{tr(${JSON.stringify(node.text)})}`);
      else if (ts.isConditionalExpression(node.parent) && node.parent.condition !== node && displayed(node)) wrap(node);
      else if (ts.isCallExpression(node.parent) && node.parent.expression.getText(ast) === "notify") wrap(node);
      else if (ts.isNewExpression(node.parent) && node.parent.expression.getText(ast) === "Error") wrap(node);
    } else if (ts.isCallExpression(node) && ["statusLabel", "stamp", "capabilityLabel"].includes(node.expression.getText(ast)) && displayed(node)) {
      wrap(node); return;
    }
    ts.forEachChild(node, walk);
  }
  for (const statement of ast.statements) {
    if (!ts.isFunctionDeclaration(statement) || !functions.has(statement.name?.text) || !statement.body) continue;
    const count = edits.length;
    walk(statement.body);
    if (edits.length > count) add(statement.body.getStart(ast) + 1, statement.body.getStart(ast) + 1, '\n  const { a: tr } = useI18n();');
  }
  if (!source.includes('from "./i18n"')) add(0, 0, 'import { useI18n } from "./i18n";\n');
  // Adjacent edits become small exact-match hunks, preserving all non-display code.
  const starts = [0]; for (let i = 0; i < source.length; i++) if (source[i] === "\n") starts.push(i + 1);
  const lineAt = pos => { let i = 0; while (i + 1 < starts.length && starts[i + 1] <= pos) i++; return i; };
  const groups = [];
  for (const edit of edits.sort((a, b) => a.start - b.start)) {
    const first = Math.max(0, lineAt(edit.start) - 2), last = Math.min(starts.length - 1, lineAt(edit.end) + 3);
    const previous = groups.at(-1);
    if (previous && first <= previous.last) { previous.last = Math.max(previous.last, last); previous.edits.push(edit); }
    else groups.push({ first, last, edits: [edit] });
  }
  patch += `*** Update File: ${path.resolve(file).replaceAll("\\", "/")}\n`;
  for (const group of groups) {
    const start = starts[group.first], end = starts[group.last] ?? source.length;
    const before = source.slice(start, end).replace(/\n$/, "");
    let after = source.slice(start, end);
    for (const edit of group.edits.sort((a, b) => b.start - a.start)) after = after.slice(0, edit.start - start) + edit.text + after.slice(edit.end - start);
    patch += "@@\n" + before.split("\n").map(line => "-" + line).join("\n") + "\n"
      + after.replace(/\n$/, "").split("\n").map(line => "+" + line).join("\n") + "\n";
  }
}
process.stdout.write(patch + "*** End Patch\n");
