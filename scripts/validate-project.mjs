import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const readJson = async (relativePath) => JSON.parse(
  await readFile(path.join(root, relativePath), "utf8"),
);
const mustExist = async (relativePath) => {
  await access(path.join(root, relativePath));
};

const packageJson = await readJson("legacy/codex-plugin/package.json");
const manifest = await readJson("legacy/codex-plugin/.codex-plugin/plugin.json");
const mcp = await readJson("legacy/codex-plugin/.mcp.json");

assert.equal(packageJson.name, "usage-loom");
assert.equal(manifest.name, "usage-loom");
assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
assert.equal(manifest.interface.displayName, "UsageLoom");
assert.equal(manifest.interface.brandColor, "#7C5CFC");
assert.ok(mcp.mcpServers.usage_loom);
assert.equal(mcp.mcpServers.usage_loom.cwd, ".");

for (const relativePath of [
  "legacy/codex-plugin/.codex-plugin/plugin.json",
  "legacy/codex-plugin/.mcp.json",
  "LICENSE",
  "LICENSE.zh-CN.md",
  "README.md",
  "SECURITY.md",
  "CONTRIBUTING.md",
  "CHANGELOG.md",
  "docs/ARCHITECTURE.md",
  "docs/PRIVACY.md",
  "docs/PROJECT_ORIGINS.md",
  "docs/RELEASING.md",
  "legacy/codex-plugin/server/index.mjs",
  "legacy/codex-plugin/src/providers/codex/usage.mjs",
  "legacy/codex-plugin/src/providers/codex/quota.mjs",
  "legacy/codex-plugin/src/providers/codex/pricing.mjs",
  "legacy/codex-plugin/assets/usage-dashboard.html",
]) await mustExist(relativePath);

const files = [
  "README.md",
  "LICENSE",
  "LICENSE.zh-CN.md",
  "THIRD_PARTY_NOTICES.md",
  "CHANGELOG.md",
  "CONTRIBUTING.md",
  "SECURITY.md",
  "docs/ARCHITECTURE.md",
  "docs/PRIVACY.md",
  "docs/PROJECT_ORIGINS.md",
  "docs/RELEASING.md",
  "legacy/codex-plugin/.codex-plugin/plugin.json",
  "legacy/codex-plugin/.mcp.json",
  "legacy/codex-plugin/server/index.mjs",
  "legacy/codex-plugin/src/providers/codex/usage.mjs",
  "legacy/codex-plugin/src/providers/codex/quota.mjs",
  "legacy/codex-plugin/src/providers/codex/pricing.mjs",
  "legacy/codex-plugin/assets/usage-dashboard.html",
  "legacy/codex-plugin/skills/usage-monitor/SKILL.md",
  "scripts/validate-project.mjs",
  "scripts/validate-local-docs.mjs",
  "legacy/codex-plugin/scripts/package-plugin.ps1",
  "legacy/codex-plugin/scripts/smoke-test.mjs",
  "legacy/codex-plugin/package.json",
  "legacy/codex-plugin/package-lock.json",
  ".github/workflows/test.yml",
];
const texts = await Promise.all(files.map((relativePath) => readFile(path.join(root, relativePath), "utf8")));
const combined = texts.join("\n");

// 将历史品牌检查限定到产品文件，避免检查器自己的规则命中自身。
const productText = texts.filter((_, index) => !files[index].startsWith("scripts/")).join("\n");
assert.doesNotMatch(productText, /codex-usage-plus|Codex Usage Plus|codex_usage_plus|#10a37f|Local developer/);
// 检查真实用户目录与本机项目路径，不在规则里写入维护者的个人路径。
assert.doesNotMatch(combined, /\b[A-Z]:[\\/]+Users[\\/]+[^\\/\s"'<>]+/i);
assert.doesNotMatch(combined, /\/(?:Users|home)\/[A-Za-z0-9_.-]+/);
assert.doesNotMatch(combined, /\b[A-Z]:[\\/]+(?:code|projects|repos)[\\/]+[A-Za-z0-9_.-]+/i);
assert.doesNotMatch(combined, /\b(?:sk|sess|key|token)[-_][A-Za-z0-9_-]{20,}\b/i);
assert.doesNotMatch(combined, /Bearer\s+[A-Za-z0-9._~+/=-]{20,}/i);
assert.equal(combined.includes("\uFFFD"), false, "files must not contain Unicode replacement characters");
assert.match(combined, /https:\/\/github\.com\/zJay26\/codex-usage/);
assert.match(combined, /https:\/\/github\.com\/Nirlep5252\/CodexBarWindows/);
assert.match(combined, /独立实现/);

// 公开文档不得依赖只在维护者本机存在的材料。
const privateDocuments = new Set([
  "docs/DEVELOPMENT_PLAN.md",
  "docs/DEVELOPMENT_LOG.md",
]);
for (const [index, relativePath] of files.entries()) {
  if (!relativePath.endsWith(".md")) continue;
  for (const match of texts[index].matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
    const target = match[1];
    if (/^(?:[a-z][a-z0-9+.-]*:|#)/i.test(target)) continue;
    const resolved = path.resolve(root, path.dirname(relativePath), target.split("#")[0]);
    const relativeTarget = path.relative(root, resolved).split(path.sep).join("/");
    assert.ok(!privateDocuments.has(relativeTarget) && !relativeTarget.startsWith("docs/internal/"),
      `公开文档不能依赖本地内部材料：${relativePath}`);
    await access(resolved);
  }
}

console.log(`项目自检通过：${root}`);
