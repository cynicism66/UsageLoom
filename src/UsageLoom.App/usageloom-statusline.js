#!/usr/bin/env node
// UsageLoom snapshot relay for the Claude Code status line.
//
// Claude Code hands the 5-hour and weekly rate-limit percentages only to the
// command configured as "statusLine" in its settings.json, and never stores them.
// This script saves just those numbers for UsageLoom and prints nothing itself:
//
//   node "<path>/usageloom-statusline.js"
//       Saves the snapshot; the status line stays empty.
//   node "<path>/usageloom-statusline.js" --pass | <existing status line command>
//       Saves the snapshot and forwards Claude Code's input unchanged, so an
//       existing status line keeps working exactly as before.
//
// Writes only <CLAUDE_CONFIG_DIR or ~/.claude>/usageloom-rate-limits.json. No network,
// no credentials, no session, project or cost data. Any failure is silent so the
// status line itself is never affected.
'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');

const FILE_NAME = 'usageloom-rate-limits.json';
const MIN_REWRITE_MS = 30000;
const WINDOWS = ['five_hour', 'seven_day'];

function windowsFrom(input) {
  const limits = input && typeof input === 'object' ? input.rate_limits : null;
  if (!limits || typeof limits !== 'object') return null;
  const result = {};
  for (const key of WINDOWS) {
    const w = limits[key];
    const used = w && typeof w === 'object' ? w.used_percentage : undefined;
    if (typeof used !== 'number' || !Number.isFinite(used) || used < 0 || used > 100) continue;
    const reset = w.resets_at;
    result[key] = {
      used_percentage: used,
      // Unix seconds as provided by Claude Code; anything else is dropped, not guessed.
      resets_at: typeof reset === 'number' && Number.isFinite(reset) && reset > 0 ? reset : null
    };
  }
  return Object.keys(result).length ? result : null;
}

function sameWindows(a, b) {
  return WINDOWS.every(key => {
    const x = a && a[key], y = b && b[key];
    if (!x || !y) return !x && !y;
    return x.used_percentage === y.used_percentage && x.resets_at === y.resets_at;
  });
}

function save(raw, now) {
  let input;
  try { input = JSON.parse(raw.toString('utf8')); } catch (e) { return; }
  const windows = windowsFrom(input);
  if (!windows) return;
  const home = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude');
  const file = path.join(home, FILE_NAME);
  try {
    const previous = JSON.parse(fs.readFileSync(file, 'utf8'));
    const age = now - previous.captured_at;
    if (previous.version === 1 && age >= 0 && age < MIN_REWRITE_MS && sameWindows(previous, windows)) return;
  } catch (e) {}
  const snapshot = Object.assign({ version: 1, captured_at: now }, windows);
  const temporary = `${file}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(snapshot), 'utf8');
    fs.renameSync(temporary, file);
  } catch (e) {
    try { fs.unlinkSync(temporary); } catch (ignored) {}
  }
}

function main() {
  const pass = process.argv.slice(2).includes('--pass');
  const chunks = [];
  let done = false;
  const finish = () => {
    if (done) return;
    done = true;
    const raw = Buffer.concat(chunks);
    // Forward first so a chained status line never waits for the snapshot write.
    if (pass) process.stdout.write(raw);
    try { save(raw, Date.now()); } catch (e) {}
    process.stdin.destroy();
  };
  process.stdin.on('data', chunk => chunks.push(chunk));
  process.stdin.on('end', finish);
  process.stdin.on('error', finish);
  setTimeout(finish, 1500).unref();
}

if (require.main === module) main();

module.exports = { windowsFrom, save, FILE_NAME };
