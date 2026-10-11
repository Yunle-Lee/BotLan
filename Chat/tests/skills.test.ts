import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { createStore } from "../apps/server/src/db.ts";
import {
  assertPublicHttpsUrl,
  parseSkillMarkdown,
  SkillService,
  skillsInstructions,
  skillTools,
} from "../apps/server/src/skills.ts";
import { builtinSkills, nvidiaSkills } from "../apps/server/src/skills-catalog.ts";

const upstream = `---
name: rag-blueprint
version: "2.6.0"
description: >-
  NVIDIA RAG Blueprint, deploy and
  troubleshoot.
license: Apache-2.0
---

# RAG Blueprint

Run the blueprint.`;

async function withService(
  fn: (service: SkillService, fetched: string[]) => Promise<void>,
  market?: string,
) {
  const dir = await mkdtemp(join(tmpdir(), "botlan-skills-"));
  const db = await createStore({ dataDir: dir });
  const fetched: string[] = [];
  const service = new SkillService(db, {
    marketUrl: market ? "https://market.example/api/skills" : false,
    fetcher: async (url) => {
      fetched.push(url);
      if (url.startsWith("https://market.example")) return market ?? "";
      return upstream;
    },
  });
  try {
    await fn(service, fetched);
  } finally {
    await db.close();
    await rm(dir, { recursive: true, force: true });
  }
}

test("catalog ids are unique and builtin skills carry instructions", () => {
  const ids = [...builtinSkills, ...nvidiaSkills].map((skill) => skill.id);
  assert.equal(new Set(ids).size, ids.length);
  for (const skill of builtinSkills) assert.ok(skill.instructions.length > 200, skill.id);
  for (const skill of nvidiaSkills)
    assert.match(
      skill.source.url ?? "",
      /^https:\/\/raw\.githubusercontent\.com\/NVIDIA\/skills\//,
    );
  assert.ok(builtinSkills.some((skill) => skill.category.includes("StepFun")));
});

test("parses Agent Skills frontmatter including folded scalars", () => {
  const parsed = parseSkillMarkdown(upstream);
  assert.equal(parsed.name, "rag-blueprint");
  assert.equal(parsed.version, "2.6.0");
  assert.equal(parsed.description, "NVIDIA RAG Blueprint, deploy and troubleshoot.");
  assert.match(parsed.body, /^# RAG Blueprint/);
  assert.equal(parseSkillMarkdown("no frontmatter").body, "no frontmatter");
});

test("rejects non-public or non-https skill URLs", () => {
  for (const url of [
    "http://example.com/SKILL.md",
    "https://localhost/SKILL.md",
    "https://127.0.0.1/SKILL.md",
    "https://192.168.1.4/SKILL.md",
    "https://[::1]/SKILL.md",
    "https://user:pw@example.com/SKILL.md",
  ])
    assert.throws(() => assertPublicHttpsUrl(url), url);
  assert.equal(assertPublicHttpsUrl("https://github.com/x").hostname, "github.com");
});

test("installs per Bot, toggles, and exposes skills to the agent", async () => {
  await withService(async (service) => {
    await service.install("island:a", "spark-model-planner");
    const a = await service.list("island:a");
    const b = await service.list("island:b");
    assert.ok(a.skills.find((skill) => skill.id === "spark-model-planner")?.installed);
    assert.ok(!b.skills.find((skill) => skill.id === "spark-model-planner")?.installed);
    // Listings never ship the full instructions to the page.
    assert.ok(a.skills.every((skill) => !("instructions" in skill)));

    const prompt = skillsInstructions(await service.enabled("island:a"));
    assert.match(prompt, /spark-model-planner/);
    const [loadSkill] = skillTools(service, "island:a");
    const loaded = (await loadSkill.execute?.({ id: "spark-model-planner" })) as {
      instructions?: string;
    };
    assert.match(loaded.instructions ?? "", /DGX Spark/);

    await service.setEnabled("island:a", "spark-model-planner", false);
    assert.equal((await service.enabled("island:a")).length, 0);
    const denied = (await loadSkill.execute?.({ id: "spark-model-planner" })) as {
      error?: string;
    };
    assert.match(denied.error ?? "", /disabled/);

    await service.uninstall("island:a", "spark-model-planner");
    assert.equal((await service.installed("island:a")).length, 0);
  });
});

test("NVIDIA skills are fetched from upstream at install time", async () => {
  await withService(async (service, fetched) => {
    const record = await service.install("owner", "nvidia-rag-blueprint");
    assert.ok(fetched.some((url) => url.endsWith("/skills/rag-blueprint/SKILL.md")));
    assert.equal(record.manifest.version, "2.6.0");
    assert.match(record.manifest.instructions, /Run the blueprint/);
    assert.equal(record.digest.length, 64);
  });
});

test("market entries are validated and rendered into instructions", async () => {
  const market = JSON.stringify({
    data: [
      {
        id: "demo",
        name: "Demo",
        systemPrompt: "Use demo.",
        commands: [{ label: "Run", command: "demo --go" }],
      },
      { id: "../bad", name: "Bad" },
      { name: "missing id" },
    ],
  });
  await withService(async (service) => {
    const { skills } = await service.list("owner");
    const marketSkills = skills.filter((skill) => skill.source.kind === "market");
    assert.deepEqual(
      marketSkills.map((skill) => skill.id),
      ["market-demo"],
    );
    const record = await service.install("owner", "market-demo");
    assert.match(record.manifest.instructions, /Use demo\./);
    assert.match(record.manifest.instructions, /demo --go/);
  }, market);
});

test("imports a SKILL.md URL and converts GitHub blob links", async () => {
  await withService(async (service, fetched) => {
    const record = await service.importUrl(
      "owner",
      "https://github.com/NVIDIA/skills/blob/main/skills/rag-blueprint/SKILL.md",
    );
    assert.equal(
      fetched.at(-1),
      "https://raw.githubusercontent.com/NVIDIA/skills/main/skills/rag-blueprint/SKILL.md",
    );
    assert.equal(record.id, "url-rag-blueprint");
    const { skills } = await service.list("owner");
    assert.ok(skills.some((skill) => skill.id === "url-rag-blueprint" && skill.installed));
  });
});
