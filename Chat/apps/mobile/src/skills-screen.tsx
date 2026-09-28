import { useEffect, useMemo, useState } from "react";
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  Text,
  TextInput,
  View,
} from "react-native";
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
  ListChecks,
  Route,
  Scan,
  Search,
  ShieldCheck,
  Sparkles,
  Terminal,
  Trash2,
  Users,
  Wrench,
  type LucideIcon,
} from "lucide-react-native";
import { useI18n } from "./i18n";
import { useIslandBot } from "./island-host";
import { Button, Card, Chip, colors, ErrorNotice, s } from "./ui";

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
  systemPrompt: string;
  commands?: {
    label: string;
    command: string;
    language?: string;
  }[];
  codeSnippet?: {
    filename: string;
    language: string;
    code: string;
  };
  requiresEnv?: {
    key: string;
    label: string;
    required: boolean;
  }[];
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
};

export function SkillsScreen() {
  const { lang, t } = useI18n();
  const island = useIslandBot();
  const botId = island?.botId || "default";
  const storageKey = `bot_installed_skills_${botId}`;

  const [skills, setSkills] = useState<SkillItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [query, setQuery] = useState("");
  const [selectedCategory, setSelectedCategory] = useState("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [installedIds, setInstalledIds] = useState<string[]>(() => {
    if (typeof window !== "undefined" && window.localStorage) {
      try {
        const raw = window.localStorage.getItem(storageKey);
        return raw ? JSON.parse(raw) : [];
      } catch {
        return [];
      }
    }
    return [];
  });
  const [toast, setToast] = useState("");

  const showToast = (msg: string) => {
    setToast(msg);
    setTimeout(() => setToast(""), 3000);
  };

  // 持久化已安装列表（每个 Bot 独立隔离）
  const toggleInstall = (skill: SkillItem) => {
    setInstalledIds((prev) => {
      const isInstalled = prev.includes(skill.id);
      const next = isInstalled ? prev.filter((id) => id !== skill.id) : [...prev, skill.id];
      if (typeof window !== "undefined" && window.localStorage) {
        try {
          window.localStorage.setItem(storageKey, JSON.stringify(next));
        } catch {
          // ignore
        }
      }
      showToast(
        isInstalled
          ? (lang === "zh" ? `已停用技能：${skill.name}` : `Skill deactivated: ${skill.name}`)
          : (lang === "zh" ? `已安装技能：${skill.name}，即刻生效！` : `Skill installed: ${skill.name}`),
      );
      return next;
    });
  };

  const fetchSkills = async () => {
    setLoading(true);
    setError("");
    try {
      const res = await fetch("https://kilee.cn/api/skills");
      if (!res.ok) throw new Error(`HTTP error ${res.status}`);
      const json = await res.json();
      if (json.data && Array.isArray(json.data)) {
        setSkills(json.data);
      } else {
        throw new Error("Invalid response format");
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchSkills();
  }, []);

  const categories = useMemo(() => {
    const set = new Set<string>();
    skills.forEach((s) => {
      if (s.category) set.add(s.category);
    });
    return ["all", ...Array.from(set)];
  }, [skills]);

  const filtered = useMemo(() => {
    return skills.filter((item) => {
      const matchCat = selectedCategory === "all" || item.category === selectedCategory;
      const q = query.toLowerCase().trim();
      const matchQuery =
        !q ||
        item.name.toLowerCase().includes(q) ||
        item.summary.toLowerCase().includes(q) ||
        item.author.toLowerCase().includes(q) ||
        item.tags.some((tag) => tag.toLowerCase().includes(q));
      return matchCat && matchQuery;
    });
  }, [skills, selectedCategory, query]);

  return (
    <View style={{ gap: 16 }}>
      {/* 顶部搜索栏与统计 */}
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
            placeholder={lang === "zh" ? "搜索技能名称、NVIDIA、RAG..." : "Search skills..."}
            placeholderTextColor={colors.muted}
            style={{ flex: 1, fontSize: 14, color: colors.text, padding: 0 }}
          />
        </View>
        <Text style={[s.small, { color: colors.muted }]}>
          {lang === "zh"
            ? `共 ${filtered.length} 个技能`
            : `${filtered.length} skills`}
        </Text>
      </View>

      {/* 分类 Chips */}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ gap: 8, paddingVertical: 2 }}>
        {categories.map((cat) => {
          const active = selectedCategory === cat;
          return (
            <Pressable
              key={cat}
              onPress={() => setSelectedCategory(cat)}
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
                {cat === "all" ? (lang === "zh" ? "全部技能" : "All") : cat}
              </Text>
            </Pressable>
          );
        })}
      </ScrollView>

      {/* 提示条 */}
      {toast ? (
        <View
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
      {loading ? (
        <View style={{ paddingVertical: 40, alignItems: "center", gap: 10 }}>
          <ActivityIndicator color={colors.blueDark} />
          <Text style={[s.muted, { fontSize: 13 }]}>
            {lang === "zh" ? "正在连接 kilee.cn 获取最新技能广场..." : "Fetching skills from kilee.cn..."}
          </Text>
        </View>
      ) : (
        <View style={{ gap: 12 }}>
          {filtered.map((skill) => {
            const isInstalled = installedIds.includes(skill.id);
            const isExpanded = expandedId === skill.id;
            const IconComponent = ICON_MAP[skill.icon] || Sparkles;

            return (
              <SkillCard
                key={skill.id}
                skill={skill}
                isInstalled={isInstalled}
                isExpanded={isExpanded}
                icon={IconComponent}
                lang={lang}
                onToggleInstall={() => toggleInstall(skill)}
                onToggleExpand={() => setExpandedId(isExpanded ? null : skill.id)}
              />
            );
          })}
        </View>
      )}
    </View>
  );
}

// 单张技能卡片：包含 Swipe Actions 体验与 Code Block Command
function SkillCard({
  skill,
  isInstalled,
  isExpanded,
  icon: IconComponent,
  lang,
  onToggleInstall,
  onToggleExpand,
}: {
  skill: SkillItem;
  isInstalled: boolean;
  isExpanded: boolean;
  icon: LucideIcon;
  lang: string;
  onToggleInstall: () => void;
  onToggleExpand: () => void;
}) {
  const [copied, setCopied] = useState<string | null>(null);

  const copyText = (text: string, label: string) => {
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(text);
      setCopied(label);
      setTimeout(() => setCopied(null), 2000);
    }
  };

  return (
    <Card
      style={{
        padding: 0,
        overflow: "hidden",
        borderWidth: isInstalled ? 1.5 : 1,
        borderColor: isInstalled ? colors.blueDark : "#E5E7EB",
      }}
    >
      {/* 卡片头部与主要信息 */}
      <Pressable
        onPress={onToggleExpand}
        style={({ pressed }) => ({
          padding: 16,
          backgroundColor: pressed ? "#FAFAFA" : "#FFFFFF",
        })}
      >
        <View style={[s.row, { gap: 12, alignItems: "flex-start" }]}>
          {/* 技能图标 */}
          <View
            style={{
              width: 44,
              height: 44,
              borderRadius: 12,
              backgroundColor: isInstalled ? "#EAF2FC" : "#F3F4F6",
              alignItems: "center",
              justifyContent: "center",
            }}
          >
            <IconComponent size={22} color={isInstalled ? colors.blueDark : "#4B5563"} />
          </View>

          {/* 标题、作者与摘要 */}
          <View style={{ flex: 1, gap: 4 }}>
            <View style={[s.row, { alignItems: "center", gap: 8, flexWrap: "wrap" }]}>
              <Text style={{ fontSize: 16, fontWeight: "600", color: colors.text }}>
                {skill.name}
              </Text>
              <Text style={{ fontSize: 11, color: colors.muted, backgroundColor: "#F3F4F6", paddingHorizontal: 6, paddingVertical: 2, borderRadius: 6 }}>
                v{skill.version}
              </Text>
              {isInstalled && (
                <View
                  style={{
                    flexDirection: "row",
                    alignItems: "center",
                    gap: 4,
                    backgroundColor: "#E8F5E9",
                    paddingHorizontal: 8,
                    paddingVertical: 2,
                    borderRadius: 10,
                  }}
                >
                  <View style={{ width: 6, height: 6, borderRadius: 3, backgroundColor: "#2E7D32" }} />
                  <Text style={{ fontSize: 11, fontWeight: "600", color: "#2E7D32" }}>
                    {lang === "zh" ? "已安装" : "Installed"}
                  </Text>
                </View>
              )}
            </View>

            <Text numberOfLines={isExpanded ? undefined : 2} style={{ fontSize: 13, color: colors.muted, lineHeight: 18 }}>
              {skill.summary}
            </Text>

            <View style={[s.row, { gap: 6, marginTop: 4, flexWrap: "wrap" }]}>
              <Text style={{ fontSize: 11, color: colors.muted }}>
                {skill.author} · {skill.category}
              </Text>
            </View>
          </View>

          {/* 快捷动作按钮 (Swipe Action / Quick Button) */}
          <View style={{ alignItems: "flex-end", gap: 6 }}>
            <Pressable
              onPress={(e) => {
                e.stopPropagation();
                onToggleInstall();
              }}
              style={({ pressed }) => ({
                paddingHorizontal: 14,
                paddingVertical: 7,
                borderRadius: 16,
                backgroundColor: isInstalled
                  ? pressed ? "#FEE2E2" : "#FEE2E2"
                  : pressed ? "#1E293B" : "#0F172A",
                flexDirection: "row",
                alignItems: "center",
                gap: 5,
              })}
            >
              {isInstalled ? (
                <>
                  <Trash2 size={13} color="#DC2626" />
                  <Text style={{ fontSize: 12, fontWeight: "600", color: "#DC2626" }}>
                    {lang === "zh" ? "停用" : "Disable"}
                  </Text>
                </>
              ) : (
                <>
                  <Check size={13} color="#FFFFFF" />
                  <Text style={{ fontSize: 12, fontWeight: "600", color: "#FFFFFF" }}>
                    {lang === "zh" ? "安装" : "Install"}
                  </Text>
                </>
              )}
            </Pressable>
            {isExpanded ? <ChevronUp size={16} color={colors.muted} /> : <ChevronDown size={16} color={colors.muted} />}
          </View>
        </View>
      </Pressable>

      {/* 展开详情：描述、System Prompt 和 Code Block Command */}
      {isExpanded && (
        <View style={{ paddingHorizontal: 16, paddingBottom: 16, paddingTop: 6, gap: 14, borderTopWidth: 1, borderColor: "#F3F4F6", backgroundColor: "#FCFCFD" }}>
          {/* 详细描述 */}
          <View style={{ gap: 4 }}>
            <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
              {lang === "zh" ? "技能说明" : "Description"}
            </Text>
            <Text style={{ fontSize: 13, color: colors.muted, lineHeight: 20 }}>
              {skill.description}
            </Text>
          </View>

          {/* System Prompt 规则预览 */}
          {skill.systemPrompt && (
            <View style={{ gap: 4 }}>
              <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
                {lang === "zh" ? "注入 Prompt 规范" : "Injected System Prompt"}
              </Text>
              <View style={{ backgroundColor: "#F3F4F6", padding: 10, borderRadius: 8 }}>
                <Text style={{ fontSize: 12, color: "#374151", fontStyle: "italic", lineHeight: 18 }}>
                  "{skill.systemPrompt}"
                </Text>
              </View>
            </View>
          )}

          {/* Code Block Command (终端命令与一键复制) */}
          {skill.commands && skill.commands.length > 0 && (
            <View style={{ gap: 6 }}>
              <View style={[s.row, { alignItems: "center", gap: 6 }]}>
                <Terminal size={14} color={colors.text} />
                <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
                  {lang === "zh" ? "终端调用指令" : "Execution Commands"}
                </Text>
              </View>
              {skill.commands.map((cmd, idx) => (
                <View
                  key={idx}
                  style={{
                    backgroundColor: "#18181B",
                    borderRadius: 8,
                    overflow: "hidden",
                    borderWidth: 1,
                    borderColor: "#27272A",
                  }}
                >
                  <View style={[s.row, { paddingHorizontal: 12, paddingVertical: 6, backgroundColor: "#27272A", justifyContent: "space-between", alignItems: "center" }]}>
                    <Text style={{ fontSize: 11, color: "#A1A1AA", fontWeight: "500" }}>{cmd.label}</Text>
                    <Pressable
                      onPress={() => copyText(cmd.command, `${skill.id}-${idx}`)}
                      style={[s.row, { alignItems: "center", gap: 4 }]}
                    >
                      {copied === `${skill.id}-${idx}` ? (
                        <Text style={{ fontSize: 11, color: "#4ADE80" }}>{lang === "zh" ? "已复制" : "Copied"}</Text>
                      ) : (
                        <>
                          <Copy size={12} color="#A1A1AA" />
                          <Text style={{ fontSize: 11, color: "#A1A1AA" }}>{lang === "zh" ? "复制" : "Copy"}</Text>
                        </>
                      )}
                    </Pressable>
                  </View>
                  <View style={{ padding: 12 }}>
                    <Text style={{ fontFamily: "monospace", fontSize: 12, color: "#38BDF8" }}>
                      $ {cmd.command}
                    </Text>
                  </View>
                </View>
              ))}
            </View>
          )}

          {/* 脚本代码片段预览 */}
          {skill.codeSnippet && (
            <View style={{ gap: 6 }}>
              <Text style={{ fontSize: 12, fontWeight: "600", color: colors.text }}>
                {skill.codeSnippet.filename}
              </Text>
              <View style={{ backgroundColor: "#0F172A", padding: 12, borderRadius: 8 }}>
                <Text style={{ fontFamily: "monospace", fontSize: 11, color: "#CBD5E1", lineHeight: 16 }}>
                  {skill.codeSnippet.code}
                </Text>
              </View>
            </View>
          )}
        </View>
      )}
    </Card>
  );
}
