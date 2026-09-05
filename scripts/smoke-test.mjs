import { buildLocalUsage, diagnoseSources, getUsageSnapshot } from "../src/providers/codex/usage.mjs";
import { getQuotaWithFallback } from "../src/providers/codex/quota.mjs";

const snapshot = await getUsageSnapshot({ force: true });
const usage = buildLocalUsage(snapshot, { sinceDays: 7, topN: 3 });
const quota = await getQuotaWithFallback(snapshot, { timeoutMs: 20_000 });
const diagnosis = await diagnoseSources(snapshot);

console.log(JSON.stringify({
  ok: true,
  filesScanned: diagnosis.filesScanned,
  sessionsWithUsage: diagnosis.sessionsWithUsage,
  sevenDayTokens: usage.totals.totalTokens,
  quotaSource: quota.source,
  liveQuota: quota.isLive,
  primaryWindow: quota.rateLimits?.primary || null,
  secondaryWindow: quota.rateLimits?.secondary || null,
  warningCounts: {
    malformedLines: diagnosis.warnings.malformedLines,
    duplicateRowsSkipped: diagnosis.warnings.duplicateRowsSkipped,
    replayRowsSkipped: diagnosis.warnings.replayRowsSkipped,
  },
}, null, 2));
