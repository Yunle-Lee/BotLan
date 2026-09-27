import { ChevronRight, FileText } from "lucide-react-native";
import { useEffect, useState } from "react";
import { Pressable, Text, View } from "react-native";
import type { Artifact, BrowserSession } from "../../../packages/domain/src";
import type { AgentArtifact, AgentTask } from "../../../packages/domain/src/agent";
import { ArtifactCard, TaskCard } from "./agent-ui";
import { BrowserThreadCard } from "./computer";
import { Button, Card, colors, ErrorNotice, s } from "./ui";
import { useWorkspace } from "./workspace";

export function FileThreadCard({ file }: { file: Artifact }) {
  const { open } = useWorkspace();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Open PDF: ${file.name}`}
      onPress={() => open({ type: "file", file })}
      style={{ width: "100%", maxWidth: 440 }}
    >
      <Card style={{ padding: 18, backgroundColor: "#F0F1F2", gap: 18 }}>
        <View style={{ borderRadius: 12, padding: 22, backgroundColor: "#FFF", gap: 14 }}>
          <Text style={[s.heading, { fontSize: 18 }]}>{file.name.replace(/\.pdf$/i, "")}</Text>
          {file.fields?.length ? (
            file.fields.slice(0, 4).map((field) => (
              <View
                key={field.name}
                style={{
                  gap: 5,
                  borderBottomWidth: 1,
                  borderBottomColor: colors.line,
                  paddingBottom: 9,
                }}
              >
                <Text style={[s.small, { fontSize: 9 }]}>
                  {field.name.replace(/_/g, " ").toUpperCase()}
                </Text>
                <Text style={[s.text, { fontSize: 12 }]}>{field.value || "—"}</Text>
              </View>
            ))
          ) : (
            <Text style={s.muted}>
              {file.pageCount} {file.pageCount === 1 ? "page" : "pages"} · Tap to read the document
            </Text>
          )}
        </View>
        <View style={[s.row, { gap: 13 }]}>
          <View style={{ backgroundColor: "#FC2359", padding: 9, borderRadius: 9 }}>
            <FileText size={23} color="#FFF" />
          </View>
          <View style={{ flex: 1, gap: 3 }}>
            <Text numberOfLines={2} style={s.heading}>
              {file.name}
            </Text>
            <Text style={s.muted}>PDF</Text>
          </View>
          <ChevronRight size={18} color={colors.muted} />
        </View>
      </Card>
    </Pressable>
  );
}
/** Hydrates task-linked artifacts by ID on replay; signed URLs are never stored in messages. */
export function TaskThreadCard({ task }: { task: AgentTask }) {
  const { api } = useWorkspace();
  const [detail, setDetail] = useState<{
    artifacts: AgentArtifact[];
    files: Artifact[];
    browsers: BrowserSession[];
  }>();
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true;
    void api
      .request<{ artifacts: AgentArtifact[]; files: Artifact[]; browsers: BrowserSession[] }>(
        `/api/agent/tasks/${task.id}`,
      )
      .then((result) => {
        if (active) {
          setDetail(result);
          setError("");
        }
      })
      .catch((e) => {
        if (active) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      active = false;
    };
  }, [api, task.id, task.updatedAt, attempt]);
  return (
    <View style={{ gap: 12 }}>
      <TaskCard task={task} compact />
      {detail?.browsers.map((browser) => (
        <BrowserThreadCard key={browser.id} browser={browser} />
      ))}
      {[...(detail?.files || [])]
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
        .slice(0, 1)
        .map((file) => (
          <FileThreadCard key={file.id} file={file} />
        ))}
      {detail?.artifacts.map((artifact) => (
        <ArtifactCard key={artifact.id} artifact={artifact} />
      ))}
      <ErrorNotice error={error} />
      {!!error && (
        <Button small onPress={() => setAttempt((value) => value + 1)}>
          Reload task results
        </Button>
      )}
    </View>
  );
}
