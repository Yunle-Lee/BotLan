import { ChevronDown, ChevronUp, RotateCcw } from "lucide-react-native";
import { useCallback, useMemo, useState } from "react";
import { Linking, Platform, ScrollView, Text, type TextStyle, View, type ViewStyle } from "react-native";
import Markdown, { type MarkdownStyles, type RenderRules } from "react-native-markdown-renderer";
import { assistantMarkdown, fenceLanguage, isLongOutput, isSafeAssistantUrl, withTaskLists } from "./assistant-markdown";
import { useI18n } from "./i18n";
import { Button, colors, CopyButton, ErrorNotice, s } from "./ui";

const mono = Platform.select({ ios: "Menlo", android: "monospace", default: "Consolas, 'Cascadia Mono', Menlo, monospace" });
const textStyle = { color: colors.text, fontSize: 16, lineHeight: 24 };
const style: Partial<MarkdownStyles> = {
  text: textStyle,
  paragraph: { marginTop: 0, marginBottom: 6 },
  list: { marginBottom: 6 },
  headingContainer: { marginTop: 10, marginBottom: 4 },
  heading1: { fontSize: 21, lineHeight: 28, fontWeight: "700", letterSpacing: -0.4 },
  heading2: { fontSize: 18.5, lineHeight: 25, fontWeight: "700", letterSpacing: -0.3 },
  heading3: { fontSize: 16.5, lineHeight: 23, fontWeight: "700" },
  heading4: { fontSize: 15, lineHeight: 22, fontWeight: "700" },
  heading5: { fontSize: 14, lineHeight: 21, fontWeight: "700", color: colors.muted },
  heading6: { fontSize: 13, lineHeight: 20, fontWeight: "700", color: colors.muted },
  strong: { fontWeight: "700" },
  link: { color: colors.blueDark, textDecorationLine: "underline" },
  codeInline: {
    fontFamily: mono, fontSize: 14, backgroundColor: "#E2E4E7", color: "#7A2E2A",
    borderRadius: 5, paddingHorizontal: 4,
  },
  blockquote: {
    borderLeftWidth: 3, borderLeftColor: "#B9C3CB", backgroundColor: "#E6E8EB", borderRadius: 6,
    paddingHorizontal: 12, paddingVertical: 6, marginBottom: 8,
  },
  hr: { backgroundColor: "#D5D9DD", height: 1, marginVertical: 12 },
  table: { borderWidth: 1, borderColor: "#D0D7DE", borderRadius: 6, width: "auto", alignSelf: "flex-start" },
  tableRow: { flexDirection: "row", borderBottomWidth: 1, borderColor: "#D0D7DE", flexShrink: 0 },
  tableHeader: { backgroundColor: "#E6E8EB" },
};

/** Fenced code: language label + copy, monospace, scrolls sideways instead of wrapping. */
function CodeBlock({ code, language }: { code: string; language: string }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  return (
    <View style={{ backgroundColor: "#151B1E", borderRadius: 10, marginBottom: 8, overflow: "hidden" }}>
      <View style={[s.between, { paddingLeft: 12, paddingRight: 4, paddingVertical: 2, backgroundColor: "#20282C" }]}>
        <Text style={{ color: "#A9B4BA", fontSize: 11, fontFamily: mono }}>{language || (zh ? "代码" : "code")}</Text>
        <CopyButton text={code} withText onDark label={zh ? "复制" : "Copy"} copiedLabel={zh ? "已复制" : "Copied"}
          failedLabel={zh ? "复制失败" : "Copy failed"} />
      </View>
      <ScrollView horizontal showsHorizontalScrollIndicator contentContainerStyle={{ padding: 12 }}>
        <Text selectable style={{ fontFamily: mono, fontSize: 13, lineHeight: 20, color: "#E4ECEF" }}>{code}</Text>
      </ScrollView>
    </View>
  );
}

const renderCodeBlock: RenderRules["fence"] = (node) => (
  <CodeBlock key={node.key} code={node.content.replace(/\n$/, "")} language={fenceLanguage(node.sourceInfo)} />
);
// Fixed column width; the table scrolls sideways when it is wider than the bubble.
const cell: ViewStyle = { flexGrow: 0, flexShrink: 0, flexBasis: "auto", width: 150, paddingHorizontal: 10, paddingVertical: 4 };
const rules: RenderRules = {
  textgroup: (node, children) => (
    <Text key={node.key} selectable style={textStyle}>
      {children}
    </Text>
  ),
  image: (node) => (
    <Text key={node.key} selectable style={{ color: colors.muted }}>
      {node.attributes.alt ? `[Image: ${node.attributes.alt}]` : "[Image]"}
    </Text>
  ),
  code_inline: (node, _children, _parent, styles) => (
    <Text key={node.key} selectable style={styles.codeInline as TextStyle}>{node.content}</Text>
  ),
  code_block: renderCodeBlock,
  fence: renderCodeBlock,
  // Wide tables scroll sideways instead of squeezing every cell to a letter per line.
  table: (node, children, _parent, styles) => (
    <ScrollView key={node.key} horizontal showsHorizontalScrollIndicator style={{ marginBottom: 6 }}>
      <View style={styles.table as ViewStyle}>{children}</View>
    </ScrollView>
  ),
  th: (node, children, _parent, styles) => (
    <View key={node.key} style={[styles.tableHeaderCell as ViewStyle, cell]}>{children}</View>
  ),
  td: (node, children, _parent, styles) => (
    <View key={node.key} style={[styles.tableRowCell as ViewStyle, cell]}>{children}</View>
  ),
};

export function AssistantResponse({ content, streaming = false }: { content: string; streaming?: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const [linkError, setLinkError] = useState("");
  const long = !streaming && isLongOutput(content);
  const [expanded, setExpanded] = useState(false);
  const source = useMemo(() => withTaskLists(content), [content]);
  const onLinkPress = useCallback((url: string) => {
    if (!isSafeAssistantUrl(url)) return false;
    setLinkError("");
    void Linking.openURL(url).catch((error) =>
      setLinkError(error instanceof Error ? error.message : String(error)),
    );
    return false;
  }, []);
  const collapsed = long && !expanded;
  return (
    <>
      <View style={collapsed ? { maxHeight: 420, overflow: "hidden" } : undefined}>
        <Markdown markdownit={assistantMarkdown} style={style} rules={rules} onLinkPress={onLinkPress}>
          {source}
        </Markdown>
        {collapsed && (
          <View pointerEvents="none" style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: 48,
            backgroundColor: "rgba(238,238,240,0.85)" }} />
        )}
      </View>
      {long && (
        <Button
          variant="ghost"
          small
          icon={expanded ? ChevronUp : ChevronDown}
          accessibilityLabel={expanded ? (zh ? "收起长回复" : "Collapse long reply") : (zh ? "展开完整回复" : "Show the full reply")}
          style={{ alignSelf: "flex-start", minHeight: 30, paddingVertical: 4 }}
          onPress={() => setExpanded(!expanded)}
        >
          {expanded ? (zh ? "收起" : "Show less") : (zh ? "展开全部" : "Show all")}
        </Button>
      )}
      <ErrorNotice error={linkError} />
    </>
  );
}

/** Under an assistant reply: copy it, regenerate it (latest reply only), and its run stats. */
export function MessageActions({ text, onRegenerate, regenerateDisabled, children }: {
  text: string;
  onRegenerate?: () => void;
  regenerateDisabled?: boolean;
  children?: React.ReactNode;
}) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  return (
    <View style={[s.row, { gap: 2, flexWrap: "wrap", minHeight: 30 }]}>
      {!!text && (
        <CopyButton text={text} label={zh ? "复制回复" : "Copy reply"} copiedLabel={zh ? "已复制" : "Copied"}
          failedLabel={zh ? "复制失败" : "Copy failed"} />
      )}
      {onRegenerate && (
        <Button variant="icon" small icon={RotateCcw} disabled={regenerateDisabled}
          accessibilityLabel={zh ? "重新生成回复" : "Regenerate reply"} onPress={onRegenerate} />
      )}
      {children ? <View style={{ paddingLeft: 4, flexShrink: 1 }}>{children}</View> : null}
    </View>
  );
}
