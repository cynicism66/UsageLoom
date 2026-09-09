import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import test from "node:test";
import {
  buildLocalUsage,
  buildRecentSessions,
  diagnoseSources,
  scanUsage,
} from "../src/providers/codex/usage.mjs";
import { buildQuotaFallback, normalizeRateLimit } from "../src/providers/codex/quota.mjs";

function row(value) {
  return JSON.stringify(value);
}

test("scanner counts increments, changes models, removes duplicates, and skips fork replay", async () => {
  const testTemp = path.join(os.tmpdir(), "UsageLoom-js-tests");
  await mkdir(testTemp, { recursive: true });
  const root = await mkdtemp(path.join(testTemp, "usage-"));
  try {
    const sessions = path.join(root, "sessions", "2026", "09", "04");
    await mkdir(sessions, { recursive: true });
    const id = "11111111-1111-4111-8111-111111111111";
    const file = path.join(sessions, `rollout-2026-09-04T10-00-00-${id}.jsonl`);
    const usageA = {
      input_tokens: 100,
      cached_input_tokens: 20,
      cache_write_input_tokens: 0,
      output_tokens: 10,
      reasoning_output_tokens: 3,
    };
    await writeFile(file, [
      row({
        timestamp: "2026-09-04T10:00:00.000Z",
        type: "session_meta",
        payload: {
          id,
          timestamp: "2026-09-04T10:00:00.000Z",
          cwd: path.join("C:", "work", "alpha"),
          source: "cli",
        },
      }),
      row({
        timestamp: "2026-09-01T10:00:00.000Z",
        type: "event_msg",
        payload: { type: "token_count", info: { last_token_usage: usageA } },
      }),
      row({
        timestamp: "2026-09-04T10:01:00.000Z",
        type: "turn_context",
        payload: { model: "gpt-5.4-mini" },
      }),
      row({
        timestamp: "2026-09-04T10:02:00.000Z",
        ordinal: 1,
        type: "event_msg",
        payload: {
          type: "token_count",
          info: { last_token_usage: usageA },
          rate_limits: {
            limit_id: "codex",
            primary: { used_percent: 25, window_minutes: 300, resets_at: 2000000000 },
          },
        },
      }),
      row({
        timestamp: "2026-09-04T10:02:00.000Z",
        ordinal: 1,
        type: "event_msg",
        payload: { type: "token_count", info: { last_token_usage: usageA } },
      }),
      row({
        timestamp: "2026-09-04T10:03:00.000Z",
        type: "turn_context",
        payload: { model: "gpt-5.6-terra" },
      }),
      row({
        timestamp: "2026-09-04T10:04:00.000Z",
        ordinal: 2,
        type: "event_msg",
        payload: {
          type: "token_count",
          info: {
            last_token_usage: {
              input_tokens: 50,
              cached_input_tokens: 0,
              output_tokens: 5,
              reasoning_output_tokens: 2,
            },
          },
        },
      }),
    ].join("\n"), "utf8");

    await writeFile(path.join(root, "session_index.jsonl"), row({ id, thread_name: "Synthetic task" }), "utf8");
    const snapshot = await scanUsage({ codexHome: root, includeTitles: true });
    const report = buildLocalUsage(snapshot, {
      sinceDays: 7,
      now: new Date("2026-09-05T00:00:00.000Z"),
    });
    assert.equal(report.totals.inputTokens, 150);
    assert.equal(report.totals.cachedInputTokens, 20);
    assert.equal(report.totals.outputTokens, 15);
    assert.equal(report.totals.reasoningOutputTokens, 5);
    assert.equal(report.totals.totalTokens, 165);
    assert.equal(report.byModel.length, 2);
    assert.equal(snapshot.warnings.duplicateRowsSkipped, 1);
    assert.equal(snapshot.warnings.replayRowsSkipped, 1);

    const privateSessions = buildRecentSessions(snapshot, {
      sinceDays: 7,
      now: new Date("2026-09-05T00:00:00.000Z"),
    });
    assert.equal("title" in privateSessions.sessions[0], false);
    const namedSessions = buildRecentSessions(snapshot, {
      sinceDays: 7,
      includeTitles: true,
      now: new Date("2026-09-05T00:00:00.000Z"),
    });
    assert.equal(namedSessions.sessions[0].title, "Synthetic task");
    assert.equal(namedSessions.sessions[0].project, "alpha");

    const diagnosis = await diagnoseSources(snapshot);
    assert.equal(diagnosis.privacy.readsAuthJson, false);
    assert.equal(diagnosis.privacy.readsConversationContent, false);
    assert.equal(diagnosis.privacy.readsTaskTitleIndex, true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("quota fallback normalizes snake_case local snapshots", () => {
  const normalized = normalizeRateLimit({
    limit_id: "codex",
    plan_type: "plus",
    primary: { used_percent: 40, window_minutes: 300, resets_at: 2000000000 },
    secondary: { used_percent: 10, window_minutes: 10080, resets_at: 2000100000 },
  });
  assert.equal(normalized.primary.usedPercent, 40);
  assert.equal(normalized.primary.remainingPercent, 60);
  assert.equal(normalized.secondary.windowDurationMins, 10080);
  assert.equal(normalized.primary.windowLabel, "5h");
  assert.equal(normalized.secondary.windowLabel, "weekly");
  assert.equal(normalized.primary.resetsAtIso, "2033-05-18T03:33:20.000Z");

  const fallback = buildQuotaFallback({
    events: [{
      timestamp: "2026-09-04T10:00:00.000Z",
      timestampMs: Date.parse("2026-09-04T10:00:00.000Z"),
      rateLimits: {
        limit_id: "codex",
        primary: { used_percent: 40, window_minutes: 300, resets_at: 2000000000 },
      },
    }],
  });
  assert.equal(fallback.isLive, false);
  assert.equal(fallback.rateLimits.limitId, "codex");
  assert.equal(fallback.windows[0].remainingPercent, 60);
  assert.equal(fallback.windows[0].windowLabel, "5h");
  assert.match(fallback.warning, /may be stale/);
});

test("cumulative high-water ignores re-emitted snapshots and fork prelude", async () => {
  const testTemp = path.join(os.tmpdir(), "UsageLoom-js-tests");
  await mkdir(testTemp, { recursive: true });
  const root = await mkdtemp(path.join(testTemp, "high-water-"));
  try {
    const sessions = path.join(root, "sessions", "2026", "09", "04");
    await mkdir(sessions, { recursive: true });
    const mainId = "22222222-2222-4222-8222-222222222222";
    const childId = "33333333-3333-4333-8333-333333333333";
    const parentId = "44444444-4444-4444-8444-444444444444";
    const total100 = {
      input_tokens: 90,
      cached_input_tokens: 20,
      output_tokens: 10,
      total_tokens: 100,
    };
    const total150 = {
      input_tokens: 135,
      cached_input_tokens: 30,
      output_tokens: 15,
      total_tokens: 150,
    };
    await writeFile(path.join(sessions, `rollout-a-${mainId}.jsonl`), [
      row({
        timestamp: "2026-09-04T09:00:00.000Z",
        type: "session_meta",
        payload: { id: mainId, timestamp: "2026-09-04T09:00:00.000Z" },
      }),
      row({
        timestamp: "2026-09-04T09:01:00.000Z",
        type: "turn_context",
        payload: { model: "gpt-5.4-mini" },
      }),
      row({
        timestamp: "2026-09-04T09:02:00.000Z",
        type: "event_msg",
        payload: { type: "token_count", info: { total_token_usage: total100 } },
      }),
      row({
        timestamp: "2026-09-04T09:03:00.000Z",
        type: "event_msg",
        payload: { type: "token_count", info: { total_token_usage: total100 } },
      }),
      row({
        timestamp: "2026-09-04T09:04:00.000Z",
        type: "event_msg",
        payload: { type: "token_count", info: { total_token_usage: total150 } },
      }),
    ].join("\n"), "utf8");

    await writeFile(path.join(sessions, `rollout-b-${childId}.jsonl`), [
      row({
        timestamp: "2026-09-04T10:00:00.000Z",
        type: "session_meta",
        payload: {
          id: childId,
          timestamp: "2026-09-04T10:00:00.000Z",
          forked_from_id: parentId,
        },
      }),
      row({
        timestamp: "2026-09-04T10:00:01.000Z",
        type: "event_msg",
        payload: {
          type: "token_count",
          info: {
            total_token_usage: {
              input_tokens: 900,
              cached_input_tokens: 400,
              output_tokens: 100,
              total_tokens: 1000,
            },
          },
        },
      }),
      row({
        timestamp: "2026-09-04T10:00:02.000Z",
        type: "session_meta",
        payload: { id: parentId, timestamp: "2026-09-04T08:00:00.000Z" },
      }),
      row({
        timestamp: "2026-09-04T10:01:00.000Z",
        type: "turn_context",
        payload: { model: "gpt-5.6-terra" },
      }),
      row({
        timestamp: "2026-09-04T10:02:00.000Z",
        type: "event_msg",
        payload: {
          type: "token_count",
          info: {
            total_token_usage: {
              input_tokens: 990,
              cached_input_tokens: 420,
              output_tokens: 110,
              total_tokens: 1100,
            },
          },
        },
      }),
    ].join("\n"), "utf8");

    const snapshot = await scanUsage({ codexHome: root });
    const report = buildLocalUsage(snapshot, {
      sinceDays: 7,
      now: new Date("2026-09-05T00:00:00.000Z"),
    });
    assert.equal(report.totals.totalTokens, 250);
    assert.equal(snapshot.warnings.duplicateRowsSkipped, 1);
    assert.equal(snapshot.warnings.forkPreludeRowsSkipped, 1);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
