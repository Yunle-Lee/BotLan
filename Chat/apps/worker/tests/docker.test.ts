import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import test from "node:test";

// Run against an explicitly started disposable worker; the token is never logged.
let base = process.env.WORKER_TEST_URL ?? "http://127.0.0.1:8790";
const token = process.env.WORKER_TOKEN;
const container = process.env.WORKER_TEST_CONTAINER;
if (!token) throw new Error("Set WORKER_TOKEN for the running test worker.");
const headers = { authorization: `Bearer ${token}`, "content-type": "application/json" };
async function api(path: string, body?: unknown) {
  const response = await fetch(`${base}${path}`, {
    headers,
    method: body === undefined ? "GET" : "POST",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  assert(response.ok, `${path}: ${response.status} ${response.ok ? "" : await response.text()}`);
  return response;
}

test("Docker Chromium opens public sites, renders, navigates and restores its saved profile", {
  timeout: 120_000,
}, async () => {
  const id = randomUUID();
  try {
    const unauthorized = await fetch(`${base}/sessions`);
    assert.equal(unauthorized.status, 401);
    const created = await (await api("/sessions", { id, url: "https://example.com" })).json();
    assert.equal(created.status, "active");
    assert.equal(created.title, "Example Domain");
    const observed = await (await api(`/sessions/${id}/read`)).json();
    assert.equal(observed.url, "https://example.com/");
    assert.equal(observed.title, "Example Domain");
    assert.match(observed.text, /Example Domain/);
    assert.equal(observed.truncated, false);
    const image = Buffer.from(await (await api(`/sessions/${id}/screenshot`)).arrayBuffer());
    assert.equal(image.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
    assert.equal(image.readUInt32BE(16), 1280);
    assert.equal(image.readUInt32BE(20), 800);
    await api(`/sessions/${id}/input`, { type: "scroll", deltaY: 500 });
    const navigated = await (
      await api(`/sessions/${id}/navigate`, { url: "https://example.org" })
    ).json();
    assert.equal(navigated.url, "https://example.org/");
    const blocked = await fetch(`${base}/sessions/${id}/navigate`, {
      headers,
      method: "POST",
      body: JSON.stringify({ url: "http://169.254.169.254/latest/meta-data" }),
    });
    assert.equal(blocked.status, 400);
    const redirectUrl =
      "https://httpbin.org/redirect-to?url=http%3A%2F%2F127.0.0.1%3A8790%2Fhealth";
    const redirectSource = await fetch(redirectUrl, { redirect: "manual" });
    assert.equal(redirectSource.status, 302, "public redirect fixture must return a real redirect");
    assert.equal(redirectSource.headers.get("location"), "http://127.0.0.1:8790/health");
    const redirect = await fetch(`${base}/sessions/${id}/navigate`, {
      headers,
      method: "POST",
      body: JSON.stringify({ url: redirectUrl }),
    });
    assert.equal(redirect.status, 502, "redirect to worker loopback must be blocked");
    await api(`/sessions/${id}/navigate`, {
      url: "https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf",
    });
    let downloads: { id: string; name: string; size: number; mimeType: string }[] = [];
    for (let attempt = 0; attempt < 30; attempt++) {
      downloads = (await (await api(`/sessions/${id}/downloads`)).json()).downloads;
      if (downloads.length) break;
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    assert.equal(downloads.length, 1, "public PDF is captured as a download");
    assert.equal(downloads[0]?.mimeType, "application/pdf");
    const pdf = Buffer.from(
      await (await api(`/sessions/${id}/downloads/${downloads[0]?.id}`)).arrayBuffer(),
    );
    assert.equal(pdf.subarray(0, 5).toString(), "%PDF-");
    assert(pdf.length > 1000 && pdf.length <= 10 * 1024 * 1024);
    await api(`/sessions/${id}/navigate`, {
      url: "https://httpbin.org/response-headers?Content-Disposition=attachment%3Bfilename%3Dnotes.txt&Content-Type=text%2Fplain",
    });
    let failures: { code: string; name: string }[] = [];
    for (let attempt = 0; attempt < 30; attempt++) {
      failures = (await (await api(`/sessions/${id}/downloads`)).json()).failures;
      if (failures.length) break;
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    assert.equal(failures[0]?.code, "UNSUPPORTED_DOWNLOAD");
    assert.equal(failures[0]?.name, "notes.txt");
    await api(`/sessions/${id}/close`, {});
    if (container) {
      // Seed localStorage through Chromium itself while the worker has closed the
      // profile. This test-only process never exposes an evaluate endpoint.
      execFileSync(
        "docker",
        [
          "exec",
          container,
          "node",
          "--input-type=module",
          "-e",
          `
        import { chromium } from 'playwright';
        const context = await chromium.launchPersistentContext('/data/${id}/profile', {headless:true});
        const page = await context.newPage();
        await page.goto('https://example.com');
        await page.evaluate(() => localStorage.setItem('studio-profile-test', 'retained'));
        await context.close();
      `,
        ],
        { stdio: "pipe", timeout: 45_000 },
      );
    }
    if (container) {
      execFileSync("docker", ["restart", container], { stdio: "pipe", timeout: 45_000 });
      // Docker may allocate a new ephemeral host port on container restart.
      base = `http://${execFileSync("docker", ["port", container, "8790"], { encoding: "utf8" }).trim()}`;
      for (let attempt = 0; attempt < 30; attempt++) {
        if (
          await fetch(`${base}/health`)
            .then((response) => response.ok)
            .catch(() => false)
        )
          break;
        await new Promise((resolve) => setTimeout(resolve, 200));
      }
      const restored = await (await api("/sessions")).json();
      assert(
        restored.some(
          (session: { id: string; status: string }) =>
            session.id === id && session.status === "closed",
        ),
      );
      const savedDownloads = await (await api(`/sessions/${id}/downloads`)).json();
      assert.equal(savedDownloads.downloads.length, 1, "PDF survives worker restart");
      assert.equal(
        savedDownloads.failures[0]?.code,
        "UNSUPPORTED_DOWNLOAD",
        "rejected download survives restart",
      );
    }
    const reopened = await (await api("/sessions", { id, url: "https://example.com" })).json();
    assert.equal(reopened.title, "Example Domain");
    await api(`/sessions/${id}/close`, {});
    if (container) {
      const state = JSON.parse(
        execFileSync(
          "docker",
          [
            "exec",
            container,
            "node",
            "-e",
            `
        const fs = require('node:fs');
        const state = JSON.parse(fs.readFileSync('/data/${id}/storage.json', 'utf8'));
        console.log(JSON.stringify(state.origins.find(x => x.origin === 'https://example.com')?.localStorage));
      `,
          ],
          { encoding: "utf8", timeout: 10_000 },
        ),
      );
      assert(
        state.some(
          (item: { name: string; value: string }) =>
            item.name === "studio-profile-test" && item.value === "retained",
        ),
      );
    }
  } finally {
    await api(`/sessions/${id}/close`, {}).catch(() => {});
  }
});
