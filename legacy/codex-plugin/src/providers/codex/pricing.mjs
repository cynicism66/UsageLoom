// Codex/OpenAI estimates in USD per 1M tokens. Verify before each release.
// Last verified: 2026-09-04.
const PRICES = Object.freeze({
  "gpt-5.6-sol": { input: 4, cachedInput: 0.4, cacheWrite: 5, output: 20 },
  "gpt-5.6-terra": { input: 2, cachedInput: 0.2, cacheWrite: 2.5, output: 12 },
  "gpt-5.6-luna": { input: 0.2, cachedInput: 0.02, cacheWrite: 0.25, output: 1.2 },
  "gpt-5.5": { input: 5, cachedInput: 0.5, cacheWrite: null, output: 30 },
  "gpt-5.4": { input: 2.5, cachedInput: 0.25, cacheWrite: null, output: 15 },
  "gpt-5.4-mini": { input: 0.75, cachedInput: 0.075, cacheWrite: null, output: 4.5 },
  "gpt-5.3-codex": { input: 1.75, cachedInput: 0.175, cacheWrite: null, output: 14 },
  "gpt-5.2-codex": { input: 1.75, cachedInput: 0.175, cacheWrite: null, output: 14 },
});

function normalizeModel(model) {
  return String(model || "unknown").trim().toLowerCase().replaceAll("_", "-");
}

export function findPrice(model) {
  const normalized = normalizeModel(model);
  if (PRICES[normalized]) return { model: normalized, rates: PRICES[normalized] };

  // 未核对的后缀或包含模型名的字符串不能自动获得价格。
  return null;
}

export function estimateCost(model, usage) {
  const price = findPrice(model);
  if (!price) return { costUsd: null, pricedTokens: 0, pricingModel: null };

  const input = Math.max(0, Number(usage.inputTokens) || 0);
  const cached = Math.max(0, Number(usage.cachedInputTokens) || 0);
  const cacheWrite = Math.max(0, Number(usage.cacheWriteInputTokens) || 0);
  const output = Math.max(0, Number(usage.outputTokens) || 0);
  const uncached = Math.max(0, input - cached - cacheWrite);
  const missingWritePrice = cacheWrite > 0 && price.rates.cacheWrite === null;
  if (cached + cacheWrite > input || Number(usage.reasoningOutputTokens || 0) > output) {
    return { costUsd: null, pricedTokens: 0, pricingModel: price.model, reason: "invalid_categories" };
  }
  const costUsd = (
    uncached * price.rates.input +
    cached * price.rates.cachedInput +
    cacheWrite * (price.rates.cacheWrite ?? 0) +
    output * price.rates.output
  ) / 1_000_000;

  return {
    costUsd,
    pricedTokens: input + output - (missingWritePrice ? cacheWrite : 0),
    unpricedTokens: missingWritePrice ? cacheWrite : 0,
    reason: missingWritePrice ? "cache_write_price_missing" : null,
    pricingModel: price.model,
  };
}

export function pricingCatalog() {
  return structuredClone(PRICES);
}
