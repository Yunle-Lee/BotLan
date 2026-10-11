// Pure helpers behind the reply workflow timeline (run-trace.tsx). No React Native imports, so
// they can be unit-tested under node.

export type StepKind = "route" | "thinking" | "tool" | "answer";
export type StepStatus = "running" | "waiting" | "done" | "error";

export interface TraceStep {
  id: string;
  kind: StepKind;
  /** Tool name for kind "tool". */
  tool?: string;
  title: string;
  /** One-line summary: tool arguments, route decision, first words of the reasoning. */
  summary: string;
  status: StepStatus;
  /** Short reason a step did not succeed or is waiting. */
  why?: string;
  /** One-line preview of what a finished step returned. */
  preview?: string;
  /** Expandable detail: reasoning text, or pretty arguments and result of a tool. */
  thought?: string;
  args?: string;
  result?: string;
  startedAt?: number;
  endedAt?: number;
  /** Duration known without timestamps (Jev reports its own latency). */
  ms?: number;
}

/** Client-side timestamps per tool call / message id, recorded from AG-UI events. */
export type StepTimes = Record<string, { start?: number; end?: number }>;

export interface RouteInfo {
  skill?: string;
  confidence?: number;
  ms?: number | null;
  used?: boolean;
}

type ToolCallLike = { id: string; function: { name: string; arguments: string } };
export type MessageLike = {
  id: string;
  role: string;
  content?: unknown;
  toolCalls?: ToolCallLike[];
  toolCallId?: string;
};

export const stepLabels: Record<string, [string, string]> = {
  load_skill: ["加载技能", "Load skill"],
  run_spark_command: ["在 Spark 上执行", "Run on Spark"],
  browse_web: ["读取网页", "Read web page"],
  search_mail: ["搜索邮件", "Search mail"],
  read_mail_thread: ["读取邮件", "Read email"],
  delegate_task: ["创建后台任务", "Delegate task"],
  agent_status: ["查看进度", "Check progress"],
  create_goal: ["保存目标", "Save goal"],
  watch_page: ["监控网页", "Watch page"],
  remember_fact: ["记住信息", "Remember"],
  run_computer_command: ["在计算容器执行", "Run in container"],
};

export const toolLabel = (name: string, zh: boolean) => (stepLabels[name] ?? [name, name])[zh ? 0 : 1];

export function argSummary(name: string, raw: string) {
  try {
    const a = JSON.parse(raw || "{}") as Record<string, unknown>;
    const pick = a.command ?? a.id ?? a.skillId ?? a.url ?? a.query ?? a.prompt ?? a.title;
    const text = typeof pick === "string" ? pick : Object.keys(a).length ? JSON.stringify(a) : "";
    return name === "run_spark_command" && text ? `$ ${text}` : text;
  } catch {
    return "";
  }
}

/** Short reason a step did not succeed: denied, timed out, the error text or a non-zero exit. */
export function failureReason(content: unknown, zh: boolean) {
  if (typeof content !== "string") return undefined;
  try {
    const r = JSON.parse(content) as { error?: unknown; status?: unknown; exit_code?: unknown; timed_out?: unknown };
    if (r.status === "denied") return zh ? "已拒绝" : "denied";
    if (r.status === "expired") return zh ? "审批超时" : "approval expired";
    if (r.timed_out) return zh ? "执行超时" : "timed out";
    if (typeof r.error === "string") return r.error.slice(0, 80);
    if (typeof r.exit_code === "number" && r.exit_code !== 0) return `exit ${r.exit_code}`;
  } catch {}
  return undefined;
}

export function resultFailed(content: unknown) {
  if (typeof content !== "string") return false;
  try {
    const r = JSON.parse(content) as { error?: unknown; ran?: unknown };
    return !!r?.error || r?.ran === false;
  } catch {
    return /^error/i.test(content);
  }
}

const oneLine = (text: string, max = 100) => {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length > max ? `${line.slice(0, max - 1)}…` : line;
};

/** One-line preview of a tool result: its output / summary / title, or how much came back. */
export function resultPreview(content: unknown, zh: boolean) {
  if (typeof content !== "string" || !content.trim()) return undefined;
  let value: unknown;
  try {
    value = JSON.parse(content);
  } catch {
    return oneLine(content);
  }
  if (typeof value === "string") return oneLine(value);
  if (Array.isArray(value)) return zh ? `${value.length} 条结果` : `${value.length} results`;
  if (value && typeof value === "object") {
    const r = value as Record<string, unknown>;
    for (const key of ["summary", "output", "text", "title", "name", "message", "status"]) {
      const v = r[key];
      if (typeof v === "string" && v.trim()) return oneLine(v);
    }
    for (const v of Object.values(r))
      if (Array.isArray(v)) return zh ? `${v.length} 条结果` : `${v.length} results`;
    const keys = Object.keys(r);
    return keys.length ? oneLine(keys.join(", ")) : undefined;
  }
  return oneLine(String(value));
}

function pretty(raw: unknown) {
  if (typeof raw !== "string") return raw == null ? "" : JSON.stringify(raw, null, 2);
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

const text = (m: MessageLike) => (typeof m.content === "string" ? m.content : "");

/**
 * Steps of one run (messages[from..to)): the Jev route, reasoning, every tool call and the final
 * answer, in the order they happened. `running` marks the run still streaming.
 */
export function buildSteps(
  messages: MessageLike[],
  from: number,
  to: number,
  opts: {
    zh?: boolean;
    running?: boolean;
    times?: StepTimes;
    route?: RouteInfo;
    /** Commands of Spark approvals still waiting for the user. */
    pendingCommands?: string[];
  } = {},
): TraceStep[] {
  const zh = !!opts.zh;
  const times = opts.times ?? {};
  const steps: TraceStep[] = [];
  const route = opts.route;
  if (route?.skill && route.skill !== "none")
    steps.push({
      id: "jev-route",
      kind: "route",
      title: zh ? "Jev 技能路由" : "Jev skill routing",
      summary: `→ ${route.skill} · ${route.confidence?.toFixed(2) ?? "?"}${route.used === false ? (zh ? "（未采用）" : " (not used)") : ""}`,
      status: "done",
      ms: route.ms ?? undefined,
    });
  let finalAnswer: MessageLike | undefined;
  let finalIndex = -1, lastToolIndex = -1;
  for (let i = from; i < to; i++) {
    const m = messages[i];
    if (m.role === "reasoning" && text(m).trim()) {
      const t = times[m.id];
      steps.push({
        id: m.id,
        kind: "thinking",
        title: zh ? "思考" : "Thinking",
        summary: oneLine(text(m), 90),
        status: opts.running && !t?.end ? "running" : "done",
        thought: text(m),
        startedAt: t?.start,
        endedAt: t?.end,
      });
    }
    if (m.role !== "assistant") continue;
    if (text(m).trim()) { finalAnswer = m; finalIndex = i; }
    if (m.toolCalls?.length) lastToolIndex = i;
    for (const call of m.toolCalls ?? []) {
      const done = messages.find((c) => c.role === "tool" && c.toolCallId === call.id);
      const name = call.function.name;
      const t = times[call.id];
      let command = "";
      try {
        const a = JSON.parse(call.function.arguments || "{}") as { command?: unknown };
        if (typeof a.command === "string") command = a.command;
      } catch {}
      const waiting = !done && opts.running && name === "run_spark_command" && !!command
        && !!opts.pendingCommands?.includes(command);
      const status: StepStatus = done
        ? resultFailed(done.content) ? "error" : "done"
        : waiting ? "waiting" : opts.running ? "running" : "error";
      steps.push({
        id: call.id,
        kind: "tool",
        tool: name,
        title: toolLabel(name, zh),
        summary: argSummary(name, call.function.arguments),
        status,
        why: done
          ? failureReason(done.content, zh)
          : waiting ? (zh ? "等待你批准" : "waiting for your approval")
            : opts.running ? undefined : (zh ? "未完成" : "did not finish"),
        preview: done && status === "done" ? resultPreview(done.content, zh) : undefined,
        args: pretty(call.function.arguments),
        result: done ? pretty(done.content) : undefined,
        startedAt: t?.start,
        endedAt: t?.end,
      });
    }
  }
  // The answer only earns a row when there was work before it.
  if (finalAnswer && steps.length && finalIndex >= lastToolIndex) {
    const t = times[finalAnswer.id];
    steps.push({
      id: `answer-${finalAnswer.id}`,
      kind: "answer",
      title: zh ? "回答" : "Answer",
      summary: oneLine(text(finalAnswer), 90),
      status: opts.running ? "running" : "done",
      startedAt: t?.start,
      endedAt: t?.end,
    });
  }
  return steps;
}

/** 420ms · 3.2s · 1m 05s */
export function formatDuration(ms: number | undefined | null) {
  if (ms == null || !Number.isFinite(ms) || ms < 0) return "";
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`;
  const m = Math.floor(ms / 60_000);
  const sec = Math.floor((ms % 60_000) / 1000);
  return `${m}m ${String(sec).padStart(2, "0")}s`;
}

/** Duration of a step: its own report, or its timestamps (live against `now` while running). */
export function stepDuration(step: TraceStep, now: number) {
  if (step.ms != null) return step.ms;
  if (step.startedAt == null) return undefined;
  if (step.endedAt != null) return step.endedAt - step.startedAt;
  return step.status === "running" || step.status === "waiting" ? now - step.startedAt : undefined;
}

/** Wall time the timed steps span (first start to last end). */
export function runSpan(steps: TraceStep[], now: number) {
  const starts = steps.map((s) => s.startedAt).filter((v): v is number => v != null);
  if (!starts.length) return undefined;
  const ends = steps.map((s) => s.endedAt ?? (s.status === "running" || s.status === "waiting" ? now : s.startedAt))
    .filter((v): v is number => v != null);
  return Math.max(...ends) - Math.min(...starts);
}
