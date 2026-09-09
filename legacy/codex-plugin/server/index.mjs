#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import readline from "node:readline";
import {
  buildLocalUsage,
  buildRecentSessions,
  diagnoseSources,
  getUsageSnapshot,
} from "../src/providers/codex/usage.mjs";
import { getQuotaWithFallback } from "../src/providers/codex/quota.mjs";

const SERVER_INFO = { name: "usage-loom", version: "0.1.0" };
const DASHBOARD_URI = "ui://usage-loom/dashboard-v1.html";
const DASHBOARD_MIME = "text/html;profile=mcp-app";
let dashboardHtml;

const TOOLS = [
  {
    name: "get_combined_report",
    title: "Codex usage and quota overview",
    description:
      "Read-only combined report: live Codex quota windows via the local Codex app-server plus detailed local-machine token attribution from Codex JSONL. Never reads auth.json or conversation content. Cost is only an API-equivalent estimate.",
    inputSchema: {
      type: "object",
      properties: {
        since_days: {
          type: "integer",
          minimum: 1,
          maximum: 3650,
          default: 7,
          description: "Rolling local-history window in days.",
        },
        top_n: {
          type: "integer",
          minimum: 1,
          maximum: 50,
          default: 5,
          description: "Maximum models, projects, and agents per ranking.",
        },
        refresh: {
          type: "boolean",
          default: false,
          description: "Force a fresh local JSONL scan instead of using the 30-second cache.",
        },
      },
      additionalProperties: false,
    },
    _meta: {
      ui: { resourceUri: DASHBOARD_URI },
      "openai/outputTemplate": DASHBOARD_URI,
      "openai/toolInvocation/invoking": "正在读取 Codex 用量…",
      "openai/toolInvocation/invoked": "Codex 用量面板已生成。",
    },
  },
  {
    name: "get_live_quota",
    title: "Live Codex quota windows",
    description:
      "Read current Codex rate-limit windows and reset times through the locally authenticated Codex app-server. Falls back to the latest local JSONL snapshot if the live query fails. Does not read or store OAuth tokens.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false,
    },
  },
  {
    name: "get_local_usage",
    title: "Detailed local Codex token usage",
    description:
      "Aggregate current-machine Codex tokens by model, project directory name, agent type, and local date. It parses only metadata and token_count records, never prompts, replies, reasoning text, tool output, or auth.json.",
    inputSchema: {
      type: "object",
      properties: {
        since_days: {
          type: "integer",
          minimum: 1,
          maximum: 3650,
          default: 7,
        },
        top_n: {
          type: "integer",
          minimum: 1,
          maximum: 100,
          default: 10,
        },
        include_archived: {
          type: "boolean",
          default: true,
        },
        refresh: {
          type: "boolean",
          default: false,
        },
      },
      additionalProperties: false,
    },
  },
  {
    name: "get_recent_sessions",
    title: "Highest-usage Codex tasks",
    description:
      "List highest-token local Codex tasks in a rolling time window. Titles and project directory names are omitted unless include_titles is explicitly true because that metadata may be sensitive.",
    inputSchema: {
      type: "object",
      properties: {
        since_days: {
          type: "integer",
          minimum: 1,
          maximum: 3650,
          default: 7,
        },
        limit: {
          type: "integer",
          minimum: 1,
          maximum: 100,
          default: 10,
        },
        include_titles: {
          type: "boolean",
          default: false,
          description: "Include local task titles and project directory names in tool output.",
        },
        query: {
          type: "string",
          maxLength: 200,
          default: "",
          description: "Optional local filter over task id, title, or project name.",
        },
        refresh: {
          type: "boolean",
          default: false,
        },
      },
      additionalProperties: false,
    },
  },
  {
    name: "diagnose_usage_sources",
    title: "Diagnose Codex usage data",
    description:
      "Read-only diagnostics for Codex home discovery, JSONL availability, latest token row, duplicate/replay skips, and privacy boundaries.",
    inputSchema: {
      type: "object",
      properties: {
        refresh: { type: "boolean", default: true },
      },
      additionalProperties: false,
    },
  },
];

function clampInteger(value, fallback, minimum, maximum) {
  if (!Number.isInteger(value)) return fallback;
  return Math.max(minimum, Math.min(maximum, value));
}

async function invokeTool(name, args = {}) {
  const sinceDays = clampInteger(args.since_days, 7, 1, 3650);
  const topN = clampInteger(args.top_n, 10, 1, 100);
  const includeArchived = args.include_archived !== false;
  const includeTitles = name === "get_recent_sessions" && Boolean(args.include_titles || args.query);
  const snapshotOptions = {
    includeArchived,
    force: Boolean(args.refresh),
    includeTitles,
  };

  if (name === "get_live_quota") {
    return getQuotaWithFallback(() => getUsageSnapshot(snapshotOptions));
  }

  const snapshot = await getUsageSnapshot({
    ...snapshotOptions,
  });

  switch (name) {
    case "get_combined_report": {
      const quota = await getQuotaWithFallback(snapshot);
      return {
        generatedAt: new Date().toISOString(),
        quota,
        localUsage: buildLocalUsage(snapshot, {
          sinceDays,
          topN: clampInteger(args.top_n, 5, 1, 50),
        }),
        interpretation: {
          accountQuota:
            "Server-backed rate-limit state for the currently authenticated Codex account when source is live_codex_app_server.",
          localUsage:
            "Token attribution from JSONL files on this computer only; it may include sessions created under previously active accounts.",
        },
      };
    }
    case "get_local_usage":
      return buildLocalUsage(snapshot, { sinceDays, topN });
    case "get_recent_sessions":
      return buildRecentSessions(snapshot, {
        sinceDays,
        limit: clampInteger(args.limit, 10, 1, 100),
        includeTitles: Boolean(args.include_titles),
        query: args.query || "",
      });
    case "diagnose_usage_sources":
      return diagnoseSources(snapshot);
    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

function summarizeQuotaWindows(result) {
  return (result?.windows || []).map((window) => {
      const used = window.usedPercent === null ? "unknown" : `${window.usedPercent}% used`;
      const remaining =
        window.remainingPercent === null ? "unknown remaining" : `${window.remainingPercent}% remaining`;
      const reset = window.resetsAtIso ? `resets ${window.resetsAtIso}` : "reset time unknown";
      const id = window.limitId ? ` [${window.limitId}]` : "";
      return `${window.windowLabel || window.kind || "window"}${id}: ${used}; ${remaining}; ${reset}`;
  });
}

function summarize(name, result) {
  if (name === "get_live_quota") {
    const windowLines = summarizeQuotaWindows(result);
    return [
      `Quota source: ${result.source}`,
      ...windowLines,
      result.warning || null,
    ].filter(Boolean).join("\n");
  }
  const local = result.localUsage || result;
  if (local?.totals) {
    return [
      `Local ${local.rollingWindowDays}-day usage: ${local.totals.totalTokens} tokens`,
      `Input ${local.totals.inputTokens}; cached ${local.totals.cachedInputTokens}; output ${local.totals.outputTokens}; reasoning ${local.totals.reasoningOutputTokens}`,
      `API-equivalent estimate: $${local.totals.apiEquivalentCostUsd}`,
      result.quota ? `Quota source: ${result.quota.source}` : null,
      ...(result.quota ? summarizeQuotaWindows(result.quota) : []),
    ].filter(Boolean).join("\n");
  }
  return `${name} completed successfully.`;
}

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

async function readDashboardHtml() {
  if (dashboardHtml === undefined) {
    dashboardHtml = await readFile(
      new URL("../assets/usage-dashboard.html", import.meta.url),
      "utf8",
    );
  }
  return dashboardHtml;
}

function toolResult(name, value) {
  return {
    content: [
      { type: "text", text: summarize(name, value) },
      { type: "text", text: JSON.stringify(value, null, 2) },
    ],
    structuredContent: value,
    isError: false,
  };
}

async function handle(message) {
  if (!message || typeof message !== "object") return;
  const { id, method, params } = message;
  if (method === "initialize") {
    send({
      jsonrpc: "2.0",
      id,
      result: {
        protocolVersion: params?.protocolVersion || "2025-06-18",
        capabilities: {
          tools: { listChanged: false },
          resources: { listChanged: false },
        },
        serverInfo: SERVER_INFO,
        instructions:
          "Read-only local Codex token attribution and live account quota. Keep local token accounting distinct from account-backed quota.",
      },
    });
    return;
  }
  if (method === "notifications/initialized" || method === "initialized") return;
  if (method === "ping") {
    send({ jsonrpc: "2.0", id, result: {} });
    return;
  }
  if (method === "tools/list") {
    send({ jsonrpc: "2.0", id, result: { tools: TOOLS } });
    return;
  }
  if (method === "resources/list") {
    send({
      jsonrpc: "2.0",
      id,
      result: {
        resources: [{
          uri: DASHBOARD_URI,
          name: "usage-loom-dashboard",
          title: "UsageLoom dashboard",
          description: "A local, read-only view of provider quota windows and token attribution.",
          mimeType: DASHBOARD_MIME,
        }],
      },
    });
    return;
  }
  if (method === "resources/read") {
    if (params?.uri !== DASHBOARD_URI) {
      send({
        jsonrpc: "2.0",
        id,
        error: { code: -32002, message: `Resource not found: ${params?.uri}` },
      });
      return;
    }
    send({
      jsonrpc: "2.0",
      id,
      result: {
        contents: [{
          uri: DASHBOARD_URI,
          mimeType: DASHBOARD_MIME,
          text: await readDashboardHtml(),
          _meta: { ui: { prefersBorder: true } },
        }],
      },
    });
    return;
  }
  if (method === "tools/call") {
    try {
      const value = await invokeTool(params?.name, params?.arguments || {});
      send({ jsonrpc: "2.0", id, result: toolResult(params?.name, value) });
    } catch (error) {
      send({
        jsonrpc: "2.0",
        id,
        result: {
          content: [{ type: "text", text: `UsageLoom error: ${error.message}` }],
          isError: true,
        },
      });
    }
    return;
  }
  if (id !== undefined) {
    send({
      jsonrpc: "2.0",
      id,
      error: { code: -32601, message: `Method not found: ${method}` },
    });
  }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  if (!line.trim()) return;
  let message;
  try {
    message = JSON.parse(line);
  } catch (error) {
    send({ jsonrpc: "2.0", id: null, error: { code: -32700, message: error.message } });
    return;
  }
  handle(message).catch((error) => {
    if (message.id !== undefined) {
      send({
        jsonrpc: "2.0",
        id: message.id,
        error: { code: -32603, message: error.message },
      });
    }
  });
});
