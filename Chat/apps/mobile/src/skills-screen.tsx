import {
  Check,
  ChevronDown,
  ChevronUp,
  Copy,
  Cpu,
  Database,
  FileStack,
  GitBranch,
  Globe,
  GraduationCap,
  Layers,
  Link2,
  ListChecks,
  type LucideIcon,
  MessageCircle,
  Power,
  Route,
  Scan,
  Search,
  ShieldCheck,
  Sparkles,
  Terminal,
  Trash2,
  Users,
  Wrench,
} from "lucide-react-native";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ActivityIndicator, Pressable, ScrollView, Text, TextInput, View } from "react-native";
import { useI18n } from "./i18n";
import { Card, colors, ErrorNotice, s } from "./ui";
import { useWorkspace } from "./workspace";

// Mirrors SkillListing in apps/server/src/skills.ts.
export interface SkillItem {
  id: string;
  name: string;
  version: string;
  author: string;
  category: string;
  icon: string;
  tags: string[];
  summary: string;
  description: string;
  source: { kind: "builtin" | "nvidia" | "market" | "url"; url?: string };
  tryPrompt?: string;
  featured?: boolean;
  commands?: { label: string; command: string; language?: string }[];
  requiresEnv?: { key: string; label: string; required: boolean }[];
  installed: boolean;
  enabled: boolean;
  installedAt?: string;
  digest?: string;
}

const ICON_MAP: Record<string, LucideIcon> = {
  Database,
  Layers,
  Search,
  Speech: Sparkles,
  Route,
  Globe,
  Scan,
  Cpu,
  Wrench,
  ListChecks,
  Users,
  GitBranch,
  Brain: Sparkles,
  GraduationCap,
  ShieldCheck,
  FileStack,
  Sparkles,
};

const SOURCE_LABEL: Record<SkillItem["source"]["kind"], { zh: string; en: string; tint: string }> =
  {
    builtin: { zh: "BotLan 内置", en: "Built-in", tint: "#EAF2FC" },
    nvidia: { zh: "NVIDIA 官方", en: "NVIDIA", tint: "#EAF6E3" },
    market: { zh: "技能广场", en: "Market", tint: "#F3F0FB" },
    url: { zh: "URL 导入", en: "Imported", tint: "#FDF0DF" },
  };

type Filter = "all" | "installed" | string;

export function SkillsScreen() {
  const { lang } = useI18n();
  const { api, ask } = useWorkspace();
  const zh = lang === "zh";

  const [skills, setSkills] = useState<SkillItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [marketError, setMarketError] = useState("");
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [importUrl, setImportUrl] = useState("");
  const [toast, setToast] = useState("");

  const showToast = (msg: string) => {
    setToast(msg);
    setTimeout(() => setToast(""), 3000);
  };

  const load = useCallback(async () => {
    setError("");
    try {
      const result = await api.request<{ skills: SkillItem[]; marketError?: string }>(
        "/api/agent/skills",
      );
      setSkills(result.skills);
      setMarketError(result.marketError ?? "");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  const run = async (id: string, action: () => Promise<unknown>, done: string) => {
    setBusyId(id);
    setError("");
    try {
      await action();
      await load();
      showToast(done);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyId(null);
    }
  };

  const install = (skill: SkillItem) =>
    run(
      skill.id,
      () => api.request(`/api/agent/skills/${encodeURIComponent(skill.id)}/install`, {}),
      zh ? `已为当前 Bot 安装：${skill.name}` : `Installed for this Bot: ${skill.name}`,
    );
  const uninstall = (skill: SkillItem) =>
    run(
      skill.id,
      () => api.request(`/api/agent/skills/${encodeURIComponent(skill.id)}/uninstall`, {}),
      zh ? `已卸载：${skill.name}` : `Removed: ${skill.name}`,
    );
  const toggle = (skill: SkillItem) =>
    run(
      skill.id,
      () =>
        api.request(`/api/agent/skills/${encodeURIComponent(skill.id)}/enabled`, {
          enabled: !skill.enabled,
        }),
      skill.enabled
        ? zh
          ? `已停用：${skill.name}`
          : `Disabled: ${skill.name}`
        : zh
          ? `已启用：${skill.name}`
          : `Enabled: ${skill.name}`,
    );
  const importFromUrl = () => {
    const url = importUrl.trim();
    if (!url) return;
    void run(
      "import",
      async () => {
        await api.request("/api/agent/skills/import", { url });
        setImportUrl("");
      },
      zh ? "已导入 SKILL.md" : "SKILL.md imported",
    );
  };

  const categories = useMemo(() => {
    const set = new Set<string>();
    for (const skill of skills) if (skill.category) set.add(skill.category);
    return Array.from(set);
  }, [skills]);

  const activeCount = skills.filter((skill) => skill.installed && skill.enabled).length;

  const filtered = useMemo(() => {
    const q = query.toLowerCase().trim();
    return skills
      .filter((item) => {
        const matchFilter =
          filter === "all" || (filter === "installed" ? item.installed : item.category === filter);
        const matchQuery =
          !q ||
          item.name.toLowerCase().includes(q) ||
          item.summary.toLowerCase().includes(q) ||
          item.author.toLowerCase().includes(q) ||
          item.tags.some((tag) => tag.toLowerCase().includes(q));
        return matchFilter && matchQuery;
      })
      .sort((a, b) => Number(Boolean(b.featured)) - Number(Boolean(a.featured)));
  }, [skills, filter, query]);

  const chips: { id: Filter; label: string }[] = [
    { id: "all", label: zh ? "全部技能" : "All" },
    {
      id: "installed",
      label: zh ? `已安装 ${skills.filter((x) => x.installed).length}` : "Installed",
    },
    ...categories.map((cat) => ({ id: cat, label: cat })),
  ];

  return (
    <View style={{ gap: 16 }}>
      {/* 当前 Bot 的技能状态 */}
      <View
        style={{
          backgroundColor: "#0F172A",
          borderRadius: 16,
          padding: 16,
          gap: 6,
        }}
      >
        <View style={[s.row, { alignItems: "center", gap: 8 }]}>
          <Sparkles size={16} color="#76B900" />
          <Text style={{ color: "#FFFFFF", fontSize: 15, fontWeight: "700" }}>
            {zh
              ? `当前 Bot 已启用 ${activeCount} 个技能`
              : `${activeCount} skills active on this Bot`}
          </Text>
        </View>
        <Text style={{ color: "#CBD5E1", fontSize: 12, lineHeight: 18 }}>
          {zh
            ? "技能按 Agent Skills 规范保存在本机后台，每个 Bot（每台 DGX Spark）独立。对话时 Agent 只看到技能目录，命中需求后再调用 load_skill 读取完整说明，节省上下文。"
            : "Skills follow the Agent Skills format and are stored per Bot by the local backend. The Agent sees a short index and calls load_skill only when a request matches."}
        </Text>
      </View>

      {/* 搜索 */}
      <View style={[s.row, { gap: 10, alignItems: "center" }]}>
        <View
          style={[
            s.row,
            {
              flex: 1,
              backgroundColor: "#F2F3F5",
              borderRadius: 16,
              paddingHorizontal: 12,
              paddingVertical: 8,
              alignItems: "center",
              gap: 8,
            },
          ]}
        >
          <Search size={16} color={colors.muted} />
          <TextInput
            value={query}
            onChangeText={setQuery}
            accessibilityLabel={zh ? "搜索技能" : "Search skills"}
            placeholder={zh ? "搜索 DGX Spark、StepFun、NVIDIA、RAG..." : "Search skills..."}
            placeholderTextColor={colors.muted}
            style={{ flex: 1, fontSize: 14, color: colors.text, padding: 0 }}
          />
        </View>
        <Text style={[s.small, { color: colors.muted }]}>
          {zh ? `共 ${filtered.length} 个` : `${filtered.length} skills`}
        </Text>
      </View>

      {/* 分类 */}
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={{ gap: 8, paddingVertical: 2 }}
      >
        {chips.map((chip) => {
          const active = filter === chip.id;
          return (
            <Pressable
              key={chip.id}
              accessibilityRole="button"
              accessibilityState={{ selected: active }}
              onPress={() => setFilter(chip.id)}
              style={({ pressed }) => ({
                paddingHorizontal: 12,
                paddingVertical: 6,
                borderRadius: 14,
                backgroundColor: active ? colors.blueDark : pressed ? "#E5E7EB" : "#F3F4F6",
              })}
            >
              <Text
                style={{
                  fontSize: 12,
                  fontWeight: active ? "600" : "400",
                  color: active ? "#FFFFFF" : colors.text,
                }}
              >
                {chip.label}
              </Text>
            </Pressable>
          );
        })}
      </ScrollView>

      {toast ? (
        <View
          accessibilityLiveRegion="polite"
          style={{
            backgroundColor: "#E8F4EC",
            paddingHorizontal: 14,
            paddingVertical: 10,
            borderRadius: 12,
            borderWidth: 1,
            borderColor: "#A3D9B5",
          }}
        >
          <Text style={{ fontSize: 13, color: "#1D6F42", fontWeight: "500" }}>{toast}</Text>
        </View>
      ) : null}

      <ErrorNotice error={error} />
      {marketError ? (
        <Text style={{ fontSize: 12, color: colors.muted }}>{marketError}</Text>
      ) : null}

      {loading ? (
        <View style={{ paddingVertical: 40, alignItems: "center", gap: 10 }}>
          <ActivityIndicator color={colors.blueDark} />
          <Text style={[s.muted, { fontSize: 13 }]}>
            {zh ? "正在加载技能目录..." : "Loading skills..."}
          </Text>
        </View>
      ) : (
        <View style={{ gap: 12 }}>
          {filtered.map((skill) => (
            <SkillCard
              key={skill.id}
              skill={skill}
              zh={zh}
              busy={busyId === skill.id}
              isExpanded={expandedId === skill.id}
              icon={ICON_MAP[skill.icon] || Sparkles}
              onInstall={() => void install(skill)}
              onUninstall={() => void uninstall(skill)}
              onToggle={() => void toggle(skill)}
              onTry={skill.tryPrompt ? () => ask(skill.tryPrompt as string) : undefined}
              onToggleExpand={() => setExpandedId(expandedId === skill.id ? null : skill.id)}
            />
          ))}
        </View>
      )}

      {/* 从 URL 导入任意 SKILL.md */}
      <Card style={{ gap: 10 }}>
        <View style={[s.row, { alignItems: "center", gap: 8 }]}>
          <Link2 size={15} color={colors.text} />
          <Text style={{ fontSize: 14, fontWeight: "600", color: colors.text }}>
            {zh ? "从 URL 导入 SKILL.md" : "Import a SKILL.md from URL"}
          </Text>
        </View>
        <Text style={{ fontSize: 12, color: colors.muted, lineHeight: 18 }}>
          {zh
            ? "支持任意遵循 Agent Skills 规范（含 name / description frontmatter）的公开 HTTPS 链接，GitHub 页面链接会自动转换为原始文件。导入内容仅作为指导，不会绕过审批与安全规则。"
            : "Any public HTTPS link to an Agent Skills SKILL.md with name/description frontmatter. GitHub blob links are converted automatically."}
        </Text>
        <View style={[s.row, { gap: 8, alignItems: "center" }]}>
          <TextInput
            value={importUrl}
            onChangeText={setImportUrl}
            accessibilityLabel={zh ? "SKILL.md 链接" : "SKILL.md URL"}
            placeholder="https://github.com/NVIDIA/skills/blob/main/skills/<name>/SKILL.md"
            placeholderTextColor={colors.muted}
            autoCapitalize="none"
            autoCorrect={false}
            style={{
              flex: 1,
              fontSize: 13,
              color: colors.text,
              backgroundColor: "#F2F3F5",
              borderRadius: 10,
              paddingHorizontal: 10,
              paddingVertical: 8,
            }}
          />
          <Pressable
            accessibilityRole="button"
            disabled={busyId === "import" || !importUrl.trim()}
            onPress={importFromUrl}
            style={({ pressed }) => ({
              paddingHorizontal: 14,
              paddingVertical: 9,
              borderRadius: 10,
              backgroundColor: pressed ? "#1E293B" : "#0F172A",
              opacity: busyId === "import" || !importUrl.trim() ? 0.5 : 1,
            })}
          >
            {busyId === "import" ? (
              <ActivityIndicator color="#FFFFFF" size="small" />
            ) : (
              <Text style={{ color: "#FFFFFF", fontSize: 12, fontWeight: "600" }}>
                {zh ? "导入" : "Import"}
              </Text>
            )}
          </Pressable>
        </View>
      </Card>
    </View>
  );
}

function SkillCard({
  skill,
  zh,
  busy,
  isExpanded,
  icon: IconComponent,
  onInstall,
  onUninstall,
  onToggle,
  onTry,
  onToggleExpand,
}: {
  skill: SkillItem;
  zh: boolean;
  busy: boolean;
  isExpanded: boolean;
  icon: LucideIcon;
  onInstall: () => void;
  onUninstall: () => void;
  onToggle: () => void;
  onTry?: () => void;
  onToggleExpand: () => void;
}) {
  const [copied, setCopied] = useState<string | null>(null);
  const active = skill.installed && skill.enabled;
  const source = SOURCE_LABEL[skill.source.kind];

  const copyText = (text: string, label: string) => {
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      void navigator.clipboard.writeText(text).then(() => {
        setCopied(label);
        setTimeout(() => setCopied(null), 2000);
      });
    }
  };

  return (
    <Card
      style={{
        padding: 0,
        overflow: "hidden",
        borderWidth: active ? 1.5 : 1,
        borderColor: active ? colors.blueDark : "#E5E7EB",
      }}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: isExpanded }}
        onPress={onToggleExpand}
        style={({ pressed }) => ({ padding: 16, backgroundColor: pressed ? "#FAFAFA" : "#FFFFFF" })}
      >
        <View style={[s.row, { gap: 12, alignItems: "flex-start" }]}>
          <View
            style={{
              width: 44,
              height: 44,
              borderRadius: 12,
              backgroundColor: active ? "#EAF2FC" : "#F3F4F6",
              alignItems: "center",
              justifyContent: "center",
            }}
          >
            <IconComponent size={22} color={active ? colors.blueDark : "#4B5563"} />
          </View>

          <View style={{ flex: 1, gap: 4 }}>
            <View style={[s.row, { alignItems: "center", gap: 6, flexWrap: "wrap" }]}>
              <Text style={{ fontSize: 16, fontWeight: "600", color: colors.text }}>
                {skill.name}
              </Text>
              <Badge text={zh ? source.zh : source.en} tint={source.tint} />
              {skill.featured ? <Badge text={zh ? "推荐" : "Featured"} tint="#FFF4CC" /> : null}
              {skill.installed ? (
                <Badge
                  text={skill.enabled ? (zh ? "已启用" : "Active") : zh ? "已停用" : "Disabled"}
                  tint={skill.enabled ? "#E8F5E9" : "#F3F4F6"}
                  color={skill.enabled ? "#2E7D32" : colors.muted}
                />
              ) : null}
            </View>
            <Text
              numberOfLines={isExpanded ? undefined : 2}
              style={{ fontSize: 13, color: colors.muted, lineHeight: 18 }}
            >
              {skill.summary}
            </Text>
            <Text style={{ fontSize: 11, color: colors.muted, marginTop: 2 }}>
              {skill.author} · {skill.category} · v{skill.version}
            </Text>
          </View>

          <View style={{ alignItems: "flex-end", gap: 6 }}>
            {skill.installed ? (
              <ActionButton
                busy={busy}
                icon={Power}
                label={skill.enabled ? (zh ? "停用" : "Disable") : zh ? "启用" : "Enable"}
                onPress={onToggle}
              />
            ) : (
              <ActionButton
                busy={busy}
                icon={Check}
                label={zh ? "安装" : "Install"}
                onPress={onInstall}
                dark
              />
            )}
            {isExpanded ? (
              <ChevronUp size={16} color={colors.muted} />
            ) : (
              <ChevronDown size={16} color={colors.muted} />
            )}
          </View>
        </View>
      </Pressable>

      {isExpanded && (
        <View
          style={{
            paddingHorizontal: 16,
            paddingBottom: 16,
            paddingTop: 6,
            gap: 14,
            borderTopWidth: 1,
            borderColor: "#F3F4F6",
            backgroundColor: "#FCFCFD",
          }}
        >
          <View style={{ gap: 4 }}>
            <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
              {zh ? "技能说明" : "Description"}
            </Text>
            <Text style={{ fontSize: 13, color: colors.muted, lineHeight: 20 }}>
              {skill.description}
            </Text>
            {skill.source.url ? (
              <Text selectable style={{ fontSize: 11, color: colors.muted }}>
                {zh ? "来源：" : "Source: "}
                {skill.source.url}
              </Text>
            ) : null}
            {skill.digest ? (
              <Text
                selectable
                style={{ fontSize: 11, color: colors.muted, fontFamily: "monospace" }}
              >
                sha256 {skill.digest.slice(0, 16)}…
              </Text>
            ) : null}
          </View>

          {skill.requiresEnv?.length ? (
            <View style={{ gap: 4 }}>
              <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
                {zh ? "需要的环境变量（在 Spark 终端自行设置）" : "Environment (set on your Spark)"}
              </Text>
              {skill.requiresEnv.map((env) => (
                <Text key={env.key} style={{ fontSize: 12, color: colors.muted }}>
                  {env.key}
                  {env.required ? " *" : ""} — {env.label}
                </Text>
              ))}
            </View>
          ) : null}

          {skill.commands?.length ? (
            <View style={{ gap: 6 }}>
              <View style={[s.row, { alignItems: "center", gap: 6 }]}>
                <Terminal size={14} color={colors.text} />
                <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
                  {zh ? "关键命令（在 DGX Spark 上执行）" : "Key commands (run on DGX Spark)"}
                </Text>
              </View>
              {skill.commands.map((cmd, idx) => (
                <View
                  key={`${skill.id}-${cmd.label}-${cmd.command}`}
                  style={{
                    backgroundColor: "#18181B",
                    borderRadius: 8,
                    overflow: "hidden",
                    borderWidth: 1,
                    borderColor: "#27272A",
                  }}
                >
                  <View
                    style={[
                      s.row,
                      {
                        paddingHorizontal: 12,
                        paddingVertical: 6,
                        backgroundColor: "#27272A",
                        justifyContent: "space-between",
                        alignItems: "center",
                      },
                    ]}
                  >
                    <Text style={{ fontSize: 11, color: "#A1A1AA", fontWeight: "500" }}>
                      {cmd.label}
                    </Text>
                    <Pressable
                      accessibilityRole="button"
                      accessibilityLabel={zh ? "复制命令" : "Copy command"}
                      onPress={() => copyText(cmd.command, `${skill.id}-${idx}`)}
                      style={[s.row, { alignItems: "center", gap: 4 }]}
                    >
                      {copied === `${skill.id}-${idx}` ? (
                        <Text style={{ fontSize: 11, color: "#4ADE80" }}>
                          {zh ? "已复制" : "Copied"}
                        </Text>
                      ) : (
                        <>
                          <Copy size={12} color="#A1A1AA" />
                          <Text style={{ fontSize: 11, color: "#A1A1AA" }}>
                            {zh ? "复制" : "Copy"}
                          </Text>
                        </>
                      )}
                    </Pressable>
                  </View>
                  <View style={{ padding: 12 }}>
                    <Text
                      selectable
                      style={{ fontFamily: "monospace", fontSize: 12, color: "#38BDF8" }}
                    >
                      $ {cmd.command}
                    </Text>
                  </View>
                </View>
              ))}
            </View>
          ) : null}

          <View style={[s.row, { gap: 8, flexWrap: "wrap" }]}>
            {onTry ? (
              <ActionButton
                icon={MessageCircle}
                label={zh ? (skill.installed ? "在对话中试用" : "安装后试用") : "Try in chat"}
                onPress={skill.installed ? onTry : onInstall}
                busy={busy}
              />
            ) : null}
            {skill.installed ? (
              <ActionButton
                icon={Trash2}
                label={zh ? "卸载" : "Remove"}
                onPress={onUninstall}
                busy={busy}
                danger
              />
            ) : null}
          </View>
        </View>
      )}
    </Card>
  );
}

function Badge({ text, tint, color }: { text: string; tint: string; color?: string }) {
  return (
    <Text
      style={{
        fontSize: 11,
        fontWeight: "600",
        color: color ?? colors.text,
        backgroundColor: tint,
        paddingHorizontal: 7,
        paddingVertical: 2,
        borderRadius: 8,
        overflow: "hidden",
      }}
    >
      {text}
    </Text>
  );
}

function ActionButton({
  icon: Icon,
  label,
  onPress,
  busy,
  dark,
  danger,
}: {
  icon: LucideIcon;
  label: string;
  onPress: () => void;
  busy?: boolean;
  dark?: boolean;
  danger?: boolean;
}) {
  const fg = dark ? "#FFFFFF" : danger ? "#DC2626" : colors.text;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      disabled={busy}
      onPress={(e) => {
        e.stopPropagation();
        onPress();
      }}
      style={({ pressed }) => ({
        paddingHorizontal: 14,
        paddingVertical: 7,
        borderRadius: 16,
        backgroundColor: dark
          ? pressed
            ? "#1E293B"
            : "#0F172A"
          : danger
            ? "#FEE2E2"
            : pressed
              ? "#E5E7EB"
              : "#F3F4F6",
        flexDirection: "row",
        alignItems: "center",
        gap: 5,
        opacity: busy ? 0.6 : 1,
      })}
    >
      {busy ? <ActivityIndicator size="small" color={fg} /> : <Icon size={13} color={fg} />}
      <Text style={{ fontSize: 12, fontWeight: "600", color: fg }}>{label}</Text>
    </Pressable>
  );
}
