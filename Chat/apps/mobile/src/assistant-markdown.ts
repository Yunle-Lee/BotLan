import MarkdownIt from "markdown-it";

export const assistantMarkdown = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  typographer: false,
});

export function isSafeAssistantUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") && !!url.hostname;
  } catch {
    return false;
  }
}

const languageNames: Record<string, string> = {
  js: "JavaScript", jsx: "JSX", ts: "TypeScript", tsx: "TSX", py: "Python", sh: "Shell",
  bash: "Bash", zsh: "Shell", ps1: "PowerShell", powershell: "PowerShell", cs: "C#", csharp: "C#",
  rb: "Ruby", rs: "Rust", go: "Go", json: "JSON", yaml: "YAML", yml: "YAML", md: "Markdown",
  html: "HTML", css: "CSS", sql: "SQL", java: "Java", kt: "Kotlin", cpp: "C++", c: "C",
  xml: "XML", toml: "TOML", diff: "Diff", dockerfile: "Dockerfile",
};

/** Display label for a fenced block's info string ("ts title=x" -> "TypeScript"); "" when none. */
export function fenceLanguage(info: string | undefined) {
  const id = (info || "").trim().split(/[\s{]/)[0]?.toLowerCase() ?? "";
  if (!id || !/^[\w#+.-]{1,24}$/.test(id)) return "";
  return languageNames[id] ?? id;
}

/** Fenced code blocks of a Markdown text, in order (used for tests and copy actions). */
export function codeBlocks(markdown: string) {
  return assistantMarkdown
    .parse(markdown, {})
    .filter((t) => t.type === "fence" || t.type === "code_block")
    .map((t) => ({ language: fenceLanguage(t.info), code: t.content.replace(/\n$/, "") }));
}

/**
 * GitHub task-list items ("- [ ] x", "- [x] y") become ballot boxes, since markdown-it has no
 * task-list rule. Text inside fenced code blocks is left alone.
 */
export function withTaskLists(markdown: string) {
  let fence = "";
  return markdown
    .split("\n")
    .map((line) => {
      const open = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
      if (open) {
        if (!fence) fence = open[1][0];
        else if (open[1][0] === fence) fence = "";
        return line;
      }
      if (fence) return line;
      return line.replace(/^(\s*(?:[-*+]|\d+[.)])\s+)\[([ xX])\]\s+/, (_m, lead: string, mark: string) =>
        `${lead}${mark === " " ? "☐" : "☑"} `);
    })
    .join("\n");
}

/** Replies this long start collapsed (the user can expand them). */
export function isLongOutput(markdown: string) {
  return markdown.length > 2400 || markdown.split("\n").length > 48;
}
