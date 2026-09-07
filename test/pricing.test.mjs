import assert from "node:assert/strict";
import test from "node:test";
import { estimateCost, findPrice } from "../src/providers/codex/pricing.mjs";

test("unknown model substrings do not inherit known prices", () => {
  assert.equal(findPrice("not-a-real-model-gpt-5.4-experimental"), null);
  assert.equal(findPrice("gpt-5.4-unknown"), null);
  assert.equal(findPrice("gpt-5.4").model, "gpt-5.4");
});
test("missing cache-write prices reduce pricing coverage", () => {
  const result = estimateCost("gpt-5.4", { inputTokens: 100, cacheWriteInputTokens: 20, outputTokens: 10 });
  assert.equal(result.pricedTokens, 90);
  assert.equal(result.unpricedTokens, 20);
  assert.equal(result.reason, "cache_write_price_missing");
});
test("invalid overlapping token categories are not priced", () => {
  assert.equal(estimateCost("gpt-5.4", { inputTokens: 100, cachedInputTokens: 80, cacheWriteInputTokens: 30, outputTokens: 10 }).costUsd, null);
});
