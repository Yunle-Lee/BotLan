import { useThreads } from "@copilotkit/react-native/headless";
import {
  Archive,
  CalendarDays,
  Code2,
  FileText,
  Globe,
  MessageCircle,
  Monitor,
  Plus,
  RefreshCw,
  Settings2,
  Sparkles,
} from "lucide-react-native";
import { createContext, type ReactNode, useContext, useEffect, useRef, useState } from "react";
import { ActivityIndicator, Pressable, Text, View } from "react-native";
import { Button, colors, ErrorNotice, Field, LinkRow, Sheet, s } from "./ui";
import { useWorkspace } from "./workspace";
import { useI18n } from "./i18n";

function newThreadId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export type Selection = { id: string; existing: boolean };

const ThreadContext = createContext<{
  enabled: boolean;
  selection: Selection;
  visited: Selection[];
  mainId: string;
  loading: boolean;
  error: string;
  retry: () => void;
  select: (selection: Selection) => void;
  start: () => void;
  claimPrompt: (id: number) => boolean;
} | null>(null);

export function ThreadsProvider({ children }: { children: ReactNode }) {
  const { workspace, navigate, api } = useWorkspace();
  const handledPrompt = useRef(0);
  const enabled = workspace.runtime.richThreads === true;
  const [selection, setSelection] = useState<Selection>({ id: "local", existing: false });
  const [visited, setVisited] = useState<Selection[]>([{ id: "local", existing: true }]);
  const [mainId, setMainId] = useState("local");
  const [loading, setLoading] = useState(enabled);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!enabled) return;
    let active = true;
    setLoading(true);
    setError("");
    void api
      .request<{ threadId: string; existing: boolean }>("/api/main-thread")
      .then((main) => {
        if (!active) return;
        const next = { id: main.threadId, existing: main.existing };
        setMainId(next.id);
        setSelection(next);
        setVisited([next]);
        setLoading(false);
      })
      .catch((e) => {
        if (active) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      active = false;
    };
  }, [api, enabled, attempt]);

  function select(next: Selection) {
    setSelection(next);
    setVisited((items) => (items.some((item) => item.id === next.id) ? items : [...items, next]));
    navigate("chat");
  }

  return (
    <ThreadContext.Provider
      value={{
        claimPrompt: (id) => {
          if (handledPrompt.current === id) return false;
          handledPrompt.current = id;
          return true;
        },
        enabled,
        mainId,
        visited,
        loading,
        error,
        retry: () => setAttempt((n) => n + 1),
        selection,
        select,
        start: () => select({ id: newThreadId(), existing: false }),
      }}
    >
      {children}
    </ThreadContext.Provider>
  );
}

export function useMuseThread() {
  const context = useContext(ThreadContext);
  if (!context) throw new Error("Threads provider is unavailable");
  return context;
}

function LocalThreadsSection({ onClose }: { onClose: () => void }) {
  const { t, lang } = useI18n();
  const { navigate } = useWorkspace();
  const { selection, visited, mainId, select, start } = useMuseThread();

  return (
    <>
      <LinkRow
        icon={MessageCircle}
        title={t("menu.mainChat")}
        detail={t("menu.savedLocal")}
        onPress={() => {
          select({ id: mainId, existing: true });
          navigate("chat");
          onClose();
        }}
      />
      <View style={[s.between, { marginTop: 8 }]}>
        <Text style={s.heading}>{t("menu.sideChats")}</Text>
        <Button
          small
          icon={Plus}
          onPress={() => {
            start();
            onClose();
          }}
        >
          {t("menu.newChat")}
        </Button>
      </View>
      {visited.filter((item) => item.id !== mainId).length === 0 ? (
        <Text style={s.muted}>
          {lang === "zh"
            ? "当前处于纯本地会话模式。所有对话与工作区状态自动保存在本地，无需联网或登录。"
            : "Running in local session mode. Messages and workspace state are saved locally with zero cloud dependencies."}
        </Text>
      ) : (
        visited
          .filter((item) => item.id !== mainId)
          .map((item, index) => (
            <LinkRow
              key={item.id}
              icon={MessageCircle}
              title={`${lang === "zh" ? "本地会话" : "Local Chat"} ${index + 1}`}
              detail={item.id === selection.id ? (lang === "zh" ? "当前活跃" : "Active") : (lang === "zh" ? "点击切换" : "Switch")}
              onPress={() => {
                select(item);
                onClose();
              }}
            />
          ))
      )}
    </>
  );
}

function RichThreadsSection({ onClose }: { onClose: () => void }) {
  const {
    selection,
    visited,
    mainId,
    loading,
    error: mainError,
    retry,
    select,
    start,
  } = useMuseThread();
  const threads = useThreads({ agentId: "default", enabled: true, includeArchived: true, limit: 20 });
  const [editing, setEditing] = useState<string>();
  const [name, setName] = useState("");
  const [error, setError] = useState("");
  const [archived, setArchived] = useState(false);

  async function mutate(action: () => Promise<void>) {
    setError("");
    try {
      await action();
      setEditing(undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  if (loading) {
    return (
      <>
        <ErrorNotice error={mainError} />
        {mainError ? (
          <Button onPress={retry}>Retry main chat</Button>
        ) : (
          <ActivityIndicator color={colors.blueDark} />
        )}
      </>
    );
  }

  return (
    <>
      <LinkRow
        icon={MessageCircle}
        title="Main chat"
        detail="Your ongoing conversation"
        onPress={() => {
          select({ id: mainId, existing: true });
          onClose();
        }}
      />
      <Button
        primary
        icon={Plus}
        onPress={() => {
          start();
          onClose();
        }}
      >
        New side chat
      </Button>
      <View style={[s.between, { marginTop: 12 }]}>
        <Text style={s.heading}>Side chats</Text>
        <Button small onPress={() => setArchived(!archived)}>
          {archived ? "Show active" : "Archived"}
        </Button>
      </View>
      {threads.isLoading && <ActivityIndicator color={colors.blueDark} />}
      <ErrorNotice error={error || threads.error?.message} />
      {threads.error && (
        <Button small onPress={threads.refetchThreads}>
          Retry conversations
        </Button>
      )}
      {!archived &&
        visited
          .filter(
            (item) =>
              item.id !== mainId && !threads.threads.some((saved) => saved.id === item.id),
          )
          .map((item, index) => (
            <LinkRow
              key={item.id}
              icon={MessageCircle}
              title={`Side chat ${index + 1}`}
              detail="Open in this app"
              onPress={() => {
                select(item);
                onClose();
              }}
            />
          ))}
      {threads.threads
        .filter((thread) => thread.id !== mainId && thread.archived === archived)
        .map((thread) => (
          <View
            key={thread.id}
            style={{
              paddingVertical: 12,
              borderBottomWidth: 1,
              borderBottomColor: colors.line,
              gap: 10,
            }}
          >
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`Open conversation: ${thread.name || "Untitled conversation"}`}
              accessibilityState={{ selected: selection.id === thread.id }}
              onPress={() => {
                select({ id: thread.id, existing: true });
                onClose();
              }}
              style={[s.row, { gap: 10 }]}
            >
              <MessageCircle size={19} color={colors.text} />
              <Text style={[s.text, { flex: 1 }]}>
                {thread.name || "Untitled conversation"}
              </Text>
            </Pressable>
            {editing === thread.id && (
              <Field label="Conversation name" value={name} onChangeText={setName} />
            )}
            <View style={[s.row, { gap: 8 }]}>
              <Button
                small
                disabled={threads.isMutating || (editing === thread.id && !name.trim())}
                onPress={() => {
                  if (editing === thread.id)
                    void mutate(() => threads.renameThread(thread.id, name.trim()));
                  else {
                    setEditing(thread.id);
                    setName(thread.name || "");
                  }
                }}
              >
                {editing === thread.id ? "Save name" : "Rename"}
              </Button>
              <Button
                small
                icon={Archive}
                disabled={threads.isMutating}
                onPress={() =>
                  void mutate(() =>
                    thread.archived
                      ? threads.unarchiveThread(thread.id)
                      : threads.archiveThread(thread.id),
                  )
                }
              >
                {thread.archived ? "Restore" : "Archive"}
              </Button>
            </View>
          </View>
        ))}
      {!threads.isLoading &&
        !threads.error &&
        !threads.threads.some(
          (thread) => thread.id !== mainId && thread.archived === archived,
        ) && (
          <Text style={s.muted}>
            {archived
              ? "No archived conversations."
              : "Keep a separate topic here. Your main chat is always available."}
          </Text>
        )}
      <ErrorNotice error={threads.fetchMoreError?.message} />
      {threads.hasMoreThreads && (
        <Button small busy={threads.isFetchingMoreThreads} onPress={threads.fetchMoreThreads}>
          Load more conversations
        </Button>
      )}
      <Text style={s.small}>
        Side chats keep their own conversation context. Your agent’s saved memory is shared.
      </Text>
    </>
  );
}

export function ThreadsSheet({ onClose }: { onClose: () => void }) {
  const { enabled } = useMuseThread();
  const { t, lang, toggleLang } = useI18n();
  const { workspace, open, navigate, refresh } = useWorkspace();
  const [error, setError] = useState("");

  async function mutate(action: () => Promise<void>) {
    setError("");
    try {
      await action();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  function go(section: "calendar" | "files" | "apps" | "code" | "skills") {
    onClose();
    navigate(section);
  }

  return (
    <Sheet
      title={lang === "zh" ? "工作台菜单" : "Workbench Menu"}
      subtitle={
        workspace.mode === "sample"
          ? lang === "zh"
            ? "本地持久工作区"
            : "Local persistent workspace"
          : workspace.profile.name
      }
      onClose={onClose}
    >
      <View style={{ gap: 14 }}>
        {enabled ? (
          <RichThreadsSection onClose={onClose} />
        ) : (
          <LocalThreadsSection onClose={onClose} />
        )}

        <View style={s.divider} />

        <LinkRow
          icon={Code2}
          title={t("menu.codeWorkbench")}
          detail={t("menu.codeWorkbenchDesc")}
          onPress={() => go("code")}
        />
        <LinkRow
          icon={Sparkles}
          title={t("menu.skills")}
          detail={t("menu.skillsDesc")}
          onPress={() => go("skills")}
        />
        <LinkRow
          icon={Plus}
          title={t("menu.delegate")}
          detail={t("menu.delegateDesc")}
          onPress={() => {
            onClose();
            open({ type: "delegate" });
          }}
        />
        <LinkRow
          icon={Monitor}
          title={t("menu.computer")}
          detail={t("menu.computerDesc")}
          onPress={() => {
            onClose();
            open({ type: "computer" });
          }}
        />
        <LinkRow icon={CalendarDays} title={t("section.calendar.title")} onPress={() => go("calendar")} />
        <LinkRow icon={FileText} title={t("section.files.title")} onPress={() => go("files")} />
        <LinkRow icon={Settings2} title={t("menu.settings")} onPress={() => go("apps")} />

        <View style={[s.row, { gap: 10, marginTop: 4 }]}>
          <Button
            small
            icon={Globe}
            onPress={toggleLang}
            style={{ flex: 1 }}
          >
            {lang === "zh" ? "Switch to English" : "切换为中文"}
          </Button>
          <Button
            small
            icon={RefreshCw}
            onPress={() => void mutate(refresh)}
            style={{ flex: 1 }}
          >
            {t("common.refresh")}
          </Button>
        </View>
        <ErrorNotice error={error} />
      </View>
    </Sheet>
  );
}
