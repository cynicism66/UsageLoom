import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import readline from "node:readline";
import test from "node:test";

test("MCP server initializes and lists read-only tools", async () => {
  const child = spawn(process.execPath, ["server/index.mjs"], {
    cwd: new URL("..", import.meta.url),
    windowsHide: true,
    stdio: ["pipe", "pipe", "inherit"],
  });
  const lines = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
  const messages = [];
  const done = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("MCP test timed out")), 5000);
    lines.on("line", (line) => {
      messages.push(JSON.parse(line));
      if ([2, 3, 4].every((id) => messages.some((message) => message.id === id))) {
        clearTimeout(timer);
        resolve();
      }
    });
  });
  child.stdin.write(`${JSON.stringify({
    jsonrpc: "2.0",
    id: 1,
    method: "initialize",
    params: { protocolVersion: "2025-06-18", clientInfo: { name: "test", version: "1" } },
  })}\n`);
  child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" })}\n`);
  child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} })}\n`);
  child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 3, method: "resources/list", params: {} })}\n`);
  child.stdin.write(`${JSON.stringify({
    jsonrpc: "2.0",
    id: 4,
    method: "resources/read",
    params: { uri: "ui://usage-loom/dashboard-v1.html" },
  })}\n`);
  try {
    await done;
    const list = messages.find((message) => message.id === 2);
    assert.equal(list.result.tools.length, 5);
    assert.ok(list.result.tools.every((tool) => tool.name.startsWith("get_") || tool.name.startsWith("diagnose_")));
    const combined = list.result.tools.find((tool) => tool.name === "get_combined_report");
    assert.equal(combined._meta.ui.resourceUri, "ui://usage-loom/dashboard-v1.html");
    const resources = messages.find((message) => message.id === 3);
    assert.equal(resources.result.resources[0].mimeType, "text/html;profile=mcp-app");
    const dashboard = messages.find((message) => message.id === 4);
    assert.match(dashboard.result.contents[0].text, /UsageLoom/);
    assert.match(dashboard.result.contents[0].text, /remainingPercent/);
  } finally {
    lines.close();
    child.kill();
  }
});
