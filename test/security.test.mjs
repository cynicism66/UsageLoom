import assert from "node:assert/strict";
import test from "node:test";
import { sanitizeDiagnostic } from "../src/providers/codex/quota.mjs";

test("diagnostics redact credentials and personal paths", () => {
  const sanitized = sanitizeDiagnostic(
    "Bearer abcdefghijklmnopqrstuvwxyz access_token=secret-value " +
    "C:\\Users\\alice\\.codex\\auth.json /home/bob/.codex/auth.json alice@example.com",
  );

  assert.doesNotMatch(sanitized, /abcdefghijklmnopqrstuvwxyz/);
  assert.doesNotMatch(sanitized, /secret-value/);
  assert.doesNotMatch(sanitized, /alice|bob/);
  assert.doesNotMatch(sanitized, /example\.com/);
  assert.match(sanitized, /<redacted>/);
  assert.match(sanitized, /<user-path>/);
  assert.match(sanitized, /<email>/);
});

test("diagnostics are bounded", () => {
  assert.equal(sanitizeDiagnostic("x".repeat(2_000)).length, 500);
  assert.equal(sanitizeDiagnostic(""), "Codex app-server query failed");
});
