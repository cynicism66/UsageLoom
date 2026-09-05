import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { access, appendFile, cp, mkdir, mkdtemp, readFile, realpath, rm } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const run = promisify(execFile);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const privateNames = new Set(["DEVELOPMENT_PLAN.md", "DEVELOPMENT_LOG.md"]);

test("internal documents are ignored without excluding public documentation", async () => {
  const rules = (await readFile(path.join(root, ".gitignore"), "utf8"))
    .split(/\r?\n/).map((line) => line.trim());
  for (const name of privateNames) assert.ok(rules.includes(`/docs/${name}`));
  assert.ok(rules.includes("/docs/internal/"));
  assert.ok(!rules.some((rule) => ["docs/", "/docs/", "*.md", "**/*.md"].includes(rule)));
});

test("public source validates without internal documents and rejects private documentation links", async () => {
  const tempParent = path.join(root, "test", ".tmp");
  await mkdir(tempParent, { recursive: true });
  const fixture = await mkdtemp(path.join(tempParent, "public-project-"));
  try {
    const publicEntries = [
      ".codex-plugin", ".mcp.json", ".github", ".gitignore", ".gitattributes", ".npmignore",
      "assets", "docs", "server", "skills", "src", "scripts",
      "package.json", "package-lock.json", "README.md", "LICENSE", "LICENSE.zh-CN.md",
      "THIRD_PARTY_NOTICES.md", "SECURITY.md", "CONTRIBUTING.md", "CHANGELOG.md",
    ];
    for (const entry of publicEntries) {
      await cp(path.join(root, entry), path.join(fixture, entry), {
        recursive: true,
        filter: (source) => !privateNames.has(path.basename(source))
          && path.relative(root, source).split(path.sep).join("/") !== "docs/internal",
      });
    }
    for (const name of privateNames) {
      await assert.rejects(access(path.join(fixture, "docs", name)), { code: "ENOENT" });
    }
    const options = { cwd: fixture, windowsHide: true, timeout: 15_000 };
    const publicCheck = await run(process.execPath, ["scripts/validate-project.mjs"], options);
    assert.match(publicCheck.stdout, /项目自检通过/);
    const localCheck = await run(process.execPath, ["scripts/validate-local-docs.mjs"], options);
    assert.match(localCheck.stdout, /跳过本地文档校验/);

    await appendFile(path.join(fixture, "README.md"),
      "\n[仅用于回归测试的内部链接](docs/DEVELOPMENT_PLAN.md)\n", "utf8");
    await assert.rejects(run(process.execPath, ["scripts/validate-project.mjs"], options),
      (error) => /公开文档不能依赖本地内部材料/.test(error.stderr));
  } finally {
    const resolvedParent = await realpath(tempParent);
    const resolvedFixture = await realpath(fixture);
    assert.equal(path.dirname(resolvedFixture), resolvedParent);
    assert.ok(path.basename(resolvedFixture).startsWith("public-project-"));
    await rm(resolvedFixture, { recursive: true, force: true });
  }
});
