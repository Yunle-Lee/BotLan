import {
  Code2,
  FileCode,
  Folder,
  FolderPlus,
  FilePlus,
  Play,
  RefreshCw,
  Save,
  Terminal as TerminalIcon,
  Sparkles,
  Cpu,
  Layers,
  Check,
  Power,
  ChevronRight,
  ArrowUpRight,
  GitCommit,
  Send,
  MessageSquare,
  Bot,
  Zap,
} from "lucide-react-native";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ActivityIndicator,
  Platform,
  Pressable,
  ScrollView,
  Text,
  TextInput,
  useWindowDimensions,
  View,
} from "react-native";
import type {
  ComputerCommand,
  ComputerDirectory,
  ComputerSnapshot,
} from "../../../packages/domain/src/computer";
import { Button, Card, colors, Empty, ErrorNotice, Field, s, timeLabel } from "./ui";
import { useWorkspace } from "./workspace";
import { useI18n } from "./i18n";

const mono = Platform.OS === "ios" ? "Menlo" : "monospace";
const message = (error: unknown) => (error instanceof Error ? error.message : String(error));

type WorkbenchTab = "files" | "terminal" | "assistant" | "models" | "diff";

interface ModelPreset {
  id: string;
  name: string;
  provider: string;
  defaultBaseUrl: string;
  defaultModel: string;
}

const MODEL_PRESETS: ModelPreset[] = [
  {
    id: "deepseek",
    name: "DeepSeek (V3 / R1 官方/本地)",
    provider: "openai",
    defaultBaseUrl: "https://api.deepseek.com/v1",
    defaultModel: "deepseek-chat",
  },
  {
    id: "ollama",
    name: "Ollama (本地私有部署)",
    provider: "openai",
    defaultBaseUrl: "http://localhost:11434/v1",
    defaultModel: "qwen2.5-coder",
  },
  {
    id: "openai",
    name: "OpenAI (GPT-4o / o1 / o3)",
    provider: "openai",
    defaultBaseUrl: "https://api.openai.com/v1",
    defaultModel: "gpt-4o",
  },
  {
    id: "anthropic",
    name: "Anthropic (Claude 3.7 Sonnet)",
    provider: "anthropic",
    defaultBaseUrl: "https://api.anthropic.com",
    defaultModel: "claude-3-7-sonnet-20250219",
  },
  {
    id: "qwen",
    name: "Qwen 通义千问 (DashScope)",
    provider: "openai",
    defaultBaseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1",
    defaultModel: "qwen-turbo",
  },
  {
    id: "gemini",
    name: "Google Gemini (2.0 Flash / Pro)",
    provider: "google",
    defaultBaseUrl: "",
    defaultModel: "gemini-2.0-flash",
  },
];

export function CodeWorkbenchScreen() {
  const { t, lang } = useI18n();
  const { api, ask } = useWorkspace();
  const { width } = useWindowDimensions();
  const wide = width >= 860;
  const isNarrow = width < 660;
  const isMobile = width < 500;

  const [activeTab, setActiveTab] = useState<WorkbenchTab>("files");
  const [snapshot, setSnapshot] = useState<ComputerSnapshot>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  // Files & Editor State
  const [currentPath, setCurrentPath] = useState("/");
  const [directory, setDirectory] = useState<ComputerDirectory>();
  const [loadingFiles, setLoadingFiles] = useState(false);
  const [selectedFile, setSelectedFile] = useState<string>();
  const [codeContent, setCodeContent] = useState("");
  const [savedContent, setSavedContent] = useState("");
  const [newFileName, setNewFileName] = useState("");
  const [creatingFile, setCreatingFile] = useState(false);
  const [newFolderName, setNewFolderName] = useState("");
  const [creatingFolder, setCreatingFolder] = useState(false);

  // Terminal State
  const [command, setCommand] = useState("");
  const [executing, setExecuting] = useState(false);
  const [recentOutput, setRecentOutput] = useState("");

  // Model Config State
  const [selectedPreset, setSelectedPreset] = useState("deepseek");
  const [customBaseUrl, setCustomBaseUrl] = useState("https://api.deepseek.com/v1");
  const [customModel, setCustomModel] = useState("deepseek-chat");
  const [customApiKey, setCustomApiKey] = useState("");
  const [modelSaved, setModelSaved] = useState(false);
  const [testingModel, setTestingModel] = useState(false);
  const [testOutput, setTestOutput] = useState("");

  // AI Assistant Chat State
  const [aiPrompt, setAiPrompt] = useState("");
  const [aiReplying, setAiReplying] = useState(false);
  const [aiMessages, setAiMessages] = useState<Array<{ role: "user" | "assistant"; content: string }>>([
    {
      role: "assistant",
      content:
        lang === "zh"
          ? "你好！我是你的 AI 编程助手，已配置 DeepSeek 模型引擎。你可以向我咨询任何编程问题、生成新功能代码、审查当前文件或排查错误。"
          : "Hello! I am your AI coding assistant powered by DeepSeek. Ask me any coding question, generate new features, review files, or debug errors.",
    },
  ]);

  const running = snapshot?.status === "running";
  const isDirty = selectedFile ? codeContent !== savedContent : false;

  // Load saved model config on mount
  useEffect(() => {
    if (typeof window !== "undefined" && window.localStorage) {
      const saved = window.localStorage.getItem("app_custom_model") || window.localStorage.getItem("chat_custom_model");
      if (saved) {
        try {
          const parsed = JSON.parse(saved);
          if (parsed.preset) setSelectedPreset(parsed.preset);
          if (parsed.baseUrl) setCustomBaseUrl(parsed.baseUrl);
          if (parsed.model) setCustomModel(parsed.model);
          if (parsed.apiKey) setCustomApiKey(parsed.apiKey);
          return;
        } catch {}
      }
      // Credentials are only saved after explicit user configuration.
    }
  }, []);

  // Poll Computer Snapshot
  const refreshSnapshot = useCallback(async () => {
    try {
      const snap = await api.request<ComputerSnapshot>("/api/computer");
      setSnapshot(snap);
    } catch {
      // Ignore
    }
  }, [api]);

  useEffect(() => {
    void refreshSnapshot();
    const timer = setInterval(() => void refreshSnapshot(), 4000);
    return () => clearInterval(timer);
  }, [refreshSnapshot]);

  // Load Directory
  const loadDirectory = useCallback(
    async (path: string) => {
      setLoadingFiles(true);
      setError("");
      try {
        const data = await api.request<ComputerDirectory>(
          `/api/computer/files?path=${encodeURIComponent(path)}`,
        );
        setDirectory(data);
        setCurrentPath(path);
      } catch (e) {
        setError(message(e));
      } finally {
        setLoadingFiles(false);
      }
    },
    [api],
  );

  useEffect(() => {
    if (running) {
      void loadDirectory(currentPath);
    }
  }, [running, loadDirectory, currentPath]);

  // Open File
  async function openFile(filePath: string) {
    setLoadingFiles(true);
    setError("");
    try {
      const res = await api.request<{ text: string }>(
        `/api/computer/files/read?path=${encodeURIComponent(filePath)}`,
      );
      setSelectedFile(filePath);
      setCodeContent(res.text);
      setSavedContent(res.text);
    } catch (e) {
      setError(message(e));
    } finally {
      setLoadingFiles(false);
    }
  }

  // Save File
  async function saveFile() {
    if (!selectedFile) return;
    setBusy(true);
    setError("");
    try {
      await api.request("/api/computer/files/write", {
        path: selectedFile,
        text: codeContent,
      });
      setSavedContent(codeContent);
      setNotice(t("code.saved"));
      setTimeout(() => setNotice(""), 3000);
    } catch (e) {
      setError(message(e));
    } finally {
      setBusy(false);
    }
  }

  // Create New File
  async function createFile() {
    if (!newFileName.trim()) return;
    setBusy(true);
    setError("");
    try {
      const fullPath = `${currentPath === "/" ? "" : currentPath}/${newFileName.trim()}`;
      await api.request("/api/computer/files/write", { path: fullPath, text: "" });
      setNewFileName("");
      setCreatingFile(false);
      await loadDirectory(currentPath);
      await openFile(fullPath);
    } catch (e) {
      setError(message(e));
    } finally {
      setBusy(false);
    }
  }

  // Create New Folder
  async function createFolder() {
    if (!newFolderName.trim()) return;
    setBusy(true);
    setError("");
    try {
      const fullPath = `${currentPath === "/" ? "" : currentPath}/${newFolderName.trim()}`;
      await api.request("/api/computer/files/mkdir", { path: fullPath });
      setNewFolderName("");
      setCreatingFolder(false);
      await loadDirectory(currentPath);
    } catch (e) {
      setError(message(e));
    } finally {
      setBusy(false);
    }
  }

  // Run Terminal Command
  async function runCommand(overrideCmd?: string) {
    const cmdToRun = overrideCmd || command;
    if (!cmdToRun.trim()) return;
    setExecuting(true);
    setError("");
    try {
      const res = await api.request<ComputerCommand>("/api/computer/commands", {
        command: cmdToRun.trim(),
      });
      setRecentOutput(
        res.exitCode === 0
          ? res.stdout || res.stderr || "(Command exited successfully with no output)"
          : `[Exit Code ${res.exitCode}]\n${res.stdout || res.stderr || "Unknown error"}`,
      );
      if (!overrideCmd) setCommand("");
      await refreshSnapshot();
    } catch (e) {
      setError(message(e));
      setRecentOutput(`Execution failed: ${message(e)}`);
    } finally {
      setExecuting(false);
    }
  }

  // Toggle Computer state
  async function toggleEnvironment() {
    setBusy(true);
    setError("");
    try {
      const action = running ? "stop" : "start";
      const snap = await api.request<ComputerSnapshot>(`/api/computer/${action}`, {});
      setSnapshot(snap);
      if (action === "start") {
        await loadDirectory("/");
      }
    } catch (e) {
      const msg = message(e);
      if (msg.includes("Computer is not configured") || msg.includes("Docker") || msg.includes("COMPUTER_ENABLED")) {
        setError(
          lang === "zh"
            ? "沙盒环境未开启：本地容器沙盒需要 Docker 支持（在 .env 中开启 COMPUTER_ENABLED=true）。无需沙盒容器，您依然可以正常使用代码编辑、终端操作以及 DeepSeek AI 编程助手！"
            : "Sandbox container is not configured: requires Docker (set COMPUTER_ENABLED=true in .env). Without Docker, you can still use the Code Editor, Terminal, and DeepSeek AI Assistant!"
        );
      } else {
        setError(msg);
      }
    } finally {
      setBusy(false);
    }
  }

  // AI Review File
  function requestAiReview() {
    if (!selectedFile) return;
    setActiveTab("assistant");
    const prompt =
      lang === "zh"
        ? `请帮我审查代码文件 ${selectedFile}，分析潜在缺陷并提出优化建议：\n\`\`\`\n${codeContent.slice(0, 3000)}\n\`\`\``
        : `Please review code file ${selectedFile}, analyze potential issues, and suggest improvements:\n\`\`\`\n${codeContent.slice(0, 3000)}\n\`\`\``;
    void sendToAi(prompt);
  }

  // Test Model Connection
  async function testModelConnection() {
    setTestingModel(true);
    setTestOutput("");
    try {
      const url = customBaseUrl.endsWith("/chat/completions")
        ? customBaseUrl
        : `${customBaseUrl.replace(/\/+$/, "")}/chat/completions`;
      const startTime = Date.now();
      const res = await fetch(url, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${customApiKey}`,
        },
        body: JSON.stringify({
          model: customModel,
          messages: [{ role: "user", content: "请用一句话确认连接状态和模型名称。" }],
          max_tokens: 60,
        }),
      });
      if (!res.ok) {
        const errText = await res.text();
        throw new Error(`HTTP ${res.status}: ${errText}`);
      }
      const data = await res.json();
      const latency = Date.now() - startTime;
      const reply = data.choices?.[0]?.message?.content || JSON.stringify(data);
      setTestOutput(
        lang === "zh"
          ? `✅ 连接成功！（延迟 ${latency}ms）\n模型回复：${reply}`
          : `✅ Connection successful! (${latency}ms latency)\nModel reply: ${reply}`,
      );
    } catch (e) {
      setTestOutput(`❌ 连接测试失败: ${message(e)}`);
    } finally {
      setTestingModel(false);
    }
  }

  // Send Prompt to AI
  async function sendToAi(overridePrompt?: string) {
    const textToSend = overridePrompt || aiPrompt;
    if (!textToSend.trim() || aiReplying) return;
    const userMsg = { role: "user" as const, content: textToSend };
    setAiMessages((prev) => [...prev, userMsg]);
    if (!overridePrompt) setAiPrompt("");
    setAiReplying(true);
    try {
      const url = customBaseUrl.endsWith("/chat/completions")
        ? customBaseUrl
        : `${customBaseUrl.replace(/\/+$/, "")}/chat/completions`;
      const history = [...aiMessages, userMsg].map((m) => ({
        role: m.role,
        content: m.content,
      }));
      const res = await fetch(url, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${customApiKey}`,
        },
        body: JSON.stringify({
          model: customModel,
          messages: history,
          max_tokens: 2048,
        }),
      });
      if (!res.ok) {
        const errText = await res.text();
        throw new Error(`HTTP ${res.status}: ${errText}`);
      }
      const data = await res.json();
      const reply = data.choices?.[0]?.message?.content || "(无返回内容)";
      setAiMessages((prev) => [...prev, { role: "assistant", content: reply }]);
    } catch (e) {
      setAiMessages((prev) => [
        ...prev,
        { role: "assistant", content: `❌ 请求出错: ${message(e)}` },
      ]);
    } finally {
      setAiReplying(false);
    }
  }

  // Save Model Settings
  function saveModelSettings() {
    if (typeof window !== "undefined" && window.localStorage) {
      window.localStorage.setItem(
        "app_custom_model",
        JSON.stringify({
          preset: selectedPreset,
          baseUrl: customBaseUrl,
          model: customModel,
          apiKey: customApiKey,
        }),
      );
    }
    setModelSaved(true);
    setTimeout(() => setModelSaved(false), 3000);
  }

  return (
    <View style={{ gap: 18 }}>
      {/* Top Banner & Environment Status */}
      <Card style={{ padding: isMobile ? 14 : 18, gap: 14 }}>
        <View
          style={{
            flexDirection: isNarrow ? "column" : "row",
            alignItems: isNarrow ? "stretch" : "center",
            justifyContent: "space-between",
            gap: isNarrow ? 12 : 16,
          }}
        >
          {/* Status info (left) */}
          <View
            style={{
              flexDirection: "row",
              alignItems: "center",
              gap: 12,
              flex: 1,
              minWidth: 0,
            }}
          >
            <View
              style={{
                width: 38,
                height: 38,
                borderRadius: 19,
                backgroundColor: running ? "#EAF7EE" : "#FEF2F2",
                borderWidth: 1.5,
                borderColor: running ? "#A3E0BA" : "#FECACA",
                alignItems: "center",
                justifyContent: "center",
                flexShrink: 0,
              }}
            >
              <Power
                size={18}
                color={running ? "#248258" : "#E05A47"}
                strokeWidth={2.4}
              />
            </View>

            <View style={{ flex: 1, minWidth: 0, gap: 3 }}>
              <View style={{ flexDirection: "row", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                <Text style={[s.heading, { fontSize: isMobile ? 15 : 16 }]}>
                  {running ? t("code.environmentReady") : t("code.environmentStopped")}
                </Text>
                <View
                  style={{
                    paddingHorizontal: 8,
                    paddingVertical: 2,
                    borderRadius: 10,
                    backgroundColor: running ? "#EAF7EE" : "#F3F4F6",
                    borderWidth: 1,
                    borderColor: running ? "#A3E0BA" : "#E5E7EB",
                  }}
                >
                  <Text
                    style={{
                      fontSize: 11,
                      fontWeight: "700",
                      color: running ? "#248258" : "#6B7280",
                    }}
                  >
                    {running
                      ? (lang === "zh" ? "运行中" : "Running")
                      : (lang === "zh" ? "未启动" : "Stopped")}
                  </Text>
                </View>
              </View>

              <Text
                style={[s.small, { fontSize: 12, color: colors.muted }]}
                numberOfLines={isNarrow ? 2 : 1}
              >
                {running
                  ? `${lang === "zh" ? "运行中" : "Running"} · ${snapshot?.commands.length || 0} ${lang === "zh" ? "条指令已执行" : "commands executed"}`
                  : lang === "zh"
                    ? "基于本地安全沙盒环境，无需外部依赖"
                    : "Secure local sandbox, no cloud dependencies"}
              </Text>
            </View>
          </View>

          {/* Action button (right / full-width on mobile) */}
          <View
            style={{
              alignSelf: isNarrow ? "stretch" : "flex-end",
              flexShrink: 0,
            }}
          >
            <Button
              small={!isNarrow}
              primary={!running}
              busy={busy}
              icon={Power}
              onPress={() => void toggleEnvironment()}
              style={{
                minWidth: isNarrow ? "100%" : 130,
                justifyContent: "center",
              }}
            >
              {running ? t("code.stopEnvironment") : t("code.startEnvironment")}
            </Button>
          </View>
        </View>

        {/* Sub-tab Navigation */}
        <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8, marginTop: 4 }}>
          {[
            { id: "files" as const, label: t("code.tab.files"), icon: FileCode },
            { id: "terminal" as const, label: t("code.tab.terminal"), icon: TerminalIcon },
            { id: "assistant" as const, label: t("code.tab.assistant"), icon: Sparkles },
            { id: "models" as const, label: t("code.tab.models"), icon: Layers },
            { id: "diff" as const, label: t("code.tab.diff"), icon: GitCommit },
          ].map((item) => {
            const active = activeTab === item.id;
            return (
              <Pressable
                key={item.id}
                onPress={() => setActiveTab(item.id)}
                style={[
                  s.row,
                  {
                    gap: 6,
                    paddingHorizontal: isMobile ? 10 : 14,
                    paddingVertical: 8,
                    borderRadius: 16,
                    backgroundColor: active ? colors.blue : "#F3F4F6",
                  },
                ]}
              >
                <item.icon size={15} color={colors.text} />
                <Text style={{ fontSize: isMobile ? 12 : 13, fontWeight: active ? "600" : "500", color: colors.text }}>
                  {item.label}
                </Text>
              </Pressable>
            );
          })}
        </View>
      </Card>

      <ErrorNotice error={error} />
      {!!notice && (
        <View style={[s.row, { gap: 8, padding: 12, backgroundColor: colors.green, borderRadius: 14 }]}>
          <Check size={16} color="#248258" />
          <Text style={{ fontSize: 13, color: "#248258", fontWeight: "600" }}>{notice}</Text>
        </View>
      )}

      {/* TAB 1: FILES & EDITOR */}
      {activeTab === "files" && (
        <View style={{ flexDirection: wide ? "row" : "column", gap: 16, minHeight: 460 }}>
          {/* File Explorer (Left) */}
          <Card style={{ width: wide ? 280 : undefined, flexShrink: 0, padding: 16, gap: 12, maxHeight: wide ? 620 : 320 }}>
            <View style={s.between}>
              <Text style={s.heading}>{t("code.filesTitle")}</Text>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel="Refresh files"
                onPress={() => void loadDirectory(currentPath)}
                style={{ padding: 6 }}
              >
                <RefreshCw size={15} color={colors.muted} />
              </Pressable>
            </View>

            {/* Actions: New File & New Folder */}
            <View style={[s.row, { gap: 8 }]}>
              <Button
                small
                icon={FilePlus}
                onPress={() => {
                  setCreatingFile(!creatingFile);
                  setCreatingFolder(false);
                }}
              >
                {t("code.newFile")}
              </Button>
              <Button
                small
                icon={FolderPlus}
                onPress={() => {
                  setCreatingFolder(!creatingFolder);
                  setCreatingFile(false);
                }}
              >
                {t("code.newFolder")}
              </Button>
            </View>

            {creatingFile && (
              <View style={{ gap: 8, padding: 10, backgroundColor: "#F7F8F9", borderRadius: 12 }}>
                <TextInput
                  placeholder="index.ts, app.py, readme.md..."
                  value={newFileName}
                  onChangeText={setNewFileName}
                  autoCapitalize="none"
                  style={[s.input, { minHeight: 36, fontSize: 13 }]}
                />
                <View style={[s.row, { gap: 8 }]}>
                  <Button small primary onPress={() => void createFile()}>
                    {t("code.newFile")}
                  </Button>
                  <Button small onPress={() => setCreatingFile(false)}>
                    {t("common.cancel")}
                  </Button>
                </View>
              </View>
            )}

            {creatingFolder && (
              <View style={{ gap: 8, padding: 10, backgroundColor: "#F7F8F9", borderRadius: 12 }}>
                <TextInput
                  placeholder="src, components, utils..."
                  value={newFolderName}
                  onChangeText={setNewFolderName}
                  autoCapitalize="none"
                  style={[s.input, { minHeight: 36, fontSize: 13 }]}
                />
                <View style={[s.row, { gap: 8 }]}>
                  <Button small primary onPress={() => void createFolder()}>
                    {t("code.newFolder")}
                  </Button>
                  <Button small onPress={() => setCreatingFolder(false)}>
                    {t("common.cancel")}
                  </Button>
                </View>
              </View>
            )}

            {/* Current Path Bar */}
            <View style={[s.row, { gap: 6, paddingVertical: 4 }]}>
              <Text style={[s.small, { fontFamily: mono, color: colors.blueDark }]}>{currentPath}</Text>
              {currentPath !== "/" && (
                <Pressable
                  onPress={() => {
                    const parts = currentPath.split("/").filter(Boolean);
                    parts.pop();
                    void loadDirectory(parts.length ? `/${parts.join("/")}` : "/");
                  }}
                >
                  <Text style={[s.small, { color: colors.blueDark, fontWeight: "600" }]}>
                    [{t("code.parentDir")}]
                  </Text>
                </Pressable>
              )}
            </View>

            {/* File List */}
            {loadingFiles ? (
              <ActivityIndicator color={colors.blueDark} />
            ) : (
              <ScrollView style={{ flex: 1 }} showsVerticalScrollIndicator={false}>
                <View style={{ gap: 4 }}>
                  {directory?.entries.filter((entry) => entry.type === "directory").map((dir) => (
                    <Pressable
                      key={dir.path}
                      onPress={() => void loadDirectory(dir.path)}
                      style={({ pressed }) => [
                        s.row,
                        {
                          gap: 8,
                          paddingVertical: 8,
                          paddingHorizontal: 10,
                          borderRadius: 10,
                          backgroundColor: pressed ? "#E8ECEF" : "transparent",
                        },
                      ]}
                    >
                      <Folder size={16} color="#E0A030" />
                      <Text style={[s.text, { fontSize: 13, flex: 1 }]} numberOfLines={1}>
                        {dir.name}
                      </Text>
                      <ChevronRight size={14} color={colors.muted} />
                    </Pressable>
                  ))}

                  {directory?.entries.filter((entry) => entry.type !== "directory").map((file) => {
                    const isSelected = selectedFile === file.path;
                    return (
                      <Pressable
                        key={file.path}
                        onPress={() => void openFile(file.path)}
                        style={({ pressed }) => [
                          s.row,
                          {
                            gap: 8,
                            paddingVertical: 8,
                            paddingHorizontal: 10,
                            borderRadius: 10,
                            backgroundColor: isSelected
                              ? colors.blue
                              : pressed
                                ? "#E8ECEF"
                                : "transparent",
                          },
                        ]}
                      >
                        <FileCode size={16} color={isSelected ? colors.blueDark : colors.muted} />
                        <Text
                          style={[
                            s.text,
                            {
                              fontSize: 13,
                              flex: 1,
                              fontWeight: isSelected ? "600" : "400",
                              color: isSelected ? colors.blueDark : colors.text,
                            },
                          ]}
                          numberOfLines={1}
                        >
                          {file.name}
                        </Text>
                      </Pressable>
                    );
                  })}

                  {!directory?.entries.length && (
                    <Text style={[s.muted, { textAlign: "center", paddingVertical: 20 }]}>
                      {t("code.noFiles")}
                    </Text>
                  )}
                </View>
              </ScrollView>
            )}
          </Card>

          {/* Code Editor (Right) */}
          <Card style={{ flex: 1, minWidth: 0, padding: 16, gap: 12, minHeight: wide ? 620 : 440 }}>
            <View
              style={{
                flexDirection: isNarrow ? "column" : "row",
                alignItems: isNarrow ? "stretch" : "center",
                justifyContent: "space-between",
                gap: 10,
              }}
            >
              <View style={[s.row, { gap: 8, flex: 1, minWidth: 0 }]}>
                <FileCode size={18} color={colors.blueDark} style={{ flexShrink: 0 }} />
                <Text style={[s.heading, { flex: 1 }]} numberOfLines={1}>
                  {selectedFile ? `${selectedFile}${isDirty ? " *" : ""}` : t("code.tab.files")}
                </Text>
              </View>

              {selectedFile && (
                <View
                  style={{
                    flexDirection: "row",
                    gap: 8,
                    alignSelf: isNarrow ? "flex-end" : "center",
                    flexShrink: 0,
                  }}
                >
                  <Button small icon={Sparkles} onPress={requestAiReview}>
                    {t("code.aiReview")}
                  </Button>
                  <Button
                    small
                    primary={isDirty}
                    busy={busy}
                    disabled={!isDirty || busy}
                    icon={Save}
                    onPress={() => void saveFile()}
                  >
                    {t("code.save")}
                  </Button>
                </View>
              )}
            </View>

            {selectedFile ? (
              <TextInput
                value={codeContent}
                onChangeText={setCodeContent}
                multiline
                scrollEnabled
                autoCapitalize="none"
                autoCorrect={false}
                style={{
                  flex: 1,
                  minHeight: 380,
                  fontFamily: mono,
                  fontSize: 13,
                  lineHeight: 20,
                  backgroundColor: "#1B1D23",
                  color: "#E2E8F0",
                  padding: 16,
                  borderRadius: 14,
                  textAlignVertical: "top",
                }}
              />
            ) : (
              <View style={{ flex: 1, alignItems: "center", justifyContent: "center", minHeight: 360, gap: 10 }}>
                <Code2 size={40} color={colors.muted} />
                <Text style={s.muted}>{t("code.emptyEditor")}</Text>
              </View>
            )}
          </Card>
        </View>
      )}

      {/* TAB 2: TERMINAL CONSOLE */}
      {activeTab === "terminal" && (
        <Card style={{ padding: 18, gap: 14 }}>
          <View
            style={{
              flexDirection: isNarrow ? "column" : "row",
              alignItems: isNarrow ? "flex-start" : "center",
              justifyContent: "space-between",
              gap: 10,
            }}
          >
            <View style={[s.row, { gap: 8 }]}>
              <TerminalIcon size={18} color={colors.blueDark} />
              <Text style={s.heading}>{t("code.terminalTitle")}</Text>
            </View>
            <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 6 }}>
              {["ls -la", "pwd", "git status", "node -v"].map((cmd) => (
                <Pressable
                  key={cmd}
                  onPress={() => void runCommand(cmd)}
                  style={{
                    paddingHorizontal: 8,
                    paddingVertical: 4,
                    borderRadius: 8,
                    backgroundColor: "#F0F2F5",
                  }}
                >
                  <Text style={{ fontSize: 11, fontFamily: mono, color: colors.muted }}>{cmd}</Text>
                </Pressable>
              ))}
            </View>
          </View>

          {/* Command Input */}
          <View
            style={{
              flexDirection: isMobile ? "column" : "row",
              alignItems: isMobile ? "stretch" : "center",
              gap: 10,
            }}
          >
            <TextInput
              placeholder="e.g. ls -la, pnpm test, git status, cat README.md..."
              value={command}
              onChangeText={setCommand}
              onSubmitEditing={() => void runCommand()}
              autoCapitalize="none"
              autoCorrect={false}
              style={[
                s.input,
                {
                  flex: 1,
                  minWidth: 0,
                  fontFamily: mono,
                  fontSize: 13,
                  backgroundColor: "#181A1F",
                  color: "#5AF78E",
                  borderColor: "#282C34",
                },
              ]}
            />
            <Button
              primary
              icon={Play}
              busy={executing}
              disabled={!command.trim() || executing}
              onPress={() => void runCommand()}
              style={{ alignSelf: isMobile ? "stretch" : "auto", flexShrink: 0 }}
            >
              {t("code.runCommand")}
            </Button>
          </View>

          {/* Terminal Console Output Display */}
          <View
            style={{
              backgroundColor: "#111417",
              borderRadius: 14,
              padding: 16,
              minHeight: 280,
              gap: 10,
            }}
          >
            <View style={[s.between, { borderBottomWidth: 1, borderBottomColor: "#2A3036", paddingBottom: 8 }]}>
              <View style={[s.row, { gap: 6 }]}>
                <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: "#E05A47" }} />
                <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: "#E8A838" }} />
                <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: "#57AB5A" }} />
              </View>
              <Text style={{ color: "#7B8894", fontSize: 11, fontFamily: mono }}>bash / powershell</Text>
            </View>

            <ScrollView style={{ flex: 1 }} showsVerticalScrollIndicator={false}>
              {recentOutput ? (
                <Text selectable style={{ color: "#7EE787", fontFamily: mono, fontSize: 12, lineHeight: 19 }}>
                  {recentOutput}
                </Text>
              ) : (
                <Text style={{ color: "#54606E", fontFamily: mono, fontSize: 12 }}>
                  {lang === "zh" ? "# 终端就绪。输入指令并回车执行。" : "# Terminal ready. Enter a command and run."}
                </Text>
              )}
            </ScrollView>
          </View>
        </Card>
      )}

      {/* TAB 3: AI ASSISTANT (Powered by DeepSeek) */}
      {activeTab === "assistant" && (
        <Card style={{ padding: 20, gap: 16 }}>
          <View
            style={{
              flexDirection: isNarrow ? "column" : "row",
              alignItems: isNarrow ? "flex-start" : "center",
              justifyContent: "space-between",
              gap: 12,
            }}
          >
            <View style={[s.row, { gap: 8, flex: 1, minWidth: 0 }]}>
              <Bot size={20} color={colors.blueDark} style={{ flexShrink: 0 }} />
              <View style={{ flex: 1, minWidth: 0 }}>
                <Text style={s.heading}>{lang === "zh" ? "DeepSeek 编程助手" : "DeepSeek Code Copilot"}</Text>
                <Text style={[s.small, { color: colors.muted }]} numberOfLines={1}>
                  {lang === "zh"
                    ? `当前引擎: ${customModel} · 在线对话与代码生成`
                    : `Engine: ${customModel} · Direct AI Chat & Code Generation`}
                </Text>
              </View>
            </View>
            <Button
              small
              icon={Sparkles}
              disabled={!selectedFile}
              onPress={requestAiReview}
              style={{ alignSelf: isNarrow ? "flex-start" : "center", flexShrink: 0 }}
            >
              {lang === "zh" ? "审查当前文件" : "Review File"}
            </Button>
          </View>

          {/* Quick Prompts */}
          <View style={[s.row, { gap: 8, flexWrap: "wrap" }]}>
            {[
              {
                label: lang === "zh" ? "⚡ 优化当前代码性能" : "⚡ Optimize performance",
                prompt: selectedFile
                  ? `请优化文件 ${selectedFile} 的代码性能与复杂度：\n${codeContent.slice(0, 2000)}`
                  : "请给出常见的代码性能优化技巧和最佳实践。",
              },
              {
                label: lang === "zh" ? "🧪 生成测试用例" : "🧪 Generate unit tests",
                prompt: selectedFile
                  ? `请为文件 ${selectedFile} 编写完整的单元测试用例：\n${codeContent.slice(0, 2000)}`
                  : "请演示如何为一个典型的 TypeScript 模块编写自动化测试。",
              },
              {
                label: lang === "zh" ? "📝 添加详细中文注释" : "📝 Add comments",
                prompt: selectedFile
                  ? `请为文件 ${selectedFile} 的核心逻辑添加规范详细的代码注释：\n${codeContent.slice(0, 2000)}`
                  : "请解释代码注释的最佳规范。",
              },
            ].map((qp, idx) => (
              <Pressable
                key={idx}
                onPress={() => void sendToAi(qp.prompt)}
                style={{
                  paddingHorizontal: 10,
                  paddingVertical: 5,
                  borderRadius: 12,
                  backgroundColor: "#F0F4F8",
                }}
              >
                <Text style={{ fontSize: 12, color: colors.blueDark, fontWeight: "500" }}>{qp.label}</Text>
              </Pressable>
            ))}
          </View>

          {/* Chat Messages */}
          <ScrollView
            style={{
              maxHeight: 400,
              minHeight: 220,
              backgroundColor: "#F9FAFB",
              borderRadius: 14,
              padding: 14,
            }}
            showsVerticalScrollIndicator={false}
          >
            <View style={{ gap: 12 }}>
              {aiMessages.map((msg, index) => {
                const isUser = msg.role === "user";
                return (
                  <View
                    key={index}
                    style={{
                      alignSelf: isUser ? "flex-end" : "flex-start",
                      maxWidth: "88%",
                      padding: 12,
                      borderRadius: 14,
                      backgroundColor: isUser ? colors.blueDark : "#FFFFFF",
                      borderWidth: isUser ? 0 : 1,
                      borderColor: "#E5E7EB",
                    }}
                  >
                    <Text
                      selectable
                      style={{
                        fontSize: 13,
                        lineHeight: 20,
                        color: isUser ? "#FFFFFF" : colors.text,
                        fontFamily: msg.content.includes("```") ? mono : "inherit",
                      }}
                    >
                      {msg.content}
                    </Text>
                  </View>
                );
              })}
              {aiReplying && (
                <View style={[s.row, { gap: 8, padding: 8 }]}>
                  <ActivityIndicator size="small" color={colors.blueDark} />
                  <Text style={[s.small, { color: colors.muted }]}>
                    {lang === "zh" ? "DeepSeek 正在思考与生成回复..." : "DeepSeek is generating response..."}
                  </Text>
                </View>
              )}
            </View>
          </ScrollView>

          {/* Prompt Input Box */}
          <View
            style={{
              flexDirection: isMobile ? "column" : "row",
              alignItems: isMobile ? "stretch" : "flex-end",
              gap: 10,
            }}
          >
            <TextInput
              placeholder={t("code.askAiPlaceholder")}
              value={aiPrompt}
              onChangeText={setAiPrompt}
              onSubmitEditing={() => void sendToAi()}
              multiline
              style={[
                s.input,
                {
                  flex: 1,
                  minWidth: 0,
                  minHeight: 46,
                  maxHeight: 120,
                  fontSize: 13,
                },
              ]}
            />
            <Button
              primary
              icon={Send}
              busy={aiReplying}
              disabled={!aiPrompt.trim() || aiReplying}
              onPress={() => void sendToAi()}
              style={{ alignSelf: isMobile ? "stretch" : "auto", flexShrink: 0 }}
            >
              {lang === "zh" ? "发送" : "Send"}
            </Button>
          </View>
        </Card>
      )}

      {/* TAB 4: MODEL PROVIDERS (from ZCode) */}
      {activeTab === "models" && (
        <Card style={{ padding: 20, gap: 16 }}>
          <View style={s.between}>
            <View style={[s.row, { gap: 8 }]}>
              <Layers size={18} color={colors.blueDark} />
              <Text style={s.heading}>{t("code.modelsTitle")}</Text>
            </View>
            {modelSaved && (
              <View style={[s.row, { gap: 5 }]}>
                <Check size={14} color="#248258" />
                <Text style={{ fontSize: 12, color: "#248258", fontWeight: "600" }}>
                  {t("code.modelSaved")}
                </Text>
              </View>
            )}
          </View>

          {/* Provider Presets selector */}
          <View style={{ gap: 8 }}>
            <Text style={s.label}>{t("code.modelProvider")}</Text>
            <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8 }}>
              {MODEL_PRESETS.map((preset) => {
                const active = selectedPreset === preset.id;
                return (
                  <Pressable
                    key={preset.id}
                    onPress={() => {
                      setSelectedPreset(preset.id);
                      setCustomBaseUrl(preset.defaultBaseUrl);
                      setCustomModel(preset.defaultModel);
                    }}
                    style={{
                      paddingHorizontal: 12,
                      paddingVertical: 8,
                      borderRadius: 14,
                      backgroundColor: active ? colors.blue : "#F1F2F4",
                      borderWidth: 1,
                      borderColor: active ? colors.blueDark : "transparent",
                    }}
                  >
                    <Text style={{ fontSize: 12, fontWeight: active ? "600" : "400", color: colors.text }}>
                      {preset.name}
                    </Text>
                  </Pressable>
                );
              })}
            </View>
          </View>

          <Field
            label={t("code.modelName")}
            value={customModel}
            onChangeText={setCustomModel}
            placeholder="e.g. deepseek-chat, qwen-turbo, gpt-4o"
            autoCapitalize="none"
          />

          <Field
            label={t("code.modelBaseUrl")}
            value={customBaseUrl}
            onChangeText={setCustomBaseUrl}
            placeholder="e.g. https://api.deepseek.com/v1"
            autoCapitalize="none"
          />

          <Field
            label={t("code.modelApiKey")}
            value={customApiKey}
            onChangeText={setCustomApiKey}
            secureTextEntry
            placeholder="sk-..."
          />

          {testOutput ? (
            <View
              style={{
                backgroundColor: testOutput.startsWith("✅") ? "#F0F9F4" : "#FEF2F2",
                borderWidth: 1,
                borderColor: testOutput.startsWith("✅") ? "#B8E3CD" : "#FCA5A5",
                borderRadius: 12,
                padding: 12,
              }}
            >
              <Text
                selectable
                style={{
                  fontSize: 13,
                  color: testOutput.startsWith("✅") ? "#1D643B" : "#B91C1C",
                  lineHeight: 20,
                  fontFamily: mono,
                }}
              >
                {testOutput}
              </Text>
            </View>
          ) : null}

          <View style={{ flexDirection: isNarrow ? "column" : "row", gap: 10 }}>
            <Button primary icon={Save} onPress={saveModelSettings} style={{ flex: isNarrow ? undefined : 1 }}>
              {t("code.saveModelConfig")}
            </Button>
            <Button
              icon={Sparkles}
              busy={testingModel}
              disabled={testingModel}
              onPress={() => void testModelConnection()}
              style={{ flex: isNarrow ? undefined : 1 }}
            >
              {t("code.testConnection")}
            </Button>
          </View>
        </Card>
      )}

      {/* TAB 5: DIFF VIEWER */}
      {activeTab === "diff" && (
        <Card style={{ padding: 18, gap: 14 }}>
          <View
            style={{
              flexDirection: isNarrow ? "column" : "row",
              alignItems: isNarrow ? "flex-start" : "center",
              justifyContent: "space-between",
              gap: 10,
            }}
          >
            <View style={[s.row, { gap: 8, flex: 1, minWidth: 0 }]}>
              <GitCommit size={18} color={colors.blueDark} style={{ flexShrink: 0 }} />
              <Text style={[s.heading, { flex: 1 }]} numberOfLines={1}>
                {selectedFile ? `${selectedFile} (Diff)` : t("code.tab.diff")}
              </Text>
            </View>
            <Button
              small
              icon={TerminalIcon}
              onPress={() => void runCommand("git diff")}
              style={{ alignSelf: isNarrow ? "flex-end" : "center", flexShrink: 0 }}
            >
              Git Diff
            </Button>
          </View>

          {selectedFile && isDirty ? (
            <View style={{ backgroundColor: "#1E1E2E", borderRadius: 14, padding: 14, gap: 6 }}>
              <Text style={{ color: "#E05A47", fontFamily: mono, fontSize: 12 }}>
                --- Original (Saved)
              </Text>
              <Text style={{ color: "#57AB5A", fontFamily: mono, fontSize: 12 }}>
                +++ Modified (Current Draft)
              </Text>
              <ScrollView style={{ maxHeight: 340 }}>
                {codeContent.split("\n").map((line, idx) => {
                  const savedLine = savedContent.split("\n")[idx];
                  const changed = line !== savedLine;
                  return (
                    <Text
                      key={idx}
                      style={{
                        fontFamily: mono,
                        fontSize: 12,
                        lineHeight: 18,
                        color: changed ? "#FFDF5D" : "#A6ACCD",
                        backgroundColor: changed ? "#353424" : "transparent",
                      }}
                    >
                      {`${String(idx + 1).padStart(3, " ")} | ${line}`}
                    </Text>
                  );
                })}
              </ScrollView>
            </View>
          ) : (
            <Text style={[s.muted, { textAlign: "center", paddingVertical: 24 }]}>
              {lang === "zh"
                ? "当前文件未被修改，或未选择任何文件。点击保存前可在此处查看差异。"
                : "No unstaged changes in the active file, or no file selected."}
            </Text>
          )}
        </Card>
      )}
    </View>
  );
}
