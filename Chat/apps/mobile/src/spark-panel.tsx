import { Check, Cpu, X } from "lucide-react-native";
import { useCallback, useEffect, useRef, useState } from "react";
import { Animated, Modal, Platform, Pressable, ScrollView, Text, TextInput, useWindowDimensions, View } from "react-native";
import Svg, { Circle } from "react-native-svg";
import { z } from "zod";
import { useI18n } from "./i18n";
import { notifyIsland } from "./island-host";
import { Button, Card, colors, ErrorNotice, s, usePressSpring, useReducedMotion } from "./ui";
import { useWorkspace } from "./workspace";

// Everything here talks to /api/spark on the local backend, which forwards to the Bot's BotLan
// gateway on the DGX Spark. The page never sees the gateway key.

const telemetrySchema = z.object({
  mem_total_gb: z.number().nullable().optional(),
  mem_available_gb: z.number().nullable().optional(),
  gpu: z.object({ name: z.string().optional(), util_pct: z.number().nullable().optional(),
    temp_c: z.number().nullable().optional(), power_w: z.number().nullable().optional() }).partial().optional(),
  gpu_processes: z.array(z.object({ pid: z.number(), name: z.string(), mem_mb: z.number().nullable() })).optional(),
  zone: z.lazy(() => zoneSchema).optional(),
});
const routeSchema = z.object({ at: z.number(), question: z.string().optional(), skill: z.string().optional(),
  confidence: z.number().optional(), ms: z.number().nullable().optional(), used: z.boolean().optional(),
  skills: z.number().optional(), error: z.string().optional() });
const approvalSchema = z.object({ id: z.string(), command: z.string(), reason: z.string(), cwd: z.string().optional() });

const zoneSchema = z.object({
  id: z.string(), name: z.string(), mem_gb: z.number(), cpu_pct: z.number(),
  usage: z.object({ mem_used_gb: z.number(), processes: z.number(), gpu_mem_gb: z.number() }).optional(),
});
const zonesSchema = z.object({
  zones: z.array(zoneSchema), mem_total_gb: z.number(), reserved_gb: z.number(), assignable_gb: z.number(),
});

const GREEN = "#76B900";
// Module constants: usePoll re-subscribes when its schema identity changes.
const routesSchema = z.object({ routes: z.array(routeSchema) });
const approvalsSchema = z.object({ approvals: z.array(approvalSchema) });

export function useSparkEnabled() {
  const { api } = useWorkspace();
  const [spark, setSpark] = useState(false);
  useEffect(() => {
    let live = true;
    api.request<{ spark: boolean }>("/api/spark").then((r) => live && setSpark(r.spark), () => {});
    return () => { live = false; };
  }, [api]);
  return spark;
}

function usePoll<T>(path: string, schema: z.ZodType<T>, ms: number, enabled: boolean) {
  const { api } = useWorkspace();
  const [value, setValue] = useState<T>();
  const [error, setError] = useState("");
  const tick = useCallback(async () => {
    try {
      setValue(schema.parse(await api.request(path)));
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [api, path, schema]);
  useEffect(() => {
    if (!enabled) return;
    void tick();
    const id = setInterval(() => void tick(), ms);
    return () => clearInterval(id);
  }, [enabled, ms, tick]);
  return { value, error, refresh: tick };
}

/** The newest Jev decision, for the reply timeline. Polls only while a Spark Bot is replying. */
export function useLatestRoute(replying: boolean) {
  const spark = useSparkEnabled();
  const routes = usePoll("/api/spark/routes", routesSchema, replying ? 1500 : 15000, spark);
  return routes.value?.routes.at(-1);
}

type Telemetry = z.output<typeof telemetrySchema>;

function memory(t: Telemetry | undefined) {
  const total = t?.mem_total_gb ?? 0;
  const used = total && t?.mem_available_gb != null ? total - t.mem_available_gb : 0;
  return { total, used, pct: total ? Math.min(100, Math.round((used / total) * 100)) : 0 };
}

/** Small ring for the dock button: unified memory in use. */
function MemoryRing({ pct, size }: { pct: number; size: number }) {
  const stroke = 3;
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  return (
    <Svg width={size} height={size} style={{ position: "absolute" }} pointerEvents="none">
      <Circle cx={size / 2} cy={size / 2} r={r} stroke="#E4E7E9" strokeWidth={stroke} fill="none" />
      {pct > 0 && (
        <Circle
          cx={size / 2} cy={size / 2} r={r} stroke={pct > 90 ? colors.danger : GREEN} strokeWidth={stroke} fill="none"
          strokeDasharray={`${(c * pct) / 100} ${c}`} strokeLinecap="round"
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
      )}
    </Svg>
  );
}

type Anchor = { x: number; y: number; width: number; height: number };

/**
 * DGX Spark entry for the left dock: chip icon inside a live memory ring, with a status dot
 * (green online, amber approval waiting, grey offline). Opens SparkPopover beside the dock.
 * Renders nothing for Bots that are not on a Spark.
 */
export function SparkDockButton({ size = 44, replying = false }: { size?: number; replying?: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const spark = useSparkEnabled();
  const telemetry = usePoll("/api/spark/telemetry", telemetrySchema, 4000, spark);
  const approvals = useSparkApprovals(replying, spark);
  const [anchor, setAnchor] = useState<Anchor>();
  const ref = useRef<View>(null);
  const press = usePressSpring(0.88);
  const close = useCallback(() => setAnchor(undefined), []);
  if (!spark) return null;
  const mem = memory(telemetry.value);
  const waiting = approvals.list.length;
  const online = !!telemetry.value && !telemetry.error;
  const dot = waiting ? "#E39B2B" : online ? GREEN : "#ACB0B5";
  const label = zh
    ? `DGX Spark 状态：${online ? `统一内存 ${mem.pct}%` : "离线"}${waiting ? `，${waiting} 条命令待批准` : ""}`
    : `DGX Spark status: ${online ? `unified memory ${mem.pct}%` : "offline"}${waiting ? `, ${waiting} waiting for approval` : ""}`;
  return (
    <>
      <Pressable
        ref={ref}
        nativeID="island-spark-button"
        accessibilityRole="button"
        accessibilityLabel={label}
        accessibilityState={{ expanded: !!anchor }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        onPress={() =>
          anchor ? close() : ref.current?.measureInWindow((x, y, width, height) => setAnchor({ x, y, width, height }))}
        style={({ pressed }) => ({
          width: size, height: size, borderRadius: size / 2, alignItems: "center", justifyContent: "center",
          backgroundColor: pressed || anchor ? colors.line : "#FFFFFF",
        })}
      >
        <Animated.View
          style={{ width: size, height: size, alignItems: "center", justifyContent: "center", transform: [{ scale: press.scale }] }}
        >
          <MemoryRing pct={online ? mem.pct : 0} size={size - 6} />
          <Cpu size={size > 40 ? 18 : 16} strokeWidth={1.8} color={colors.text} />
          <View
            pointerEvents="none"
            style={{ position: "absolute", right: size * 0.1, top: size * 0.1, width: 9, height: 9,
              borderRadius: 5, backgroundColor: dot, borderWidth: 1.5, borderColor: "#FFF" }}
          />
        </Animated.View>
      </Pressable>
      {anchor && <SparkPopover anchor={anchor} onClose={close} telemetry={telemetry} replying={replying} zh={zh} />}
    </>
  );
}

const PANEL_WIDTH = 340;

/** Where the popover goes: right of the anchor when it fits, else below it, kept inside the page. */
export function popoverPlacement(anchor: Anchor, page: { width: number; height: number }, panelWidth = PANEL_WIDTH) {
  const w = Math.min(panelWidth, page.width - 16);
  const right = anchor.x + anchor.width + 10;
  const beside = right + w <= page.width - 8;
  const left = beside ? right : Math.max(8, Math.min(anchor.x, page.width - w - 8));
  const top = beside
    ? Math.max(8, Math.min(anchor.y - 8, page.height - 340))
    : Math.min(anchor.y + anchor.height + 8, page.height - 168);
  return { left, top, width: w, maxHeight: Math.max(160, page.height - top - 8), beside };
}

/** Details next to the dock button; outside click or Esc closes it. */
function SparkPopover({ anchor, onClose, telemetry, replying, zh }: {
  anchor: Anchor;
  onClose: () => void;
  telemetry: { value?: Telemetry; error: string };
  replying: boolean;
  zh: boolean;
}) {
  const page = useWindowDimensions();
  const reduced = useReducedMotion();
  const appear = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (reduced) appear.setValue(1);
    else Animated.spring(appear, { toValue: 1, speed: 22, bounciness: 5, useNativeDriver: Platform.OS !== "web" }).start();
  }, [appear, reduced]);
  useEffect(() => {
    if (Platform.OS !== "web" || typeof document === "undefined") return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);
  const place = popoverPlacement(anchor, page);
  return (
    <Modal transparent visible animationType="none" onRequestClose={onClose}>
      <Pressable
        accessibilityLabel={zh ? "关闭 Spark 面板" : "Close Spark panel"}
        onPress={onClose}
        style={{ position: "absolute", left: 0, top: 0, right: 0, bottom: 0 }}
      />
      <Animated.View
        nativeID="island-spark-popover"
        accessibilityViewIsModal
        style={{
          position: "absolute", left: place.left, top: place.top, width: place.width, maxHeight: place.maxHeight,
          backgroundColor: colors.card, borderRadius: 18, borderWidth: 1, borderColor: colors.line,
          shadowColor: "#18384B", shadowOpacity: 0.14, shadowRadius: 24, shadowOffset: { width: 0, height: 8 }, elevation: 8,
          opacity: appear,
          transform: [
            { translateX: appear.interpolate({ inputRange: [0, 1], outputRange: [place.beside ? -8 : 0, 0] }) },
            { scale: appear.interpolate({ inputRange: [0, 1], outputRange: [0.96, 1] }) },
          ],
        }}
      >
        <ScrollView contentContainerStyle={{ padding: 14, gap: 10 }} keyboardShouldPersistTaps="handled">
          <View style={s.between}>
            <Text style={[s.small, { color: GREEN, fontWeight: "700", fontSize: 12 }]}>
              DGX Spark · {telemetry.value?.gpu?.name ?? "GB10"}
            </Text>
            <Button variant="icon" small icon={X} accessibilityLabel={zh ? "关闭" : "Close"} onPress={onClose} />
          </View>
          <SparkDetails telemetry={telemetry} replying={replying} zh={zh} />
        </ScrollView>
      </Animated.View>
    </Modal>
  );
}

function Meter({ label, value, pct, tint = GREEN }: { label: string; value: string; pct: number; tint?: string }) {
  return (
    <View style={{ gap: 4 }}>
      <View style={s.between}>
        <Text style={[s.small, { color: colors.text, fontWeight: "600" }]}>{label}</Text>
        <Text style={s.small}>{value}</Text>
      </View>
      <View
        accessibilityRole="progressbar"
        accessibilityLabel={label}
        accessibilityValue={{ min: 0, max: 100, now: pct }}
        style={{ height: 6, borderRadius: 3, backgroundColor: colors.line, overflow: "hidden" }}
      >
        <View style={{ width: `${pct}%`, height: "100%", backgroundColor: tint, borderRadius: 3 }} />
      </View>
    </View>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <View style={{ backgroundColor: colors.canvas, borderWidth: 1, borderColor: colors.line, borderRadius: 10, paddingHorizontal: 9, paddingVertical: 5 }}>
      <Text style={[s.small, { fontSize: 10 }]}>{label}</Text>
      <Text style={[s.small, { color: colors.text, fontWeight: "600", fontSize: 12 }]}>{value}</Text>
    </View>
  );
}

/** Unified memory, GPU, GPU processes, the latest Jev route and the zone manager. */
function SparkDetails({ telemetry, replying, zh }: {
  telemetry: { value?: Telemetry; error: string }; replying: boolean; zh: boolean;
}) {
  const routes = usePoll("/api/spark/routes", routesSchema, replying ? 1500 : 6000, true);
  const t = telemetry.value;
  const mem = memory(t);
  const gpu = t?.gpu;
  const last = routes.value?.routes.at(-1);
  return (
    <>
      <Meter
        label={zh ? "统一内存" : "Unified memory"}
        value={mem.total ? `${mem.used.toFixed(1)} / ${mem.total.toFixed(0)} GB · ${mem.pct}%` : "—"}
        pct={mem.pct}
        tint={mem.pct > 90 ? colors.danger : GREEN}
      />
      {gpu?.util_pct != null && (
        <Meter label={zh ? "GPU 利用率" : "GPU utilization"} value={`${gpu.util_pct}%`} pct={Math.min(100, gpu.util_pct)} tint="#54A8FF" />
      )}
      {(gpu?.temp_c != null || gpu?.power_w != null) && (
        <View style={[s.row, { gap: 8, flexWrap: "wrap" }]}>
          {gpu?.temp_c != null && <Stat label={zh ? "温度" : "Temp"} value={`${gpu.temp_c}°C`} />}
          {gpu?.power_w != null && <Stat label={zh ? "功耗" : "Power"} value={`${Math.round(gpu.power_w)} W`} />}
        </View>
      )}
      {!!t?.gpu_processes?.length && (
        <View style={{ gap: 3 }}>
          <Text style={[s.small, { color: colors.text, fontWeight: "600" }]}>{zh ? "GPU 进程" : "GPU processes"}</Text>
          {t.gpu_processes.map((p) => (
            <View key={p.pid} style={[s.between, { gap: 8 }]}>
              <Text numberOfLines={1} style={[s.small, { flex: 1, fontFamily: "monospace" }]}>{p.name} · {p.pid}</Text>
              <Text style={s.small}>{p.mem_mb ? `${(p.mem_mb / 1024).toFixed(1)} GB` : "—"}</Text>
            </View>
          ))}
        </View>
      )}
      {last && (
        <View style={{ gap: 2, borderTopWidth: 1, borderColor: colors.line, paddingTop: 8 }}>
          <Text style={[s.small, { color: colors.text, fontWeight: "600" }]}>{zh ? "最近一次 Jev 路由" : "Latest Jev route"}</Text>
          <Text style={s.small} accessibilityLabel={zh ? "Jev 技能路由" : "Jev skill route"}>
            {last.error
              ? `Jev ✕ ${last.error}`
              : `Jev-0.8B → ${last.skill} · ${last.confidence?.toFixed(2)} · ${last.ms ?? "?"}ms${last.used ? "" : zh ? "（未采用）" : " (not used)"}`}
          </Text>
          {!!last.question && <Text numberOfLines={2} style={[s.small, { fontStyle: "italic" }]}>“{last.question}”</Text>}
        </View>
      )}
      <View style={{ borderTopWidth: 1, borderColor: colors.line, paddingTop: 8 }}>
        {t?.zone ? <ZoneLine zone={t.zone} zh={zh} /> : <SparkZones zh={zh} />}
      </View>
      <ErrorNotice error={telemetry.error && !t ? telemetry.error : undefined} />
    </>
  );
}

function ZoneLine({ zone, zh }: { zone: z.output<typeof zoneSchema>; zh: boolean }) {
  const u = zone.usage;
  const pct = u ? Math.min(100, Math.round((u.mem_used_gb / zone.mem_gb) * 100)) : 0;
  return (
    <View style={{ gap: 4 }}>
      <Text style={[s.small, { color: colors.text }]}>
        {zh ? "分区" : "Zone"} {zone.name} · {zone.mem_gb}GB · CPU {zone.cpu_pct}%
        {u ? ` · ${zh ? "已用" : "used"} ${u.mem_used_gb.toFixed(1)}GB · ${u.processes} ${zh ? "进程" : "procs"}` : ""}
        {u?.gpu_mem_gb ? ` · GPU ${u.gpu_mem_gb.toFixed(1)}GB` : ""}
      </Text>
      <View style={{ height: 4, borderRadius: 2, backgroundColor: colors.line, overflow: "hidden" }}>
        <View style={{ width: `${pct}%`, height: "100%", backgroundColor: "#54A8FF" }} />
      </View>
    </View>
  );
}

/** Master Bot only: split this Spark into zones; each new zone becomes a Bot in the island. */
function SparkZones({ zh }: { zh: boolean }) {
  const { api } = useWorkspace();
  const [open, setOpen] = useState(false);
  const zones = usePoll("/api/spark/zones", zonesSchema, 5000, open);
  const [name, setName] = useState("");
  const [mem, setMem] = useState("24");
  const [cpu, setCpu] = useState("400");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  if (!open)
    return (
      <Button small onPress={() => setOpen(true)}>
        {zh ? "分区管理（一区一 Bot）" : "Zones (one Bot each)"}
      </Button>
    );
  const create = async () => {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const r = await api.request<{ zone: { id: string; name: string }; claimId: string }>("/api/spark/zones", {
        name: name.trim(), mem_gb: Number(mem), cpu_pct: Number(cpu),
      });
      // The native host turns the claim into a new Bot; the zone key never enters this page.
      notifyIsland({ type: "spark-zone-created", claimId: r.claimId });
      setNotice(zh ? `已创建分区「${r.zone.name}」，右上角滑轨里多了一个 Bot。` : `Zone “${r.zone.name}” created; a new Bot is in the switcher.`);
      setName("");
      await zones.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const remove = async (id: string) => {
    setBusy(true);
    setError("");
    try {
      await api.request(`/api/spark/zones/${encodeURIComponent(id)}/delete`, {});
      setNotice(zh ? "分区已删除，它启动的进程已结束；对应 Bot 请在长岛里移除。" : "Zone deleted and its processes stopped; remove its Bot from Long Island.");
      await zones.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const z0 = zones.value;
  return (
    <View style={{ gap: 8, borderTopWidth: 1, borderColor: colors.line, paddingTop: 8 }}>
      <Text style={[s.small, { color: colors.text, fontWeight: "600" }]}>
        {zh ? "DGX Spark 分区" : "DGX Spark zones"}
        {z0 ? ` · ${zh ? "可分配" : "assignable"} ${z0.assignable_gb}GB / ${z0.mem_total_gb.toFixed(0)}GB（${zh ? "预留" : "reserved"} ${z0.reserved_gb}GB）` : ""}
      </Text>
      {z0?.zones.map((zone) => (
        <View key={zone.id} style={[s.between, { gap: 8 }]}>
          <View style={{ flex: 1 }}><ZoneLine zone={zone} zh={zh} /></View>
          <Button small danger disabled={busy} onPress={() => void remove(zone.id)}>{zh ? "删除" : "Delete"}</Button>
        </View>
      ))}
      <View style={[s.row, { gap: 6, flexWrap: "wrap" }]}>
        <TextInput accessibilityLabel={zh ? "分区名称" : "Zone name"} placeholder={zh ? "名称，如 视觉区" : "Name, e.g. Vision"}
          value={name} onChangeText={setName} style={[s.input, { flex: 2, minWidth: 110 }]} />
        <TextInput accessibilityLabel={zh ? "内存 GB" : "Memory GB"} value={mem} onChangeText={setMem}
          keyboardType="numeric" style={[s.input, { flex: 1, minWidth: 60 }]} />
        <TextInput accessibilityLabel={zh ? "CPU 百分比（100 = 1 核）" : "CPU percent (100 = one core)"} value={cpu}
          onChangeText={setCpu} keyboardType="numeric" style={[s.input, { flex: 1, minWidth: 60 }]} />
      </View>
      <Text style={s.small}>
        {zh
          ? "内存 GB · CPU%（100 = 1 核）。内存/CPU/进程数由 cgroup 硬限制；GPU 显存不受 cgroup 约束，由 Bot 按预算配置引擎。"
          : "Memory GB · CPU% (100 = one core). Memory, CPU and processes are enforced by cgroups; GPU memory is not, so the Bot sizes engines to the budget."}
      </Text>
      <View style={[s.row, { gap: 8 }]}>
        <Button small primary busy={busy} disabled={busy || !name.trim() || !(Number(mem) >= 1)} onPress={() => void create()}>
          {zh ? "创建分区并添加 Bot" : "Create zone + Bot"}
        </Button>
        <Button small onPress={() => setOpen(false)}>{zh ? "收起" : "Close"}</Button>
      </View>
      {!!notice && <Text style={s.small}>{notice}</Text>}
      <ErrorNotice error={error || (zones.error && !z0 ? zones.error : "")} />
    </View>
  );
}

export type SparkApproval = z.output<typeof approvalSchema>;

/** Pending run_spark_command approvals for this Bot (polled faster while it replies). */
export function useSparkApprovals(replying: boolean, enabled?: boolean) {
  const sparkBot = useSparkEnabled();
  const spark = enabled ?? sparkBot;
  const pending = usePoll("/api/spark/approvals", approvalsSchema,
    replying ? 1000 : 5000, spark);
  return { spark, list: spark ? pending.value?.approvals ?? [] : [], refresh: pending.refresh };
}

/** Approval cards for pending Spark commands. Approve / deny happens only here. */
export function SparkApprovals({ approvals }: { approvals: ReturnType<typeof useSparkApprovals> }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const { api } = useWorkspace();
  const [busy, setBusy] = useState<{ id: string; approved: boolean }>();
  const [error, setError] = useState("");
  const seen = useRef(new Set<string>());
  const list = approvals.list.filter((a) => !seen.current.has(a.id));
  if (!approvals.spark || (!list.length && !error)) return null;
  const decide = async (id: string, approved: boolean) => {
    setBusy({ id, approved });
    setError("");
    try {
      await api.request(`/api/spark/approvals/${encodeURIComponent(id)}`, { approved });
      seen.current.add(id);
      await approvals.refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <View style={{ gap: 10 }}>
      {list.map((a) => (
        <Card key={a.id} style={{ padding: 14, gap: 8, borderWidth: 1, borderColor: GREEN }}>
          <View style={[s.row, { gap: 8 }]}>
            <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: "#E39B2B" }} />
            <Text style={s.heading}>{zh ? "在 DGX Spark 上执行？" : "Run on the DGX Spark?"}</Text>
          </View>
          <Text style={s.muted}>{a.reason}</Text>
          <View style={{ backgroundColor: "#11191C", borderRadius: 8, padding: 10 }}>
            <Text selectable style={{ color: "#E8F5D0", fontFamily: "monospace", fontSize: 13 }}>
              {a.cwd ? `[${a.cwd}] ` : ""}$ {a.command}
            </Text>
          </View>
          <View style={[s.row, { gap: 8 }]}>
            <Button small primary icon={Check} busy={busy?.id === a.id && busy.approved} disabled={!!busy}
              onPress={() => void decide(a.id, true)}>
              {zh ? "批准执行" : "Approve"}
            </Button>
            <Button small variant="danger" icon={X} busy={busy?.id === a.id && !busy.approved} disabled={!!busy}
              onPress={() => void decide(a.id, false)}>
              {zh ? "拒绝" : "Deny"}
            </Button>
          </View>
        </Card>
      ))}
      <ErrorNotice error={error} />
    </View>
  );
}

const resultSchema = z.object({
  status: z.string().optional(), ran: z.boolean().optional(), exit_code: z.number().nullable().optional(),
  timed_out: z.boolean().optional(), truncated: z.boolean().optional(), output: z.string().optional(),
  ms: z.number().optional(), error: z.string().optional(), note: z.string().optional(),
}).passthrough();

/** The tool call in the transcript: what was asked, and the receipt the Spark returned. */
export function SparkCommandCard({ args, result, loading }: { args: Record<string, unknown>; result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  let value = result;
  if (typeof value === "string") {
    try { value = JSON.parse(value); } catch { value = undefined; }
  }
  const r = resultSchema.safeParse(value ?? {}).data;
  const command = typeof args.command === "string" ? args.command : "";
  const state = loading
    ? zh ? "等待批准 / 执行中…" : "Waiting for approval / running…"
    : r?.error ? `✕ ${r.error}`
      : !r?.ran ? (zh ? "未执行" : "Did not run") + (r?.note ? ` — ${r.note}` : "")
        : r.timed_out ? (zh ? "超时，已终止" : "Timed out and killed")
          : `exit ${r.exit_code} · ${r.ms ?? "?"}ms${r.truncated ? zh ? " · 输出已截断" : " · output truncated" : ""}`;
  return (
    <Card style={{ padding: 12, gap: 8 }}>
      <Text style={[s.small, { color: GREEN, fontWeight: "700" }]}>DGX Spark · run_spark_command</Text>
      <Text selectable style={{ fontFamily: "monospace", fontSize: 13, color: colors.text }}>$ {command}</Text>
      <Text style={s.small}>{state}</Text>
      {!!r?.output && (
        <View style={{ backgroundColor: "#11191C", borderRadius: 8, padding: 10, maxHeight: 260, overflow: "hidden" }}>
          <Text selectable style={{ color: "#D8E2DC", fontFamily: "monospace", fontSize: 12 }}>
            {r.output.slice(-4000)}
          </Text>
        </View>
      )}
    </Card>
  );
}
