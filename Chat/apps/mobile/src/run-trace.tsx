import type { AbstractAgent, Message } from "@copilotkit/react-native/headless";
import {
  BookOpen,
  Bookmark,
  Brain,
  Check,
  ChevronDown,
  ChevronRight,
  Eye,
  Globe,
  Hourglass,
  type LucideIcon,
  Mail,
  MailOpen,
  MessageSquareText,
  Route,
  Send,
  Target,
  Terminal,
  Activity,
  Wrench,
  X,
} from "lucide-react-native";
import { useEffect, useRef, useState } from "react";
import { ActivityIndicator, Animated, Pressable, ScrollView, Text, View } from "react-native";
import { z } from "zod";
import { useI18n } from "./i18n";
import {
  argSummary,
  buildSteps,
  formatDuration,
  resultFailed,
  type RouteInfo,
  runSpan,
  stepDuration,
  type StepTimes,
  type TraceStep,
  toolLabel,
} from "./run-steps";
import { colors, CopyButton, s, useReducedMotion } from "./ui";
import { useWorkspace } from "./workspace";

// Agent workflow and run stats under each reply. Stats come from the server's CUSTOM
// `botlan-run-stats` event (see engine/tanstack-agent.ts); step timings are recorded here from
// AG-UI events. Both live for this session only.

const statsSchema = z.object({
  steps: z.number(),
  outputTokens: z.number(),
  contextTokens: z.number(),
  tokensPerSecond: z.number().nullable(),
  firstTokenMs: z.number().nullable(),
  totalMs: z.number(),
});
export type RunStats = z.output<typeof statsSchema>;

/** Stats keyed by the id of the last assistant message of each finished run. */
export function useRunStats(agent: AbstractAgent) {
  const [byMessage, setByMessage] = useState<Record<string, RunStats>>({});
  useEffect(() => {
    let pending: RunStats | undefined;
    const sub = agent.subscribe({
      onCustomEvent: ({ event }) => {
        if (event.name !== "botlan-run-stats") return;
        const parsed = statsSchema.safeParse(event.value);
        if (parsed.success) pending = parsed.data;
      },
      onRunFinalized: ({ messages }) => {
        const last = [...messages].reverse().find((m) => m.role === "assistant");
        if (pending && last) {
          const value = pending;
          setByMessage((current) => ({ ...current, [last.id]: value }));
        }
        pending = undefined;
      },
    });
    return () => sub.unsubscribe();
  }, [agent]);
  return byMessage;
}

/**
 * Client-side start / end times of every tool call, text message and reasoning message,
 * from the AG-UI event stream. Keyed by toolCallId / messageId.
 */
export function useStepTimes(agent: AbstractAgent) {
  const [times, setTimes] = useState<StepTimes>({});
  useEffect(() => {
    const mark = (id: string | undefined, edge: "start" | "end") => {
      if (!id) return;
      const at = Date.now();
      setTimes((current) => {
        const prev = current[id];
        if (edge === "start" && prev?.start) return current;
        return { ...current, [id]: { ...prev, [edge]: at } };
      });
    };
    const sub = agent.subscribe({
      onToolCallStartEvent: ({ event }) => mark(event.toolCallId, "start"),
      onToolCallResultEvent: ({ event }) => mark(event.toolCallId, "end"),
      onTextMessageStartEvent: ({ event }) => mark(event.messageId, "start"),
      onTextMessageEndEvent: ({ event }) => mark(event.messageId, "end"),
      onReasoningMessageStartEvent: ({ event }) => mark(event.messageId, "start"),
      onReasoningMessageEndEvent: ({ event }) => mark(event.messageId, "end"),
      onEvent: ({ event }) => {
        // Chunk events (no explicit START) still mark when a message began.
        const e = event as { type?: string; messageId?: string; toolCallId?: string };
        if (e.type === "TEXT_MESSAGE_CHUNK" || e.type === "REASONING_MESSAGE_CHUNK") mark(e.messageId, "start");
        if (e.type === "TOOL_CALL_CHUNK") mark(e.toolCallId, "start");
      },
      onRunFinalized: ({ messages }) => {
        // Close whatever was still open (streams that never sent an END).
        const at = Date.now();
        setTimes((current) => {
          let next = current;
          for (const m of messages) {
            const ids = [m.id, ...("toolCalls" in m && m.toolCalls ? m.toolCalls.map((c) => c.id) : [])];
            for (const id of ids)
              if (next[id]?.start && !next[id].end) next = { ...next, [id]: { ...next[id], end: at } };
          }
          return next;
        });
      },
    });
    return () => sub.unsubscribe();
  }, [agent]);
  return times;
}

/** Context window of the Bot's model, when its gateway reports one (BotLan gateway: per llama slot). */
export function useContextWindow() {
  const { api } = useWorkspace();
  const [size, setSize] = useState<number>();
  useEffect(() => {
    let live = true;
    api.request<{ spark: boolean }>("/api/spark")
      .then((r) => (r.spark ? api.request<{ ctx_per_slot?: number | null }>("/api/spark/health") : undefined))
      .then((h) => live && typeof h?.ctx_per_slot === "number" && h.ctx_per_slot > 0 && setSize(h.ctx_per_slot))
      .catch(() => {});
    return () => { live = false; };
  }, [api]);
  return size;
}

const k = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(n >= 10000 ? 0 : 1)}K` : String(n));

export function RunStatsLine({ stats, contextWindow }: { stats: RunStats; contextWindow?: number }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const parts = [
    stats.tokensPerSecond != null ? `${stats.tokensPerSecond} tok/s` : undefined,
    `${k(stats.outputTokens)} ${zh ? "输出" : "out"}`,
    stats.contextTokens
      ? `${zh ? "上下文" : "context"} ${k(stats.contextTokens)}${contextWindow ? ` / ${k(contextWindow)}` : ""}`
      : undefined,
    stats.firstTokenMs != null ? `${zh ? "首字" : "first token"} ${(stats.firstTokenMs / 1000).toFixed(1)}s` : undefined,
    `${zh ? "共" : "total"} ${(stats.totalMs / 1000).toFixed(1)}s`,
  ].filter(Boolean);
  const full = contextWindow && stats.contextTokens / contextWindow > 0.85;
  return (
    <Text
      accessibilityLabel={zh ? "本次回复的速度与上下文" : "Speed and context for this reply"}
      style={[s.small, { color: full ? colors.danger : colors.muted, fontSize: 11 }]}
    >
      {parts.join(" · ")}
      {full ? (zh ? " · 上下文快满了" : " · context nearly full") : ""}
    </Text>
  );
}

/** Steps of one run, for the timeline. */
export function stepsOf(messages: Message[], from: number, to: number, opts: Parameters<typeof buildSteps>[3] = {}) {
  return buildSteps(messages as Parameters<typeof buildSteps>[0], from, to, opts);
}

const toolIcons: Record<string, LucideIcon> = {
  load_skill: BookOpen,
  run_spark_command: Terminal,
  run_computer_command: Terminal,
  browse_web: Globe,
  search_mail: Mail,
  read_mail_thread: MailOpen,
  delegate_task: Send,
  agent_status: Activity,
  create_goal: Target,
  watch_page: Eye,
  remember_fact: Bookmark,
};

const kindTint: Record<TraceStep["kind"], { bg: string; fg: string }> = {
  route: { bg: "#EEF6E0", fg: "#4E7D00" },
  thinking: { bg: colors.lavender, fg: "#6B5BB5" },
  tool: { bg: colors.sky, fg: colors.blueDark },
  answer: { bg: colors.green, fg: "#2F7A4B" },
};

function stepIcon(step: TraceStep): LucideIcon {
  if (step.kind === "route") return Route;
  if (step.kind === "thinking") return Brain;
  if (step.kind === "answer") return MessageSquareText;
  return toolIcons[step.tool ?? ""] ?? Wrench;
}

/** Re-renders every `ms` while `on`, for live durations. */
function useNow(on: boolean, ms = 250) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!on) return;
    setNow(Date.now());
    const id = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(id);
  }, [on, ms]);
  return now;
}

/** Soft pulsing halo behind the step that is running now. */
function Pulse({ color }: { color: string }) {
  const reduced = useReducedMotion();
  const v = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (reduced) return;
    const loop = Animated.loop(
      Animated.sequence([
        Animated.timing(v, { toValue: 1, duration: 900, useNativeDriver: false }),
        Animated.timing(v, { toValue: 0, duration: 0, useNativeDriver: false }),
      ]),
    );
    loop.start();
    return () => loop.stop();
  }, [reduced, v]);
  if (reduced) return null;
  return (
    <Animated.View
      pointerEvents="none"
      style={{
        position: "absolute", left: 0, top: 0, width: 26, height: 26, borderRadius: 13, borderWidth: 2, borderColor: color,
        opacity: v.interpolate({ inputRange: [0, 1], outputRange: [0.55, 0] }),
        transform: [{ scale: v.interpolate({ inputRange: [0, 1], outputRange: [1, 1.55] }) }],
      }}
    />
  );
}

function StatusBadge({ status }: { status: TraceStep["status"] }) {
  const base = { position: "absolute" as const, right: -3, bottom: -3, width: 13, height: 13, borderRadius: 7,
    borderWidth: 1.5, borderColor: "#FFF", alignItems: "center" as const, justifyContent: "center" as const };
  if (status === "running")
    return (
      <View style={[base, { backgroundColor: "#FFF", borderWidth: 0 }]}>
        <ActivityIndicator size={10} color={colors.blueDark} />
      </View>
    );
  if (status === "waiting")
    return <View style={[base, { backgroundColor: "#E39B2B" }]}><Hourglass size={7} color="#FFF" strokeWidth={3} /></View>;
  if (status === "error")
    return <View style={[base, { backgroundColor: colors.danger }]}><X size={8} color="#FFF" strokeWidth={3.5} /></View>;
  return <View style={[base, { backgroundColor: "#3D8B37" }]}><Check size={8} color="#FFF" strokeWidth={3.5} /></View>;
}

function DetailBlock({ label, text, zh }: { label: string; text: string; zh: boolean }) {
  return (
    <View style={{ gap: 3 }}>
      <View style={[s.between, { minHeight: 26 }]}>
        <Text style={[s.small, { fontWeight: "600", color: colors.text, fontSize: 10, letterSpacing: 0.4 }]}>{label}</Text>
        <CopyButton text={text} label={zh ? `复制${label}` : `Copy ${label.toLowerCase()}`} copiedLabel={zh ? "已复制" : "Copied"}
          failedLabel={zh ? "复制失败" : "Copy failed"} />
      </View>
      <ScrollView horizontal style={{ backgroundColor: "#F5F6F7", borderRadius: 8, maxHeight: 220 }}
        contentContainerStyle={{ padding: 8 }}>
        <ScrollView nestedScrollEnabled style={{ maxHeight: 204 }}>
          <Text selectable style={{ fontFamily: "monospace", fontSize: 11.5, lineHeight: 17, color: colors.text }}>
            {text.length > 6000 ? `${text.slice(0, 6000)}\n…` : text}
          </Text>
        </ScrollView>
      </ScrollView>
    </View>
  );
}

function StepRow({ step, now, last, zh, forceOpen }: {
  step: TraceStep; now: number; last: boolean; zh: boolean; forceOpen: boolean;
}) {
  const [open, setOpen] = useState(false);
  const Icon = stepIcon(step);
  const tint = kindTint[step.kind];
  const ms = stepDuration(step, now);
  const live = step.status === "running" || step.status === "waiting";
  const expandable = !!(step.thought || step.args || step.result);
  // Live reasoning streams open so the user can follow it.
  const showDetail = open || (forceOpen && step.kind === "thinking" && step.status === "running");
  const statusWord = { running: zh ? "进行中" : "running", waiting: zh ? "等待中" : "waiting",
    done: zh ? "完成" : "done", error: zh ? "失败" : "failed" }[step.status];
  const line = [step.summary, step.why ? `— ${step.why}` : ""].filter(Boolean).join("  ");
  return (
    <View style={{ flexDirection: "row", gap: 10 }}>
      <View style={{ width: 26, alignItems: "center" }}>
        <View style={{ width: 26, height: 26 }}>
          {live && <Pulse color={step.status === "waiting" ? "#E39B2B" : tint.fg} />}
          <View style={{ width: 26, height: 26, borderRadius: 13, backgroundColor: step.status === "error" ? "#FBEFED" : tint.bg,
            alignItems: "center", justifyContent: "center" }}>
            <Icon size={13} color={step.status === "error" ? colors.danger : tint.fg} strokeWidth={2} />
          </View>
          <StatusBadge status={step.status} />
        </View>
        {!last && <View style={{ flex: 1, width: 1.5, backgroundColor: colors.line, marginVertical: 2, minHeight: 8 }} />}
      </View>
      <View style={{ flex: 1, paddingBottom: last ? 0 : 10, gap: 4, minWidth: 0 }}>
        <Pressable
          accessibilityRole={expandable ? "button" : "text"}
          accessibilityLabel={`${step.title}, ${statusWord}${ms != null ? `, ${formatDuration(ms)}` : ""}${line ? `. ${line}` : ""}`}
          accessibilityState={expandable ? { expanded: showDetail } : undefined}
          disabled={!expandable}
          onPress={() => setOpen(!showDetail)}
          style={({ pressed }) => ({ borderRadius: 8, paddingVertical: 2, opacity: pressed ? 0.6 : 1 })}
        >
          <View style={[s.row, { gap: 6, minHeight: 22 }]}>
            <Text style={[s.small, { fontWeight: "600", color: step.status === "error" ? colors.danger : colors.text, fontSize: 12 }]}>
              {step.title}
            </Text>
            {expandable && (showDetail
              ? <ChevronDown size={12} color={colors.muted} />
              : <ChevronRight size={12} color={colors.muted} />)}
            <View style={{ flex: 1 }} />
            {ms != null && (
              <Text style={[s.small, { fontVariant: ["tabular-nums"], color: live ? colors.blueDark : colors.muted }]}>
                {formatDuration(ms)}
              </Text>
            )}
          </View>
          {!!line && (
            <Text numberOfLines={showDetail ? 4 : 1} style={[s.small, {
              fontFamily: step.kind === "tool" ? "monospace" : undefined,
              fontStyle: step.kind === "thinking" ? "italic" : undefined,
              color: step.status === "error" || step.why && step.status !== "waiting" ? colors.danger : colors.muted,
            }]}>
              {line}
            </Text>
          )}
          {!!step.preview && !showDetail && (
            <Text numberOfLines={1} style={[s.small, { color: "#3D6B4A" }]}>↳ {step.preview}</Text>
          )}
        </Pressable>
        {showDetail && (
          <View style={{ gap: 6 }}>
            {!!step.thought && <DetailBlock label={zh ? "思考过程" : "Reasoning"} text={step.thought} zh={zh} />}
            {!!step.args && step.args !== "{}" && <DetailBlock label={zh ? "参数" : "Arguments"} text={step.args} zh={zh} />}
            {!!step.result && <DetailBlock label={zh ? "结果" : "Result"} text={step.result} zh={zh} />}
          </View>
        )}
      </View>
    </View>
  );
}

/**
 * Reasoning-steps timeline shown once per run above its answer: Jev route, thinking, each tool
 * call (icon, args, live duration, result preview, expandable detail), approvals, the answer.
 * Compact by default, open while the run is live.
 */
export function RunTimeline({ messages, from, to, running, route, times, pendingCommands }: {
  messages: Message[];
  from: number;
  to: number;
  running: boolean;
  route?: RouteInfo;
  times?: StepTimes;
  pendingCommands?: string[];
}) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const steps = stepsOf(messages, from, to, { zh, running, route, times, pendingCommands });
  const [open, setOpen] = useState(running);
  const wasRunning = useRef(running);
  useEffect(() => {
    if (running) setOpen(true);
    else if (wasRunning.current) setOpen(false);
    wasRunning.current = running;
  }, [running]);
  const live = steps.some((st) => st.status === "running" || st.status === "waiting");
  const now = useNow(running && live);
  const work = steps.filter((st) => st.kind !== "answer");
  if (!work.length || (work.length === 1 && work[0].kind === "route" && !running)) return null;
  const errors = steps.filter((st) => st.status === "error").length;
  const waiting = steps.some((st) => st.status === "waiting");
  const span = runSpan(steps, now);
  const current = running ? steps.find((st) => st.status === "running" || st.status === "waiting") : undefined;
  const tools = steps.filter((st) => st.kind === "tool").length;
  const header = [
    zh ? "工作流" : "Workflow",
    zh ? `${steps.length} 步` : `${steps.length} step${steps.length > 1 ? "s" : ""}`,
    tools ? (zh ? `${tools} 次工具调用` : `${tools} tool call${tools > 1 ? "s" : ""}`) : "",
    span != null && span > 0 ? formatDuration(span) : "",
    errors ? (zh ? `${errors} 步失败` : `${errors} failed`) : "",
  ].filter(Boolean).join(" · ");
  return (
    <View style={{ borderWidth: 1, borderColor: colors.line, borderRadius: 16, backgroundColor: colors.card, overflow: "hidden" }}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={header}
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen(!open)}
        style={({ pressed }) => [s.row, { gap: 8, paddingHorizontal: 12, paddingVertical: 9,
          backgroundColor: pressed ? colors.canvas : "transparent" }]}
      >
        {running ? <ActivityIndicator size="small" color={waiting ? "#E39B2B" : colors.blueDark} />
          : errors ? <X size={14} color={colors.danger} strokeWidth={2.5} />
            : <Check size={14} color="#3D8B37" strokeWidth={2.5} />}
        <View style={{ flex: 1, minWidth: 0 }}>
          <Text numberOfLines={1} style={[s.small, { fontWeight: "600", color: colors.text, fontSize: 12 }]}>{header}</Text>
          {!open && current && (
            <Text numberOfLines={1} style={s.small}>
              {current.status === "waiting" ? (zh ? "等待批准 · " : "Waiting for approval · ") : ""}
              {current.title}{current.summary ? ` · ${current.summary}` : ""}
            </Text>
          )}
        </View>
        {open ? <ChevronDown size={15} color={colors.muted} /> : <ChevronRight size={15} color={colors.muted} />}
      </Pressable>
      {open && (
        <View style={{ paddingHorizontal: 12, paddingTop: 4, paddingBottom: 12 }}>
          {steps.map((st, i) => (
            <StepRow key={st.id} step={st} now={now} last={i === steps.length - 1} zh={zh} forceOpen={running} />
          ))}
        </View>
      )}
    </View>
  );
}

/** Card for tools without a dedicated one, so no step is invisible. */
export function GenericToolCard({ name, args, result, loading }: {
  name: string; args: Record<string, unknown>; result: unknown; loading: boolean;
}) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const [open, setOpen] = useState(false);
  const label = toolLabel(name, zh);
  const text = typeof result === "string" ? result : result == null ? "" : JSON.stringify(result, null, 2);
  const failed = resultFailed(text);
  const Icon = toolIcons[name] ?? Wrench;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      onPress={() => setOpen(!open)}
      style={{ borderWidth: 1, borderColor: colors.line, borderRadius: 14, padding: 10, gap: 6, backgroundColor: colors.card }}
    >
      <View style={[s.row, { gap: 6 }]}>
        {loading ? <ActivityIndicator size={12} color={colors.blueDark} />
          : <Icon size={13} color={failed ? colors.danger : colors.blueDark} />}
        <Text numberOfLines={1} style={[s.small, { flex: 1, fontWeight: "700", color: failed ? colors.danger : colors.text }]}>
          {label}
          <Text style={{ fontWeight: "400", color: colors.muted, fontFamily: "monospace" }}>
            {`  ${argSummary(name, JSON.stringify(args)).slice(0, 120)}`}
          </Text>
        </Text>
      </View>
      {open && !!text && (
        <Text selectable style={{ fontFamily: "monospace", fontSize: 12, color: colors.text }} numberOfLines={30}>
          {text.slice(0, 4000)}
        </Text>
      )}
    </Pressable>
  );
}
