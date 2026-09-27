import { CopilotKitProvider } from "@copilotkit/react-native/headless";
import { StatusBar } from "expo-status-bar";
import {
  Bell,
  Check,
  Code2,
  Lightbulb,
  type LucideIcon,
  Menu,
  MessageCircle,
  PanelsTopLeft,
  Shapes,
  SquareCheck,
  X,
} from "lucide-react-native";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ActivityIndicator,
  AppState,
  Pressable,
  ScrollView,
  Text,
  useWindowDimensions,
  View,
} from "react-native";
import { SafeAreaProvider, SafeAreaView } from "react-native-safe-area-context";
import type { Section, Workspace } from "../../packages/domain/src";
import {
  AgentActivityScreen,
  AgentStatus,
  AppsScreen,
  GoalsScreen,
  IdeasScreen,
} from "./src/agent-ui";
import { AgentWorkspaceProvider, useAgentWorkspace } from "./src/agent-workspace";
import { API_URL, createSession, MuseApi } from "./src/api";
import { ChatScreen, WorkspaceTools } from "./src/chat";
import { CodeWorkbenchScreen } from "./src/code-workbench";
import { ComputerEntry } from "./src/computer";
import { ComputerDraftProvider } from "./src/computer-drafts";
import { Details } from "./src/details";
import { BrowserScreen, CalendarScreen, FilesScreen, MailScreen } from "./src/screens";
import { ThreadsProvider, ThreadsSheet, useMuseThread } from "./src/threads";
import { Button, Card, colors, ErrorNotice, Field, IconButton, Mascot, s } from "./src/ui";
import { type Detail, useWorkspace, WorkspaceContext } from "./src/workspace";
import { I18nProvider, useI18n } from "./src/i18n";
import { useIslandBot, notifyIsland } from "./src/island-host";
import { IslandAvatar } from "./src/island-avatar";

export default function App() {
  return (
    <SafeAreaProvider>
      <StatusBar style="dark" />
      <I18nProvider>
        <MainApp />
      </I18nProvider>
    </SafeAreaProvider>
  );
}

function MainApp() {
  const island = useIslandBot();
  const { lang, toggleLang, t } = useI18n();
  const [token, setToken] = useState("");
  const [accessKey, setAccessKey] = useState("");
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const connect = useCallback(async (key?: string) => {
    setBusy(true);
    setError("");
    try {
      const session = await createSession(key);
      setToken(session.token);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, []);
  useEffect(() => {
    if (!island) void connect();
  }, [connect, Boolean(island)]);
  useEffect(() => {
    if (island) notifyIsland({ type: "page-ready", botId: island.botId });
  }, [island]);

  const activeToken = island?.token || token;
  if (activeToken) {
    return (
      <CopilotKitProvider
        key={island?.botId || "standalone"}
        runtimeUrl={`${API_URL}/api/copilotkit`}
        headers={{ Authorization: `Bearer ${activeToken}` }}
      >
        <WorkspaceApp key={island?.botId || "standalone"} token={activeToken} />
      </CopilotKitProvider>
    );
  }

  return (
    <SafeAreaView
      style={{
        flex: 1,
        backgroundColor: colors.canvas,
        justifyContent: "center",
        alignItems: "center",
        padding: 24,
      }}
    >
      <View style={{ position: "absolute", top: 20, right: 20 }}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Toggle language"
          onPress={toggleLang}
          style={{
            paddingHorizontal: 12,
            paddingVertical: 6,
            borderRadius: 16,
            backgroundColor: "#EEEEF0",
          }}
        >
          <Text style={{ fontSize: 13, fontWeight: "600", color: colors.text }}>
            {lang === "zh" ? "EN" : "中文"}
          </Text>
        </Pressable>
      </View>
      <View style={{ width: "100%", maxWidth: 420, gap: 22, alignItems: "center" }}>
        <Mascot size={72} />
        <Text
          style={{ fontSize: 32, color: colors.text, letterSpacing: -1, fontWeight: "500", textAlign: "center" }}
        >
          {t("welcome.title")}
        </Text>
        <Text style={[s.muted, { textAlign: "center" }]}>{t("welcome.subtitle")}</Text>
        {busy ? (
          <ActivityIndicator color={colors.blueDark} />
        ) : (
          <Card style={{ width: "100%" }}>
            <ErrorNotice error={error} />
            <Field
              label={t("welcome.accessKey")}
              value={accessKey}
              onChangeText={setAccessKey}
              secureTextEntry
              placeholder={t("welcome.placeholder")}
            />
            <Button primary onPress={() => void connect(accessKey || undefined)}>
              {t("welcome.open")}
            </Button>
            <Text style={[s.small, { marginTop: 15 }]}>
              {t("welcome.localNotice")}
            </Text>
          </Card>
        )}
      </View>
    </SafeAreaView>
  );
}
function WorkspaceApp({ token }: { token: string }) {
  const { t } = useI18n();
  const api = useMemo(() => new MuseApi(token), [token]);
  const [workspace, setWorkspace] = useState<Workspace>();
  const [section, setSection] = useState<Section>("chat");
  const [detail, setDetail] = useState<Detail>();
  const [toast, setToast] = useState("");
  const [error, setError] = useState("");
  const [prompt, setPrompt] = useState<{ id: number; text: string }>();
  const refresh = useCallback(async () => {
    const snapshot = await api.request<Workspace>("/api/workspace");
    setWorkspace(snapshot);
    setError("");
  }, [api]);
  useEffect(() => {
    void refresh().catch((e) => setError(String(e)));
  }, [refresh]);
  useEffect(() => {
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "active") void refresh().catch((e) => setError(String(e)));
    });
    return () => listener.remove();
  }, [refresh]);
  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => setToast(""), 5500);
    return () => clearTimeout(timer);
  }, [toast]);
  const navigate = useCallback(
    (next: Section) =>
      setSection(next === "today" ? "chat" : next === "connections" ? "apps" : next),
    [],
  );
  const open = useCallback((next: Detail) => setDetail(next), []);
  const close = useCallback(() => setDetail(undefined), []);
  const ask = useCallback((text: string) => {
    setPrompt({ id: Date.now(), text });
    setSection("chat");
  }, []);
  if (!workspace)
    return (
      <SafeAreaView
        style={{
          flex: 1,
          backgroundColor: colors.canvas,
          alignItems: "center",
          justifyContent: "center",
          padding: 24,
          gap: 18,
        }}
      >
        <Mascot size={56} />
        {error ? (
          <>
            <ErrorNotice error={error} />
            <Button onPress={() => void refresh().catch((e) => setError(String(e)))}>
              {t("common.tryAgain")}
            </Button>
          </>
        ) : (
          <>
            <ActivityIndicator color={colors.blueDark} />
            <Text style={s.muted}>{t("workspace.opening")}</Text>
          </>
        )}
      </SafeAreaView>
    );
  return (
    <WorkspaceContext.Provider
      value={{ workspace, api, section, navigate, refresh, open, close, notify: setToast, ask }}
    >
      <AgentWorkspaceProvider>
        <ComputerDraftProvider key={token}>
          <ThreadsProvider>
            <WorkspaceShell
              detail={detail}
              toast={toast}
              clearToast={() => setToast("")}
              error={error}
              prompt={prompt}
            />
          </ThreadsProvider>
        </ComputerDraftProvider>
      </AgentWorkspaceProvider>
    </WorkspaceContext.Provider>
  );
}
function WorkspaceShell({
  detail,
  toast,
  clearToast,
  error,
  prompt,
}: {
  detail?: Detail;
  toast: string;
  clearToast: () => void;
  error: string;
  prompt?: { id: number; text: string };
}) {
  const { lang, toggleLang, t, titles, navLabels } = useI18n();
  const island = useIslandBot();
  const { workspace, section, navigate, open } = useWorkspace();
  const { data } = useAgentWorkspace();
  const {
    selection,
    visited,
    mainId,
    loading: threadsLoading,
    error: threadsError,
    retry: retryThreads,
    enabled: richThreads,
  } = useMuseThread();
  const [threadsOpen, setThreadsOpen] = useState(false);
  const { width, height } = useWindowDimensions();
  const desktop = width >= 900;
  const compactDock = Boolean(island) && height < 430;
  const dockSize = compactDock ? 36 : 44;
  const pending =
    (data?.notifications.filter((n) => !n.read).length || 0) +
    workspace.actions.filter((a) => a.status === "awaiting_review").length;
  const activeTask =
    data?.tasks.find(
      (task) => task.status === "waiting_approval" || task.status === "waiting_input",
    ) || data?.tasks.find((task) => task.status === "running");
  const agentName = island?.name || data?.identity.name || (lang === "zh" ? "AI 智能体" : "AI Agent");
  const status = activeTask
    ? activeTask.status === "waiting_approval"
      ? `${t("status.readyReview")} · ${activeTask.title}`
      : activeTask.status === "waiting_input"
        ? `${t("status.needsInput")} · ${activeTask.title}`
        : activeTask.plan.find((step) => step.status === "running")?.title || activeTask.title
    : data?.tasks.some((task) => task.status === "queued")
      ? t("status.pickingNext")
      : t("status.standby");
  const title = titles[section] || titles.apps;
  const Screen =
    section === "code"
      ? CodeWorkbenchScreen
      : section === "mail"
        ? MailScreen
        : section === "calendar"
          ? CalendarScreen
          : section === "browser"
            ? BrowserScreen
            : section === "files"
              ? FilesScreen
              : section === "activity"
                ? AgentActivityScreen
                : section === "ideas"
                  ? IdeasScreen
                  : section === "goals"
                    ? GoalsScreen
                    : AppsScreen;
  const utility = ["mail", "calendar", "browser", "files"].includes(section);
  const primaryNav: { id: Section; label: string; icon: LucideIcon }[] = useMemo(
    () => [
      { id: "chat", label: navLabels.chat, icon: MessageCircle },
      { id: "code", label: navLabels.code, icon: Code2 },
    ],
    [navLabels],
  );
  const secondaryNav: { id: Section; label: string; icon: LucideIcon }[] = useMemo(
    () => [
      { id: "activity", label: navLabels.activity, icon: PanelsTopLeft },
      { id: "ideas", label: navLabels.ideas, icon: Lightbulb },
      { id: "goals", label: navLabels.goals, icon: SquareCheck },
      { id: "apps", label: navLabels.apps, icon: Shapes },
    ],
    [navLabels],
  );
  return (
    <>
      <WorkspaceTools />
      <SafeAreaView style={{ flex: 1, backgroundColor: colors.canvas }} edges={["top", "bottom"]}>
        <View
          style={{
            flex: 1,
            width: "100%",
            maxWidth: section === "code" ? (desktop ? 1040 : 760) : 760,
            alignSelf: "center",
            position: "relative",
          }}
        >
          {island && (
            <>
              <View nativeID="island-page-drag-region" style={{ height: 10, marginHorizontal: 82 }} />
              <ScrollView
                nativeID="island-left-dock"
                showsVerticalScrollIndicator={false}
                style={{ position: "absolute", left: 4, top: 8, bottom: 8, width: 72, zIndex: 50 }}
                contentContainerStyle={{ alignItems: "center", gap: compactDock ? 8 : 12 }}
              >
                <IconButton icon={Menu} label={t("header.menu")} onPress={() => setThreadsOpen(true)} />
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={`Open ${agentName} activity and approvals`}
                  onPress={() => navigate("activity")}
                  style={({ pressed }) => ({ alignItems: "center", width: 68, opacity: pressed ? 0.65 : 1 })}
                >
                  <IslandAvatar size={compactDock ? 44 : 52} covered={threadsOpen || Boolean(detail)} />
                  <Text numberOfLines={2} style={{ fontSize: 11, fontWeight: "600", color: colors.text, textAlign: "center", marginTop: 3 }}>
                    {agentName}
                  </Text>
                  <Text numberOfLines={2} style={{ fontSize: 10, color: colors.muted, textAlign: "center", marginTop: 3 }}>
                    {status}
                  </Text>
                </Pressable>
                <Pressable
                  accessibilityRole="button" accessibilityLabel="Toggle language" onPress={toggleLang}
                  style={({ pressed }) => ({ width: 36, height: 32, alignItems: "center", justifyContent: "center",
                    borderRadius: 16, backgroundColor: pressed ? "#E2E4E7" : "#EEEEF0" })}
                >
                  <Text style={{ fontSize: 12, fontWeight: "700", color: colors.text }}>{lang === "zh" ? "EN" : "中"}</Text>
                </Pressable>
                <View>
                  <IconButton icon={Bell} label={`${t("header.notifications")}, ${pending}`} onPress={() => open({ type: "notifications" })} />
                  {pending > 0 && <View pointerEvents="none" style={{ position: "absolute", right: 6, top: 6,
                    width: 6, height: 6, borderRadius: 3, backgroundColor: colors.blueDark }} />}
                </View>
                {section === "chat" && <ComputerEntry compact />}
              </ScrollView>
            </>
          )}
          {!island && <View
            nativeID="island-page-drag-region"
            style={{
              height: desktop ? 146 : 122,
              paddingTop: desktop ? 14 : 2,
              marginHorizontal: 20,
              position: "relative",
            }}
          >
            <View style={{ position: "absolute", left: 0, top: 16, zIndex: 50 }}>
              <IconButton
                icon={Menu}
                label={t("header.menu")}
                onPress={() => setThreadsOpen(true)}
              />
            </View>
            <View pointerEvents="box-none" style={{ alignItems: "center", gap: 1 }}>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`Open ${agentName} activity and approvals`}
                onPress={() => navigate("activity")}
                style={({ pressed }) => ({
                  alignItems: "center",
                  maxWidth: "70%",
                  opacity: pressed ? 0.65 : 1,
                })}
              >
                <Mascot size={desktop ? 58 : 49} variant={data?.identity.avatar} />
                <Text
                  style={{
                    fontSize: 16,
                    fontWeight: "600",
                    color: colors.text,
                    letterSpacing: -0.4,
                  }}
                >
                  {agentName}
                </Text>
                <Text
                  numberOfLines={1}
                  style={{ fontSize: 11, color: colors.muted, marginBottom: 6 }}
                >
                  {status}
                </Text>
              </Pressable>
              {section === "chat" && <ComputerEntry />}
            </View>
            <View style={{ position: "absolute", right: 0, top: 16, zIndex: 50, flexDirection: "row", alignItems: "center", gap: 8 }}>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel="Toggle language"
                onPress={toggleLang}
                style={({ pressed }) => ({
                  paddingHorizontal: 10,
                  paddingVertical: 6,
                  borderRadius: 14,
                  backgroundColor: pressed ? "#E2E4E7" : "#EEEEF0",
                })}
              >
                <Text style={{ fontSize: 12, fontWeight: "700", color: colors.text }}>
                  {lang === "zh" ? "EN" : "中"}
                </Text>
              </Pressable>
              <IconButton
                icon={Bell}
                label={`${t("header.notifications")}, ${pending}`}
                onPress={() => open({ type: "notifications" })}
              />
              {pending > 0 && (
                <View
                  pointerEvents="none"
                  style={{
                    width: 6,
                    height: 6,
                    borderRadius: 4,
                    position: "absolute",
                    top: 7,
                    right: 9,
                    backgroundColor: colors.blueDark,
                  }}
                />
              )}
            </View>
          </View>}
          <View nativeID="island-main-content" style={{ flex: 1, minHeight: 0 }}>
            {section !== "chat" && (
              <ScrollView
                key={section}
                showsVerticalScrollIndicator={false}
                contentContainerStyle={{
                  paddingLeft: island ? 84 : desktop ? 42 : 22,
                  paddingRight: desktop ? 82 : 74,
                  paddingBottom: 28,
                }}
                keyboardShouldPersistTaps="handled"
              >
                {utility && (
                  <Button
                    small
                    style={{ alignSelf: "flex-start", marginBottom: 18 }}
                    onPress={() => navigate("apps")}
                  >
                    {t("common.backToApps")}
                  </Button>
                )}
                <Text style={[s.title, { fontSize: 25, marginBottom: 22 }]}>{title?.title}</Text>
                <ErrorNotice error={error} />
                <Screen />
              </ScrollView>
            )}
            <View
              style={{
                display: section === "chat" ? "flex" : "none",
                flex: 1,
                paddingLeft: island ? 84 : desktop ? 42 : 17,
                paddingRight: desktop ? 82 : 74,
              }}
            >
              <AgentStatus />
              {richThreads ? (
                <>
                  <ErrorNotice error={threadsError} />
                  {threadsError ? (
                    <Button onPress={retryThreads}>{t("chat.retryMain")}</Button>
                  ) : threadsLoading ? (
                    <ActivityIndicator color={colors.blueDark} />
                  ) : null}
                  {!threadsLoading && selection.id !== mainId && (
                    <Text style={[s.small, { textAlign: "center", marginBottom: 8 }]}>
                      {t("chat.sideChat")}
                    </Text>
                  )}
                  {visited.map((thread) => (
                    <View
                      key={thread.id}
                      style={{ display: selection.id === thread.id ? "flex" : "none", flex: 1 }}
                    >
                      <ChatScreen
                        thread={thread}
                        active={section === "chat" && selection.id === thread.id}
                        prompt={selection.id === thread.id ? prompt : undefined}
                      />
                    </View>
                  ))}
                </>
              ) : (
                <ChatScreen prompt={prompt} active={section === "chat"} />
              )}
            </View>
          </View>

          {/* Same actions; embedded Chat puts both docks at the sides of the full-height content. */}
          <View
            nativeID="island-right-dock"
            pointerEvents="box-none"
            style={{
              position: "absolute",
              right: 13,
              top: island ? 8 : desktop ? 160 : 130,
              zIndex: 999,
              alignItems: "center",
              gap: 10,
            }}
          >
            {/* Top Pill: Chat & Code */}
            <View
              style={{
                backgroundColor: "#FFFFFF",
                borderRadius: 28,
                paddingVertical: 7,
                paddingHorizontal: 5,
                gap: 6,
                borderWidth: 2,
                borderColor: "#181A1F",
                shadowColor: "#000",
                shadowOffset: { width: 0, height: 3 },
                shadowOpacity: 0.1,
                shadowRadius: 10,
                elevation: 5,
              }}
            >
              {primaryNav.map((item) => {
                const active = section === item.id;
                return (
                  <Pressable
                    key={item.id}
                    accessibilityRole="tab"
                    accessibilityLabel={item.label}
                    accessibilityState={{ selected: active }}
                    onPress={() => navigate(item.id)}
                    style={({ pressed }) => [
                      {
                        width: dockSize,
                        height: dockSize,
                        borderRadius: 22,
                        alignItems: "center",
                        justifyContent: "center",
                        backgroundColor: active ? "#F0F1F3" : pressed ? "#F7F8F9" : "transparent",
                        cursor: "pointer",
                      },
                    ]}
                  >
                    <item.icon
                      size={22}
                      strokeWidth={2}
                      color={active ? colors.blueDark : colors.text}
                    />
                  </Pressable>
                );
              })}
            </View>

            {/* Bottom Pill: Activity, Ideas, Goals, Apps */}
            <View
              style={{
                backgroundColor: "#FFFFFF",
                borderRadius: 28,
                paddingVertical: 7,
                paddingHorizontal: 5,
                gap: 6,
                borderWidth: 2,
                borderColor: "#181A1F",
                shadowColor: "#000",
                shadowOffset: { width: 0, height: 3 },
                shadowOpacity: 0.1,
                shadowRadius: 10,
                elevation: 5,
              }}
            >
              {secondaryNav.map((item) => {
                const active = section === item.id || (item.id === "apps" && utility);
                return (
                  <Pressable
                    key={item.id}
                    accessibilityRole="tab"
                    accessibilityLabel={item.label}
                    accessibilityState={{ selected: active }}
                    onPress={() => navigate(item.id)}
                    style={({ pressed }) => [
                      {
                        width: dockSize,
                        height: dockSize,
                        borderRadius: 22,
                        alignItems: "center",
                        justifyContent: "center",
                        backgroundColor: active ? "#F0F1F3" : pressed ? "#F7F8F9" : "transparent",
                        cursor: "pointer",
                      },
                    ]}
                  >
                    <item.icon
                      size={22}
                      strokeWidth={2}
                      color={active ? colors.blueDark : colors.text}
                    />
                  </Pressable>
                );
              })}
            </View>
          </View>
        </View>
        {!!toast && (
          <View
            nativeID="island-toast"
            pointerEvents="box-none"
            style={{ position: "absolute", bottom: 24, left: 20, right: 20, alignItems: "center" }}
          >
            <View
              style={[
                s.row,
                {
                  gap: 10,
                  padding: 14,
                  backgroundColor: colors.text,
                  borderRadius: 20,
                  maxWidth: 560,
                },
              ]}
            >
              <Check size={16} color={colors.blue} />
              <Text style={{ color: "#FFF", fontSize: 13, flexShrink: 1 }}>{toast}</Text>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel="Dismiss notification"
                onPress={clearToast}
              >
                <X size={16} color="#FFF" />
              </Pressable>
            </View>
          </View>
        )}
        {threadsOpen && <ThreadsSheet onClose={() => setThreadsOpen(false)} />}
        {detail && (
          <Details
            key={
              detail.type === "task"
                ? detail.taskId
                : detail.type === "file"
                  ? detail.file.id
                  : detail.type === "browser"
                    ? detail.browser.id
                    : detail.type === "mail"
                      ? detail.mail.id
                      : detail.type === "review"
                        ? detail.action.id
                        : detail.type === "email"
                          ? JSON.stringify(detail.draft)
                          : detail.type === "event"
                            ? detail.event?.id || "event-new"
                            : detail.type
            }
            detail={detail}
          />
        )}
      </SafeAreaView>
    </>
  );
}
