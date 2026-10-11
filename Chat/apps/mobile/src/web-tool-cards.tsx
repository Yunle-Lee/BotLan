import { Calculator, Clock, FileJson, Globe2, Link2, Search } from "lucide-react-native";
import { type ReactNode, useContext, useState } from "react";
import { ActivityIndicator, Linking, Pressable, Text, View } from "react-native";
import { z } from "zod";
import { isSafeAssistantUrl } from "./assistant-markdown";
import { BrowserRunContext } from "./browser-tool-card";
import { useI18n } from "./i18n";
import { Card, colors, ErrorNotice, s } from "./ui";

/** Cards for the server web tools: web_search, fetch_url, read_more, extract_links, browse_web (direct), get_time, calculate. */

function parse(result: unknown) {
  if (typeof result !== "string") return result;
  try {
    return JSON.parse(result);
  } catch {
    return undefined;
  }
}
const errorOf = (value: unknown) => z.object({ error: z.string() }).safeParse(value).data?.error;
function domain(url: string) {
  try {
    return new URL(url).hostname.replace(/^www\./, "");
  } catch {
    return url;
  }
}

function useOpener() {
  const [error, setError] = useState("");
  const open = (url: string) => {
    if (!isSafeAssistantUrl(url)) return;
    setError("");
    void Linking.openURL(url).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  };
  return { open, error };
}

/** Letter badge instead of a remote favicon, so rendering a card never contacts third parties. */
function SiteBadge({ url }: { url: string }) {
  const name = domain(url);
  let hue = 0;
  for (const ch of name) hue = (hue * 31 + ch.charCodeAt(0)) % 360;
  return (
    <View
      style={{
        width: 22,
        height: 22,
        borderRadius: 6,
        alignItems: "center",
        justifyContent: "center",
        backgroundColor: `hsl(${hue}, 55%, 90%)`,
      }}
    >
      <Text style={{ fontSize: 11, fontWeight: "700", color: `hsl(${hue}, 45%, 32%)` }}>
        {name.charAt(0).toUpperCase()}
      </Text>
    </View>
  );
}

function Shell({
  icon: Icon,
  title,
  subtitle,
  loading,
  children,
}: {
  icon: typeof Globe2;
  title: string;
  subtitle?: string;
  loading: boolean;
  children?: ReactNode;
}) {
  const { active } = useContext(BrowserRunContext);
  return (
    <Card style={{ padding: 13, backgroundColor: "#EEEEF0", gap: 10, width: "100%", maxWidth: 440 }}>
      <View style={[s.row, { gap: 10 }]}>
        <View style={[s.iconBox, { width: 32, height: 32, borderRadius: 9 }]}>
          <Icon size={18} color={colors.blueDark} />
        </View>
        <View style={{ flex: 1, gap: 1 }}>
          <Text style={[s.text, { fontWeight: "600" }]}>{title}</Text>
          {!!subtitle && (
            <Text numberOfLines={1} style={[s.small, { fontSize: 12 }]}>
              {subtitle}
            </Text>
          )}
        </View>
        {loading && active ? <ActivityIndicator size="small" color={colors.blueDark} /> : null}
      </View>
      {children}
    </Card>
  );
}

function LinkList({ items, open }: { items: { title: string; url: string; snippet?: string }[]; open: (url: string) => void }) {
  return (
    <View style={{ backgroundColor: "#FAFAFB", borderRadius: 12, paddingVertical: 4 }}>
      {items.map((item) => (
        <Pressable
          key={item.url}
          accessibilityRole="link"
          accessibilityLabel={`${item.title}, ${domain(item.url)}`}
          onPress={() => open(item.url)}
          style={({ pressed }) => ({ padding: 10, gap: 3, opacity: pressed ? 0.6 : 1 })}
        >
          <View style={[s.row, { gap: 8 }]}>
            <SiteBadge url={item.url} />
            <Text numberOfLines={1} style={[s.small, { flex: 1, fontSize: 12 }]}>
              {domain(item.url)}
            </Text>
          </View>
          <Text numberOfLines={2} style={[s.text, { fontSize: 14, color: colors.blueDark }]}>
            {item.title}
          </Text>
          {!!item.snippet && (
            <Text numberOfLines={2} style={[s.small, { fontSize: 12 }]}>
              {item.snippet}
            </Text>
          )}
        </Pressable>
      ))}
    </View>
  );
}

const searchSchema = z.object({
  provider: z.string().optional(),
  results: z.array(z.object({ title: z.string(), url: z.string(), snippet: z.string() })),
});

export function WebSearchCard({ args, result, loading }: { args: Record<string, unknown>; result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const { open, error } = useOpener();
  const value = parse(result);
  const data = searchSchema.safeParse(value).data;
  const query = typeof args.query === "string" ? args.query : "";
  return (
    <Shell
      icon={Search}
      title={zh ? "网页搜索" : "Web search"}
      subtitle={
        loading
          ? `${zh ? "正在搜索" : "Searching"} “${query}”…`
          : data
            ? `“${query}” · ${data.results.length} ${zh ? "条结果" : "results"}${data.provider ? ` · ${data.provider}` : ""}`
            : query
      }
      loading={loading}
    >
      {data && data.results.length > 0 && <LinkList items={data.results} open={open} />}
      <ErrorNotice error={errorOf(value) || error} />
    </Shell>
  );
}

const linksSchema = z.object({
  url: z.string(),
  total: z.number(),
  links: z.array(z.object({ text: z.string(), url: z.string() })),
});

export function LinksCard({ result, loading }: { result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const { open, error } = useOpener();
  const value = parse(result);
  const data = linksSchema.safeParse(value).data;
  return (
    <Shell
      icon={Link2}
      title={zh ? "页面链接" : "Page links"}
      subtitle={
        loading
          ? zh ? "正在列出链接…" : "Listing links…"
          : data ? `${domain(data.url)} · ${data.total} ${zh ? "个链接" : "links"}` : undefined
      }
      loading={loading}
    >
      {data && data.links.length > 0 && (
        <LinkList items={data.links.slice(0, 8).map((l) => ({ title: l.text, url: l.url }))} open={open} />
      )}
      <ErrorNotice error={errorOf(value) || error} />
    </Shell>
  );
}

const pageSchema = z.object({
  url: z.string(),
  title: z.string().optional(),
  text: z.string(),
  offset: z.number().optional(),
  totalChars: z.number().optional(),
  nextOffset: z.number().optional(),
});

/** browse_web without the worker browser, and read_more: a text excerpt of the page. */
export function PageTextCard({ result, loading, more = false }: { result: unknown; loading: boolean; more?: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const { open, error } = useOpener();
  const value = parse(result);
  const page = pageSchema.safeParse(value).data;
  const range =
    page?.totalChars != null
      ? `${zh ? "字符" : "chars"} ${(page.offset ?? 0).toLocaleString()}–${((page.offset ?? 0) + page.text.length).toLocaleString()} / ${page.totalChars.toLocaleString()}`
      : "";
  return (
    <Shell
      icon={Globe2}
      title={more ? (zh ? "继续阅读" : "Reading more") : zh ? "网页" : "Web page"}
      subtitle={loading ? (zh ? "正在读取页面…" : "Reading the page…") : page ? `${domain(page.url)}${range ? ` · ${range}` : ""}` : undefined}
      loading={loading}
    >
      {page && (
        <Pressable
          accessibilityRole="link"
          onPress={() => open(page.url)}
          style={{ backgroundColor: "#FAFAFB", borderRadius: 12, padding: 12, gap: 6 }}
        >
          {!!page.title && (
            <Text numberOfLines={2} style={[s.text, { fontSize: 14, color: colors.blueDark }]}>
              {page.title}
            </Text>
          )}
          <Text numberOfLines={4} style={[s.small, { fontSize: 12 }]}>
            {page.text.slice(0, 400)}
          </Text>
        </Pressable>
      )}
      <ErrorNotice error={errorOf(value) || error} />
    </Shell>
  );
}

const fetchSchema = z.object({
  results: z.array(
    z.object({
      url: z.string(),
      status: z.number().optional(),
      contentType: z.string().optional(),
      body: z.string().optional(),
      truncated: z.boolean().optional(),
      error: z.string().optional(),
    }),
  ),
});

export function FetchCard({ result, loading }: { result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const zh = lang === "zh";
  const { open, error } = useOpener();
  const value = parse(result);
  const data = fetchSchema.safeParse(value).data;
  return (
    <Shell
      icon={FileJson}
      title={zh ? "获取网址" : "Fetch URL"}
      subtitle={loading ? (zh ? "正在请求…" : "Requesting…") : data ? `${data.results.length} ${zh ? "个请求" : data.results.length === 1 ? "request" : "requests"}` : undefined}
      loading={loading}
    >
      {data?.results.map((item) => (
        <View key={item.url} style={{ backgroundColor: "#FAFAFB", borderRadius: 12, padding: 10, gap: 5 }}>
          <Pressable accessibilityRole="link" onPress={() => open(item.url)} style={[s.row, { gap: 8 }]}>
            <SiteBadge url={item.url} />
            <Text numberOfLines={1} style={[s.small, { flex: 1, fontSize: 12, color: colors.blueDark }]}>
              {item.url}
            </Text>
            {item.status != null && (
              <Text style={[s.small, { fontSize: 11, color: item.status < 400 ? "#47896C" : "#B4532A" }]}>
                {item.status}
              </Text>
            )}
          </Pressable>
          {item.error ? (
            <Text style={[s.small, { fontSize: 12, color: "#B4532A" }]}>{item.error}</Text>
          ) : (
            <Text numberOfLines={5} style={{ fontFamily: "monospace", fontSize: 11, color: colors.text }}>
              {(item.body ?? "").slice(0, 500)}
            </Text>
          )}
          {item.truncated && <Text style={[s.small, { fontSize: 11 }]}>{zh ? "内容已截断" : "Truncated"}</Text>}
        </View>
      ))}
      <ErrorNotice error={errorOf(value) || error} />
    </Shell>
  );
}

export function CalculatorCard({ args, result, loading }: { args: Record<string, unknown>; result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const value = parse(result);
  const data = z.object({ expression: z.string(), result: z.number() }).safeParse(value).data;
  const expression = typeof args.expression === "string" ? args.expression : "";
  return (
    <Shell icon={Calculator} title={lang === "zh" ? "计算" : "Calculator"} loading={loading}>
      <View style={{ backgroundColor: "#FAFAFB", borderRadius: 12, padding: 12, gap: 4 }}>
        <Text style={[s.small, { fontFamily: "monospace", fontSize: 12 }]}>{data?.expression ?? expression}</Text>
        {data && (
          <Text accessibilityLabel={`= ${data.result}`} style={[s.text, { fontSize: 20, fontWeight: "600" }]}>
            = {data.result.toLocaleString(undefined, { maximumFractionDigits: 12 })}
          </Text>
        )}
      </View>
      <ErrorNotice error={errorOf(value)} />
    </Shell>
  );
}

export function TimeCard({ result, loading }: { result: unknown; loading: boolean }) {
  const { lang } = useI18n();
  const value = parse(result);
  const data = z
    .object({ local: z.string(), weekday: z.string(), timeZone: z.string(), offset: z.string().optional() })
    .safeParse(value).data;
  return (
    <Shell
      icon={Clock}
      title={lang === "zh" ? "当前时间" : "Current time"}
      subtitle={data ? `${data.local} · ${data.weekday} · ${data.timeZone}${data.offset ? ` (${data.offset})` : ""}` : undefined}
      loading={loading}
    >
      <ErrorNotice error={errorOf(value)} />
    </Shell>
  );
}

/** browse_web results from the direct reader carry no browser session to preview. */
export function isDirectPage(result: unknown) {
  const value = parse(result);
  return !!value && typeof value === "object" && (value as { via?: unknown }).via === "direct";
}
