import { spawn } from "node:child_process";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";

function toFiniteNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

export function sanitizeDiagnostic(value) {
  const text = String(value || "")
    .replace(/Bearer\s+[^\s"']+/gi, "Bearer <redacted>")
    .replace(/((?:api[_-]?key|access[_-]?token|refresh[_-]?token|cookie)\s*[:=]\s*)[^\s"']+/gi, "$1<redacted>")
    .replace(/[A-Za-z]:\\Users\\[^\s"']+/g, "<user-path>")
    .replace(/\/(?:Users|home)\/[^\s"']+/g, "<user-path>")
    .replace(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, "<email>")
    .trim();
  return text ? text.slice(0, 500) : "Codex app-server query failed";
}

function normalizeEpochSeconds(value) {
  const number = toFiniteNumber(value);
  if (number !== null) return number > 10_000_000_000 ? number / 1000 : number;
  const parsed = Date.parse(String(value || ""));
  return Number.isFinite(parsed) ? parsed / 1000 : null;
}

function windowLabel(windowDurationMins, fallback) {
  if (windowDurationMins === 300) return "5h";
  if (windowDurationMins === 10_080) return "weekly";
  if (windowDurationMins === 1_440) return "daily";
  if (windowDurationMins === 60) return "hourly";
  return windowDurationMins ? `${windowDurationMins}m` : fallback;
}

function camelWindow(window, fallback = "window") {
  if (!window) return null;
  const rawUsed = toFiniteNumber(window.usedPercent ?? window.used_percent);
  const usedPercent = rawUsed === null ? null : Math.max(0, Math.min(100, rawUsed));
  const rawDuration = toFiniteNumber(
    window.windowDurationMins ?? window.window_minutes,
  );
  const rawReset = window.resetsAt ?? window.resets_at ?? null;
  const resetEpoch = normalizeEpochSeconds(rawReset);
  return {
    usedPercent,
    remainingPercent: usedPercent === null ? null : Number((100 - usedPercent).toFixed(2)),
    windowDurationMins: rawDuration,
    windowLabel: windowLabel(rawDuration, fallback),
    resetsAt: rawReset,
    resetsAtIso: resetEpoch === null ? null : new Date(resetEpoch * 1000).toISOString(),
  };
}

export function normalizeRateLimit(raw) {
  if (!raw) return null;
  const primary = camelWindow(raw.primary, "primary");
  const secondary = camelWindow(raw.secondary, "secondary");
  const windows = [
    primary ? { kind: "primary", ...primary } : null,
    secondary ? { kind: "secondary", ...secondary } : null,
  ].filter(Boolean);
  return {
    limitId: raw.limitId ?? raw.limit_id ?? null,
    limitName: raw.limitName ?? raw.limit_name ?? null,
    primary,
    secondary,
    windows,
    credits: raw.credits ?? null,
    planType: raw.planType ?? raw.plan_type ?? null,
    rateLimitReachedType: raw.rateLimitReachedType ?? raw.rate_limit_reached_type ?? null,
  };
}

function collectWindows(primary, byId) {
  const rows = [];
  const seen = new Set();
  const add = (limitId, limit) => {
    if (!limit) return;
    for (const window of limit.windows || []) {
      const key = `${limitId || limit.limitId || "unknown"}:${window.kind}`;
      if (seen.has(key)) continue;
      seen.add(key);
      rows.push({
        limitId: limitId || limit.limitId || null,
        limitName: limit.limitName || null,
        ...window,
      });
    }
  };
  add(primary?.limitId, primary);
  for (const [limitId, limit] of Object.entries(byId || {})) add(limitId, limit);
  return rows;
}

function normalizeLiveResponse(rateResult, usageResult) {
  const byId = {};
  for (const [id, value] of Object.entries(rateResult?.rateLimitsByLimitId || {})) {
    byId[id] = normalizeRateLimit(value);
  }
  const primary = normalizeRateLimit(rateResult?.rateLimits);
  if (primary?.limitId && !byId[primary.limitId]) byId[primary.limitId] = primary;
  return {
    source: "live_codex_app_server",
    isLive: true,
    fetchedAt: new Date().toISOString(),
    rateLimits: primary,
    rateLimitsByLimitId: byId,
    windows: collectWindows(primary, byId),
    rateLimitResetCredits: rateResult?.rateLimitResetCredits ?? null,
    accountUsage: usageResult || null,
  };
}

export function buildQuotaFallback(snapshot) {
  const latestById = new Map();
  for (const event of snapshot.quotaSnapshots || snapshot.events) {
    if (!event.rateLimits) continue;
    const normalized = normalizeRateLimit(event.rateLimits);
    if (!normalized?.limitId) continue;
    const previous = latestById.get(normalized.limitId);
    if (!previous || event.timestampMs > previous.timestampMs) {
      latestById.set(normalized.limitId, {
        timestampMs: event.timestampMs,
        timestamp: event.timestamp,
        value: normalized,
      });
    }
  }
  const rows = [...latestById.values()];
  const preferred =
    rows.find((row) => row.value.limitId === "codex") ||
    rows.sort((a, b) => b.timestampMs - a.timestampMs)[0] ||
    null;
  return {
    source: "latest_local_jsonl_snapshot",
    isLive: false,
    fetchedAt: new Date().toISOString(),
    snapshotAt: preferred?.timestamp || null,
    staleAgeSeconds: preferred
      ? Math.max(0, Math.round((Date.now() - preferred.timestampMs) / 1000))
      : null,
    rateLimits: preferred?.value || null,
    rateLimitsByLimitId: Object.fromEntries(rows.map((row) => [row.value.limitId, row.value])),
    windows: collectWindows(
      preferred?.value || null,
      Object.fromEntries(rows.map((row) => [row.value.limitId, row.value])),
    ),
    rateLimitResetCredits: null,
    accountUsage: null,
    warning:
      "Live app-server query failed. These values are the latest local session snapshot and may be stale or belong to the account active at that time.",
  };
}

export async function queryLiveQuota({ timeoutMs = 15_000, env = process.env } = {}) {
  const command = env.CODEX_CLI_PATH || "codex";
  const codexHome = env.CODEX_HOME || path.join(env.USERPROFILE || os.homedir(), ".codex");
  const childEnv = { ...env, CODEX_HOME: codexHome };
  return new Promise((resolve, reject) => {
    const child = spawn(command, ["app-server", "--stdio"], {
      env: childEnv,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    const lines = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
    let stderr = "";
    let completed = false;
    let rateResult;
    let usageResult;
    let rateDone = false;
    let usageDone = false;

    const cleanup = () => {
      clearTimeout(timer);
      lines.close();
      if (!child.killed) child.kill();
    };
    const fail = (error) => {
      if (completed) return;
      completed = true;
      cleanup();
      reject(error);
    };
    const finishIfReady = () => {
      if (completed || !rateDone || !usageDone) return;
      completed = true;
      const result = normalizeLiveResponse(rateResult, usageResult);
      cleanup();
      resolve(result);
    };
    const send = (message) => child.stdin.write(`${JSON.stringify(message)}\n`);
    const timer = setTimeout(
      () => fail(new Error(`Codex app-server query timed out after ${timeoutMs} ms`)),
      timeoutMs,
    );

    child.stderr.on("data", (chunk) => {
      stderr = `${stderr}${chunk}`.slice(-2000);
    });
    child.on("error", fail);
    child.on("exit", (code) => {
      if (!completed) fail(new Error(`Codex app-server exited with code ${code}: ${sanitizeDiagnostic(stderr)}`));
    });
    lines.on("line", (line) => {
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        return;
      }
      if (message.id === 1) {
        if (message.error) return fail(new Error(message.error.message || "Initialization failed"));
        send({ method: "initialized", params: {} });
        send({ method: "account/rateLimits/read", id: 2 });
        send({ method: "account/usage/read", id: 3 });
      } else if (message.id === 2) {
        rateDone = true;
        if (message.error) return fail(new Error(message.error.message || "Rate-limit query failed"));
        rateResult = message.result;
        finishIfReady();
      } else if (message.id === 3) {
        usageDone = true;
        usageResult = message.error ? null : message.result;
        finishIfReady();
      }
    });

    send({
      method: "initialize",
      id: 1,
      params: {
        clientInfo: {
          name: "usage_loom",
          title: "UsageLoom",
          version: "0.1.0",
        },
      },
    });
  });
}

export async function getQuotaWithFallback(snapshotOrLoader, options = {}) {
  try {
    return await queryLiveQuota(options);
  } catch (error) {
    const snapshot = typeof snapshotOrLoader === "function"
      ? await snapshotOrLoader()
      : snapshotOrLoader;
    return {
      ...buildQuotaFallback(snapshot),
      liveQueryError: sanitizeDiagnostic(error.message),
    };
  }
}
