import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const documents = ["docs/DEVELOPMENT_PLAN.md", "docs/DEVELOPMENT_LOG.md"];
const contents = new Map();

for (const document of documents) {
  let bytes;
  try {
    bytes = await readFile(path.join(root, document));
  } catch (error) {
    if (error.code === "ENOENT") continue;
    throw error;
  }
  const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  assert.ok(!text.includes("\uFFFD"), `${document} 包含替换字符`);
  assert.ok(text.endsWith("\n"), `${document} 缺少末尾换行`);
  assert.doesNotMatch(text, /\b(?:sk|sess|key|token)[-_][A-Za-z0-9_-]{20,}\b/i,
    `${document} 包含疑似凭据`);
  assert.doesNotMatch(text, /Bearer\s+[A-Za-z0-9._~+/=-]{20,}/i,
    `${document} 包含疑似访问令牌`);
  let fence = null;
  for (const line of text.split("\n")) {
    const match = line.match(/^(`{3,}|~{3,})/);
    if (!match) continue;
    if (fence === null) fence = match[1];
    else if (match[1][0] === fence[0] && match[1].length >= fence.length) fence = null;
  }
  assert.equal(fence, null, `${document} 代码块未闭合`);
  for (const match of text.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
    if (/^(?:[a-z][a-z0-9+.-]*:|#)/i.test(match[1])) continue;
    await access(path.resolve(root, path.dirname(document), match[1].split("#")[0]));
  }
  contents.set(document, text);
}

if (contents.size === 0) {
  console.log("未发现本地开发文档，跳过本地文档校验；公开构建不需要这些文件。");
} else {
  const plan = contents.get(documents[0]);
  const log = contents.get(documents[1]);
  assert.ok(plan && log, "本地规划与开发记录应完整保留");
  const rows = plan.split("\n").filter((line) => /^\| UL-/.test(line))
    .map((line) => line.split("|").slice(1, -1).map((cell) => cell.trim()));
  const tasks = new Map(rows.map((row) => [row[0], row]));
  assert.ok(tasks.size > 0, "本地规划缺少任务索引");
  assert.equal(tasks.size, rows.length, "任务编号重复");
  const states = new Set(["未开始", "进行中", "待验证", "已完成", "阻塞", "暂缓"]);
  const visiting = new Set();
  const visited = new Set();
  const visit = (id) => {
    assert.ok(tasks.has(id), `依赖任务不存在：${id}`);
    assert.ok(!visiting.has(id), `任务依赖存在循环：${id}`);
    if (visited.has(id)) return;
    visiting.add(id);
    const row = tasks.get(id);
    assert.ok(states.has(row[4]), `任务状态无效：${id}`);
    for (const dependency of row[2].match(/UL-[A-Z0-9]+-[0-9]{3}/g) || []) visit(dependency);
    visiting.delete(id);
    visited.add(id);
  };
  for (const id of tasks.keys()) visit(id);
  for (const match of log.matchAll(/UL-(?:DOC|P[0-9]+)-[0-9]{3}/g)) {
    assert.ok(tasks.has(match[0]), `记录引用未知任务：${match[0]}`);
  }
  const records = [...log.matchAll(/^### (DEV-[0-9]{8}-[0-9]{3})[｜|]/gm)].map((match) => match[1]);
  assert.ok(records.length > 0, "缺少实际开发记录");
  assert.equal(new Set(records).size, records.length, "开发记录编号重复");
  console.log(`本地文档校验通过：${contents.size} 份文档，${tasks.size} 个任务，${records.length} 条记录。`);
}
