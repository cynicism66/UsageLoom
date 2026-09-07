import assert from "node:assert/strict";
import { mkdir, mkdtemp, realpath, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import test from "node:test";
import { scanUsage, buildLocalUsage } from "../src/providers/codex/usage.mjs";

const meta = { type: "session_meta", payload: { id: "synthetic-correction" } };
const model = { type: "turn_context", payload: { model: "gpt-5.4" } };
const count = (input, output, cached) => ({ timestamp: "2026-09-05T01:00:00Z", type: "event_msg", payload: { type: "token_count", info: { total_token_usage: {
  input_tokens: input, output_tokens: output, ...(cached === undefined ? {} : { cached_input_tokens: cached }),
} } } });

async function withFixture(action) {
  const parent = path.join(os.tmpdir(), "UsageLoom-js-tests"); await mkdir(parent, { recursive: true });
  const root = await mkdtemp(path.join(parent, "classification-"));
  await mkdir(path.join(root, "sessions"));
  const write = (name, rows) => writeFile(path.join(root, "sessions", name + ".jsonl"), rows.map(row => JSON.stringify(row)).join("\n"));
  try { await action(root, write); }
  finally {
    const resolved = await realpath(root);
    assert.equal(path.dirname(resolved), await realpath(parent));
    assert(path.basename(resolved).startsWith("classification-"));
    await rm(resolved, { recursive: true, force: true });
  }
}

test("same-total correction updates categories without adding consumption", async () => withFixture(async (home, write) => {
  await write("a", [meta, model, count(160, 40, 20), count(160, 40, 30), count(160, 40, 30)]);
  const snapshot = await scanUsage({ codexHome: home });
  assert.equal(snapshot.events.length, 1);
  assert.equal(snapshot.events[0].usage.cachedInputTokens, 30);
  assert.equal(snapshot.events[0].usage.totalTokens, 200);
  assert.equal(snapshot.warnings.classificationCorrections, 1);
  assert.equal(snapshot.warnings.duplicateRowsSkipped, 1);
}));
test("classification correction in a resumed file reaches prior event", async () => withFixture(async (home, write) => {
  await write("a", [meta, model, count(80, 20, 10)]);
  await write("b", [meta, model, count(80, 20, 15), count(120, 30)]);
  const snapshot = await scanUsage({ codexHome: home });
  const report = buildLocalUsage(snapshot, { now: new Date("2026-09-05T02:00:00Z") });
  assert.equal(report.totals.totalTokens, 150);
  assert.equal(report.totals.cachedInputTokens, 15);
  assert.equal(snapshot.warnings.classificationCorrections, 1);
}));
test("ambiguous correction reports a gap instead of inventing attribution", async () => withFixture(async (home, write) => {
  await write("a", [meta, model, count(80, 20, 10), count(160, 40, 20), count(160, 40, 30)]);
  const snapshot = await scanUsage({ codexHome: home });
  assert.equal(snapshot.warnings.unresolvedClassificationCorrections, 1);
  assert.equal(snapshot.events.reduce((sum, e) => sum + e.usage.totalTokens, 0), 200);
}));
