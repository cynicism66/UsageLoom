import { createReadStream } from "node:fs";
import { opendir, readFile, stat } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import { estimateCost } from "./pricing.mjs";

// Provider-specific parsing lives here; callers receive normalized usage fields.
const EMPTY_USAGE = Object.freeze({
  inputTokens: 0,
  cachedInputTokens: 0,
  cacheWriteInputTokens: 0,
  outputTokens: 0,
  reasoningOutputTokens: 0,
  totalTokens: 0,
});

let scanCache = null;

export function resolveCodexHome(env = process.env) {
  if (env.CODEX_HOME) return path.resolve(env.CODEX_HOME);
  const profile = env.USERPROFILE || os.homedir();
  return path.join(profile, ".codex");
}

function toNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : 0;
}

export function normalizeUsage(raw = {}) {
  const inputTokens = toNumber(raw.input_tokens ?? raw.inputTokens);
  const cachedInputTokens = toNumber(raw.cached_input_tokens ?? raw.cachedInputTokens);
  const cacheWriteInputTokens = toNumber(
    raw.cache_write_input_tokens ?? raw.cacheWriteInputTokens,
  );
  const outputTokens = toNumber(raw.output_tokens ?? raw.outputTokens);
  const reasoningOutputTokens = toNumber(
    raw.reasoning_output_tokens ?? raw.reasoningOutputTokens,
  );
  return {
    inputTokens,
    cachedInputTokens,
    cacheWriteInputTokens,
    outputTokens,
    reasoningOutputTokens,
    totalTokens: inputTokens + outputTokens,
  };
}

function addUsage(target, value) {
  target.inputTokens += value.inputTokens;
  target.cachedInputTokens += value.cachedInputTokens;
  target.cacheWriteInputTokens += value.cacheWriteInputTokens;
  target.outputTokens += value.outputTokens;
  target.reasoningOutputTokens += value.reasoningOutputTokens;
  target.totalTokens += value.totalTokens;
  return target;
}

function subtractUsage(current, previous) {
  const result = {};
  for (const key of Object.keys(EMPTY_USAGE)) {
    result[key] = Math.max(0, (current[key] || 0) - (previous[key] || 0));
  }
  result.totalTokens = result.inputTokens + result.outputTokens;
  return result;
}

function usageKey(usage) {
  return [
    usage.inputTokens,
    usage.cachedInputTokens,
    usage.cacheWriteInputTokens,
    usage.outputTokens,
    usage.reasoningOutputTokens,
  ].join(":");
}

function localDate(isoTimestamp) {
  const date = new Date(isoTimestamp);
  if (Number.isNaN(date.getTime())) return "unknown";
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function classifyAgent(meta = {}) {
  const source = meta.source;
  const text = JSON.stringify({ source, threadSource: meta.thread_source }).toLowerCase();
  if (text.includes("guardian")) return "guardian";
  if (text.includes("memory")) return "memory";
  if (source && typeof source === "object" && source.subagent) return "subagent";
  return "main";
}

function sessionIdFromFilename(filePath) {
  const match = path.basename(filePath).match(
    /([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$/i,
  );
  return match?.[1] || path.basename(filePath, ".jsonl");
}

async function listJsonlFiles(root) {
  const files = [];
  async function visit(directory) {
    let handle;
    try {
      handle = await opendir(directory);
    } catch (error) {
      if (error?.code === "ENOENT") return;
      throw error;
    }
    for await (const entry of handle) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (entry.isFile() && entry.name.endsWith(".jsonl")) files.push(fullPath);
    }
  }
  await visit(root);
  return files;
}

async function loadTitles(codexHome) {
  const titles = new Map();
  try {
    const body = await readFile(path.join(codexHome, "session_index.jsonl"), "utf8");
    for (const line of body.split(/\r?\n/)) {
      if (!line) continue;
      try {
        const row = JSON.parse(line);
        if (row.id && row.thread_name) titles.set(String(row.id), String(row.thread_name));
      } catch {
        // A damaged title row must not prevent token accounting.
      }
    }
  } catch {
    // Titles are optional metadata.
  }
  return titles;
}

async function scanFile(filePath, titles, warnings, sessionHighWater) {
  const session = {
    id: sessionIdFromFilename(filePath),
    title: null,
    cwd: null,
    project: "(unknown)",
    agent: "main",
    startedAt: null,
    latestAt: null,
    events: [],
    quotaSnapshots: [],
  };
  let currentModel = "unknown";
  let ownerStartedMs = null;
  let previousTotal = { ...EMPTY_USAGE };
  let potentialFork = false;
  let replayBuffer = null;
  let ownerMetadataSeen = false;
  const seen = new Set();

  const recordQuota = (row) => {
    const rateLimits = row.payload?.rate_limits;
    const timestampMs = Date.parse(row.timestamp);
    if (!rateLimits || !Number.isFinite(timestampMs)) return;
    session.quotaSnapshots.push({
      timestamp: row.timestamp,
      timestampMs,
      rateLimits,
    });
  };

  const accountTokenRow = (row, model) => {
    const timestamp = row.timestamp;
    const eventMs = Date.parse(timestamp);
    if (!Number.isFinite(eventMs)) {
      warnings.invalidTimestamps += 1;
      return;
    }
    if (ownerStartedMs !== null && eventMs + 1000 < ownerStartedMs) {
      warnings.replayRowsSkipped += 1;
      return;
    }

    const info = row.payload.info || {};
    let usage;
    let dedupeKey;
    if (info.total_token_usage) {
      const currentTotal = normalizeUsage(info.total_token_usage);
      if (currentTotal.totalTokens < previousTotal.totalTokens) {
        warnings.cumulativeResets += 1;
        previousTotal = { ...EMPTY_USAGE };
      } else if (currentTotal.totalTokens === previousTotal.totalTokens) {
        warnings.duplicateRowsSkipped += 1;
        previousTotal = currentTotal;
        return;
      }
      usage = subtractUsage(currentTotal, previousTotal);
      previousTotal = currentTotal;
      dedupeKey = `total|${usageKey(currentTotal)}`;
    } else if (info.last_token_usage) {
      usage = normalizeUsage(info.last_token_usage);
      dedupeKey = `last|${row.ordinal ?? ""}|${timestamp}|${usageKey(usage)}|${model}`;
      warnings.lastUsageFallbackRows += 1;
    } else {
      warnings.rowsWithoutUsage += 1;
      return;
    }
    if (usage.totalTokens <= 0) return;
    if (seen.has(dedupeKey)) {
      warnings.duplicateRowsSkipped += 1;
      return;
    }
    seen.add(dedupeKey);
    session.latestAt = timestamp;
    session.events.push({
      timestamp,
      timestampMs: eventMs,
      date: localDate(timestamp),
      model,
      usage,
      rateLimits: row.payload.rate_limits || null,
    });
  };

  try {
    const input = createReadStream(filePath, { encoding: "utf8" });
    const lines = readline.createInterface({ input, crlfDelay: Infinity });
    for await (const line of lines) {
      if (!line.includes('"token_count"') && !line.includes('"session_meta"') && !line.includes('"turn_context"')) {
        continue;
      }
      let row;
      try {
        row = JSON.parse(line);
      } catch {
        warnings.malformedLines += 1;
        continue;
      }

      if (row.type === "session_meta" && !ownerMetadataSeen) {
        const meta = row.payload || {};
        ownerMetadataSeen = true;
        session.id = String(meta.id || session.id);
        session.cwd = typeof meta.cwd === "string" ? meta.cwd : null;
        session.project = session.cwd ? path.basename(session.cwd) || session.cwd : "(unknown)";
        session.agent = classifyAgent(meta);
        session.startedAt = meta.timestamp || row.timestamp || null;
        ownerStartedMs = Date.parse(session.startedAt);
        if (!Number.isFinite(ownerStartedMs)) ownerStartedMs = null;
        session.title = titles.get(session.id) || null;
        previousTotal = sessionHighWater.get(session.id) || { ...EMPTY_USAGE };
        potentialFork = Boolean(
          meta.forked_from_id ||
          meta.parent_thread_id ||
          (meta.session_id && meta.id && meta.session_id !== meta.id),
        );
        if (potentialFork) replayBuffer = [];
        continue;
      }

      if (row.type === "session_meta" && ownerMetadataSeen && replayBuffer) {
        let baseline = previousTotal;
        for (const buffered of replayBuffer) {
          const total = buffered.row.payload?.info?.total_token_usage;
          if (!total) continue;
          const normalized = normalizeUsage(total);
          if (normalized.totalTokens >= baseline.totalTokens) baseline = normalized;
        }
        previousTotal = baseline;
        warnings.forkPreludeRowsSkipped += replayBuffer.length;
        replayBuffer = null;
        continue;
      }

      if (row.type === "turn_context") {
        const context = row.payload || {};
        if (typeof context.model === "string" && context.model) currentModel = context.model;
        if (!session.cwd && typeof context.cwd === "string") {
          session.cwd = context.cwd;
          session.project = path.basename(context.cwd) || context.cwd;
        }
        continue;
      }

      if (row.type !== "event_msg" || row.payload?.type !== "token_count") continue;
      recordQuota(row);
      if (replayBuffer) replayBuffer.push({ row, model: currentModel });
      else accountTokenRow(row, currentModel);
    }
  } catch (error) {
    warnings.unreadableFiles.push({ file: path.basename(filePath), error: error.message });
  }

  if (replayBuffer) {
    for (const buffered of replayBuffer) accountTokenRow(buffered.row, buffered.model);
  }

  const storedHighWater = sessionHighWater.get(session.id);
  if (!storedHighWater || previousTotal.totalTokens >= storedHighWater.totalTokens) {
    sessionHighWater.set(session.id, previousTotal);
  }

  if (!session.title) session.title = titles.get(session.id) || null;
  return session;
}

export async function scanUsage({
  codexHome = resolveCodexHome(),
  includeArchived = true,
  includeTitles = false,
} = {}) {
  const warnings = {
    malformedLines: 0,
    invalidTimestamps: 0,
    replayRowsSkipped: 0,
    forkPreludeRowsSkipped: 0,
    duplicateRowsSkipped: 0,
    cumulativeResets: 0,
    lastUsageFallbackRows: 0,
    rowsWithoutUsage: 0,
    unreadableFiles: [],
  };
  const titles = includeTitles ? await loadTitles(codexHome) : new Map();
  const roots = [path.join(codexHome, "sessions")];
  if (includeArchived) roots.push(path.join(codexHome, "archived_sessions"));
  const nested = await Promise.all(roots.map(listJsonlFiles));
  const files = nested.flat().sort((a, b) => a.localeCompare(b));
  const sessions = [];
  const sessionHighWater = new Map();
  for (const file of files) {
    sessions.push(await scanFile(file, titles, warnings, sessionHighWater));
  }
  const events = sessions.flatMap((session) =>
    session.events.map((event) => ({
      ...event,
      sessionId: session.id,
      title: session.title,
      project: session.project,
      cwd: session.cwd,
      agent: session.agent,
    })),
  );
  const quotaSnapshots = sessions.flatMap((session) => session.quotaSnapshots);
  return {
    codexHome,
    scannedAt: new Date().toISOString(),
    fileCount: files.length,
    sessionCount: sessions.filter((session) => session.events.length > 0).length,
    sessions,
    events,
    quotaSnapshots,
    titleIndexRead: includeTitles,
    warnings,
  };
}

export async function getUsageSnapshot(options = {}) {
  const codexHome = options.codexHome || resolveCodexHome();
  const includeArchived = options.includeArchived !== false;
  const includeTitles = options.includeTitles === true;
  const cacheKey = `${codexHome}|${includeArchived}|${includeTitles}`;
  const maxAgeMs = options.maxAgeMs ?? 30_000;
  if (
    !options.force &&
    scanCache?.key === cacheKey &&
    Date.now() - scanCache.createdAt < maxAgeMs
  ) {
    return scanCache.value;
  }
  const value = await scanUsage({ codexHome, includeArchived, includeTitles });
  scanCache = { key: cacheKey, createdAt: Date.now(), value };
  return value;
}

function aggregateRows(events, keySelector) {
  const rows = new Map();
  for (const event of events) {
    const key = keySelector(event) || "(unknown)";
    if (!rows.has(key)) rows.set(key, { key, usage: { ...EMPTY_USAGE }, costUsd: 0, pricedTokens: 0 });
    const row = rows.get(key);
    addUsage(row.usage, event.usage);
    const estimate = estimateCost(event.model, event.usage);
    if (estimate.costUsd !== null) {
      row.costUsd += estimate.costUsd;
      row.pricedTokens += estimate.pricedTokens;
    }
  }
  return [...rows.values()];
}

function finishRows(rows, label, topN) {
  return rows
    .sort((a, b) => b.usage.totalTokens - a.usage.totalTokens)
    .slice(0, topN)
    .map((row) => ({
      [label]: row.key,
      ...row.usage,
      apiEquivalentCostUsd: Number(row.costUsd.toFixed(6)),
      pricingCoveragePercent: row.usage.totalTokens
        ? Number(((row.pricedTokens / row.usage.totalTokens) * 100).toFixed(2))
        : 0,
    }));
}

export function buildLocalUsage(
  snapshot,
  { sinceDays = 7, topN = 10, now = new Date() } = {},
) {
  const cutoffMs = now.getTime() - Math.max(1, sinceDays) * 86_400_000;
  const events = snapshot.events.filter((event) => event.timestampMs >= cutoffMs);
  const totals = { ...EMPTY_USAGE };
  let costUsd = 0;
  let pricedTokens = 0;
  const unknownModels = new Set();
  for (const event of events) {
    addUsage(totals, event.usage);
    const estimate = estimateCost(event.model, event.usage);
    if (estimate.costUsd === null) unknownModels.add(event.model);
    else {
      costUsd += estimate.costUsd;
      pricedTokens += estimate.pricedTokens;
    }
  }
  const uncachedInputTokens = Math.max(
    0,
    totals.inputTokens - totals.cachedInputTokens - totals.cacheWriteInputTokens,
  );
  return {
    scope: "current_machine_local_jsonl",
    rollingWindowDays: Math.max(1, sinceDays),
    from: new Date(cutoffMs).toISOString(),
    to: now.toISOString(),
    filesScanned: snapshot.fileCount,
    sessionsIncluded: new Set(events.map((event) => event.sessionId)).size,
    totals: {
      ...totals,
      uncachedInputTokens,
      cacheHitPercent: totals.inputTokens
        ? Number(((totals.cachedInputTokens / totals.inputTokens) * 100).toFixed(2))
        : 0,
      apiEquivalentCostUsd: Number(costUsd.toFixed(6)),
      pricingCoveragePercent: totals.totalTokens
        ? Number(((pricedTokens / totals.totalTokens) * 100).toFixed(2))
        : 0,
    },
    byModel: finishRows(aggregateRows(events, (event) => event.model), "model", topN),
    byProject: finishRows(aggregateRows(events, (event) => event.project), "project", topN),
    byAgent: finishRows(aggregateRows(events, (event) => event.agent), "agent", topN),
    daily: finishRows(aggregateRows(events, (event) => event.date), "date", 10_000).sort((a, b) =>
      a.date.localeCompare(b.date),
    ),
    unknownPricingModels: [...unknownModels].sort(),
    warnings: snapshot.warnings,
    disclaimer:
      "Token values are local-machine accounting. Cost is an API-equivalent estimate, not a ChatGPT/Codex subscription bill or quota measurement.",
  };
}

export function buildRecentSessions(
  snapshot,
  { sinceDays = 7, limit = 10, includeTitles = false, query = "", now = new Date() } = {},
) {
  const cutoffMs = now.getTime() - Math.max(1, sinceDays) * 86_400_000;
  const needle = String(query).trim().toLowerCase();
  const result = [];
  for (const session of snapshot.sessions) {
    const events = session.events.filter((event) => event.timestampMs >= cutoffMs);
    if (!events.length) continue;
    if (
      needle &&
      ![session.project, session.title, session.id].some((value) =>
        String(value || "").toLowerCase().includes(needle),
      )
    ) continue;
    const usage = { ...EMPTY_USAGE };
    let costUsd = 0;
    for (const event of events) {
      addUsage(usage, event.usage);
      const estimate = estimateCost(event.model, event.usage);
      if (estimate.costUsd !== null) costUsd += estimate.costUsd;
    }
    result.push({
      sessionId: session.id,
      ...(includeTitles ? { title: session.title, project: session.project } : {}),
      agent: session.agent,
      startedAt: session.startedAt,
      latestAt: session.latestAt,
      models: [...new Set(events.map((event) => event.model))],
      ...usage,
      apiEquivalentCostUsd: Number(costUsd.toFixed(6)),
    });
  }
  return {
    scope: "current_machine_local_jsonl",
    rollingWindowDays: Math.max(1, sinceDays),
    metadataIncluded: Boolean(includeTitles),
    sessions: result
      .sort((a, b) => b.totalTokens - a.totalTokens)
      .slice(0, Math.max(1, Math.min(100, limit))),
    disclaimer: includeTitles
      ? "Task titles and project directory names are included because metadata was explicitly requested."
      : "Task titles and project paths were omitted for privacy.",
  };
}

export async function diagnoseSources(snapshot) {
  let homeStat = null;
  try {
    homeStat = await stat(snapshot.codexHome);
  } catch {
    // Reported below.
  }
  const latest = snapshot.events.reduce(
    (current, event) => (!current || event.timestampMs > current.timestampMs ? event : current),
    null,
  );
  return {
    codexHome: snapshot.codexHome,
    codexHomeExists: Boolean(homeStat?.isDirectory()),
    filesScanned: snapshot.fileCount,
    sessionsWithUsage: snapshot.sessionCount,
    latestUsageTimestamp: latest?.timestamp || null,
    latestModel: latest?.model || null,
    warnings: snapshot.warnings,
    privacy: {
      readsAuthJson: false,
      readsConversationContent: false,
      readsTaskTitleIndex: Boolean(snapshot.titleIndexRead),
      startsHttpServer: false,
      runtimeTelemetry: false,
    },
  };
}
