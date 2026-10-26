// Behavior checks for the Claude Code status line relay (usageloom-statusline.js).
// Usage: node scripts/test-claude-statusline.mjs [path/to/usageloom-statusline.js]
// Runs against a temporary CLAUDE_CONFIG_DIR; never touches the real ~/.claude.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const script = path.resolve(process.argv[2] ?? path.join(root, "src/UsageLoom.App/usageloom-statusline.js"));
assert.ok(existsSync(script), `missing relay script: ${script}`);
const home = mkdtempSync(path.join(os.tmpdir(), "UsageLoom-statusline-"));
const snapshotFile = path.join(home, "usageloom-rate-limits.json");
const env = { ...process.env, CLAUDE_CONFIG_DIR: home };

const run = (input, args = []) => {
  const result = spawnSync(process.execPath, [script, ...args], { input, env, timeout: 10000 });
  assert.equal(result.status, 0, result.stderr?.toString());
  assert.equal(result.stderr.toString(), "");
  return result.stdout;
};
const status = (fiveUsed, reset = 1792485600) => JSON.stringify({
  session_id: "private-session",
  workspace: { current_dir: "D:/项目/私有" },
  cost: { total_cost_usd: 1.23 },
  rate_limits: {
    five_hour: { used_percentage: fiveUsed, resets_at: reset },
    seven_day: { used_percentage: 12.5, resets_at: reset + 86400 }
  }
});
const snapshot = () => JSON.parse(readFileSync(snapshotFile, "utf8"));
const results = [];
const test = (name, body) => { body(); results.push(name); console.log(`PASS ${name}`); };

try {
  test("without --pass prints nothing and saves only the two windows", () => {
    assert.equal(run(status(40)).length, 0);
    const saved = snapshot();
    assert.deepEqual(Object.keys(saved).sort(), ["captured_at", "five_hour", "seven_day", "version"]);
    assert.equal(saved.version, 1);
    assert.ok(Math.abs(saved.captured_at - Date.now()) < 60000);
    assert.deepEqual(saved.five_hour, { used_percentage: 40, resets_at: 1792485600 });
    assert.deepEqual(saved.seven_day, { used_percentage: 12.5, resets_at: 1792572000 });
    assert.ok(!readFileSync(snapshotFile, "utf8").includes("private"));
  });

  test("--pass forwards the exact input bytes", () => {
    const input = Buffer.from(status(41), "utf8");
    assert.ok(run(input, ["--pass"]).equals(input));
    assert.equal(snapshot().five_hour.used_percentage, 41);
  });

  test("unchanged windows within 30 seconds are not rewritten; changes are", () => {
    const first = snapshot().captured_at;
    run(status(41));
    assert.equal(snapshot().captured_at, first);
    run(status(42));
    assert.equal(snapshot().five_hour.used_percentage, 42);
  });

  test("input without usable rate limits keeps the previous snapshot", () => {
    const before = readFileSync(snapshotFile, "utf8");
    for (const input of ["{}", "not json", JSON.stringify({ rate_limits: { five_hour: { used_percentage: 101 } } })]) {
      assert.equal(run(input, ["--pass"]).toString(), input);
      assert.equal(readFileSync(snapshotFile, "utf8"), before);
    }
  });

  test("a missing reset time is stored as null, not guessed", () => {
    rmSync(snapshotFile);
    run(JSON.stringify({ rate_limits: { seven_day: { used_percentage: 3 } } }));
    assert.deepEqual(snapshot().seven_day, { used_percentage: 3, resets_at: null });
    assert.equal(snapshot().five_hour, undefined);
  });

  test("an unwritable Claude home never breaks the status line", () => {
    const input = status(50);
    const result = spawnSync(process.execPath, [script, "--pass"], {
      input, env: { ...env, CLAUDE_CONFIG_DIR: path.join(home, "missing", "dir") }, timeout: 10000
    });
    assert.equal(result.status, 0);
    assert.equal(result.stdout.toString(), input);
  });

  test("shell pipe to an existing status line command keeps its output", () => {
    const downstream = path.join(home, "downstream.js");
    writeFileSync(downstream, "let b='';process.stdin.on('data',c=>b+=c).on('end',()=>process.stdout.write('5h '+JSON.parse(b).rate_limits.five_hour.used_percentage+'%'));");
    const node = JSON.stringify(process.execPath);
    const result = spawnSync(`${node} ${JSON.stringify(script)} --pass | ${node} ${JSON.stringify(downstream)}`, {
      input: status(60), env, shell: true, timeout: 10000
    });
    assert.equal(result.status, 0, result.stderr?.toString());
    assert.equal(result.stdout.toString(), "5h 60%");
    assert.equal(snapshot().five_hour.used_percentage, 60);
  });
} finally {
  rmSync(home, { recursive: true, force: true });
}
console.log(`PASS Claude status line relay: ${results.length} checks`);
