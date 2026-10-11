import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";
import { test } from "node:test";
import {
  assertPublicUrl,
  calculate,
  clip,
  htmlToPage,
  isPublicIp,
  jsonShape,
  localTime,
  parseBing,
  parseDuckDuckGo,
  type Resolver,
  safeGet,
  WebService,
} from "../apps/server/src/web-tools.ts";

const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), "utf8");
const dns =
  (table: Record<string, string[]>): Resolver =>
  async (host) =>
    (table[host] ?? []).map((address) => ({ address, family: address.includes(":") ? 6 : 4 }));

test("public IP classification", () => {
  for (const ip of ["8.8.8.8", "104.20.23.154", "2606:4700::1"]) assert.ok(isPublicIp(ip), ip);
  for (const ip of [
    "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1",
    "0.0.0.0", "198.18.0.92", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:127.0.0.1",
    "::ffff:10.0.0.1", "2001:db8::1",
  ])
    assert.ok(!isPublicIp(ip), ip);
});

test("SSRF guard blocks private targets, credentials and non-http schemes", async () => {
  const resolve = dns({
    "good.test": ["93.184.216.34"],
    "rebind.test": ["93.184.216.34", "127.0.0.1"],
    "inside.test": ["10.0.0.5"],
    "meta.test": ["169.254.169.254"],
    "v6local.test": ["::1"],
  });
  const ok = await assertPublicUrl("https://good.test/path", resolve);
  assert.equal(ok.address, "93.184.216.34");
  for (const url of [
    "http://127.0.0.1/",
    "http://[::1]/",
    "http://169.254.169.254/latest/meta-data",
    "http://rebind.test/",
    "http://inside.test/",
    "http://meta.test/",
    "http://v6local.test/",
    "http://localhost/",
    "http://printer.local/",
    "https://user:pass@good.test/",
    "https://good.test:8443/",
    "file:///etc/passwd",
    "ftp://good.test/",
    "javascript:alert(1)",
    "not a url",
  ])
    await assert.rejects(assertPublicUrl(url, resolve), /Blocked/, url);
  await assert.rejects(assertPublicUrl("https://none.test/", resolve), /Blocked|resolve/);
  await assert.rejects(
    assertPublicUrl("https://fake.test/", dns({ "fake.test": ["198.18.0.7"] })),
    /fake-IP/,
  );
});

test("safeGet refuses loopback servers and redirects into private space", async () => {
  const server = createServer((_req, res) => res.end("secret"));
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", done));
  const { port } = server.address() as { port: number };
  try {
    await assert.rejects(safeGet(`http://127.0.0.1:${port}/`), /Blocked/);
    // A public hostname that resolves to loopback (DNS rebinding) is refused before connecting.
    await assert.rejects(safeGet("http://evil.test/", { resolve: dns({ "evil.test": ["127.0.0.1"] }) }), /Blocked/);
  } finally {
    server.close();
  }
});

test("DuckDuckGo HTML results parse from a saved page", () => {
  const results = parseDuckDuckGo(fixture("ddg.html"));
  assert.ok(results.length >= 5);
  for (const result of results) {
    assert.match(result.url, /^https?:\/\//);
    assert.ok(!/duckduckgo\.com\/l\//.test(result.url), "redirect wrapper unwrapped");
    assert.ok(result.title.length > 0);
  }
  assert.ok(results.some((r) => r.url.startsWith("https://www.nvidia.com/")));
  assert.ok(results.some((r) => /GB10/.test(r.snippet)));
});

test("Bing fallback results parse from a saved page", () => {
  const results = parseBing(fixture("bing.html"));
  assert.ok(results.length >= 3);
  assert.ok(results.every((r) => /^https?:\/\//.test(r.url) && !/bing\.com/.test(new URL(r.url).hostname)));
});

test("search uses a configured SearXNG provider first", async () => {
  const web = new WebService({
    env: { SEARXNG_URL: "http://searx.lan:8888" },
    fetcher: (async (url: string | URL) => {
      assert.match(String(url), /^http:\/\/searx\.lan:8888\/search\?q=spark&format=json/);
      return Response.json({ results: [{ title: "A", url: "https://a.example/", content: "x".repeat(900) }] });
    }) as typeof fetch,
  });
  const found = await web.search("owner", "spark");
  assert.equal(found.provider, "searxng");
  assert.equal(found.results[0]?.snippet.length, 260);
});

test("calculator evaluates safely", () => {
  const cases: [string, number][] = [
    ["1+2*3", 7], ["(1+2)*3", 9], ["2^3^2", 512], ["2**10", 1024], ["-2^2", -4], ["10/4", 2.5],
    ["7 % 3", 1], ["15% * 80", 12], ["50%", 0.5], ["sqrt(16)+abs(-3)", 7], ["max(1, 5, 3)", 5],
    ["pi", 3.14159265358979], ["log(1000)", 3], ["ln(e)", 1], ["0.1+0.2", 0.3], ["1,000 * 3", 3000],
    ["3 × 4 ÷ 2", 6], ["1e3 + .5", 1000.5],
  ];
  for (const [expression, expected] of cases) assert.equal(calculate(expression), expected, expression);
  for (const bad of ["", "1/0", "2+", "(1", "alert(1)", "constructor", "1;2", "process.exit()", "9^999", "(".repeat(200)])
    assert.throws(() => calculate(bad), Error, bad);
});

test("long page text is clipped with a continuation offset", () => {
  const text = "a".repeat(15000);
  const first = clip(text);
  assert.equal(first.text.length, 6000);
  assert.equal(first.nextOffset, 6000);
  assert.ok(first.truncated);
  const last = clip(text, 12000);
  assert.equal(last.text.length, 3000);
  assert.equal(last.nextOffset, undefined);
  assert.deepEqual(clip("short"), { text: "short", offset: 0, totalChars: 5, truncated: false });
  assert.match(jsonShape({ a: [{ b: 1 }], c: null }), /^\{a:array\(1\)\[\{b:number\}\],c:null\}$/);
});

test("HTML is reduced to text and absolute links", () => {
  const page = htmlToPage(
    `<html><head><title> Hi </title><script>evil()</script></head><body><h2>Part</h2><p>Hello <a href="/x#y">next page</a></p><a href="javascript:1">bad</a><ul><li>one</li></ul></body></html>`,
    "https://site.example/dir/",
  );
  assert.equal(page.title, "Hi");
  assert.match(page.text, /## Part/);
  assert.match(page.text, /- one/);
  assert.ok(!page.text.includes("evil"));
  assert.deepEqual(page.links, [{ text: "next page", url: "https://site.example/x" }]);
});

test("get_time honours IANA zones and rejects unknown ones", () => {
  const now = new Date("2026-10-10T04:05:06Z");
  assert.equal(localTime("Asia/Shanghai", now).local, "2026-10-10 12:05:06");
  assert.equal(localTime("Asia/Shanghai", now).weekday, "Saturday");
  assert.throws(() => localTime("Mars/Base", now));
});
