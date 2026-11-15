import type { EngineInterface, Register, SessionRateLimit } from 'claude-code'

// Saves the 5-hour and weekly rate limits for UsageLoom in the file and format its
// status-line relay (usageloom-statusline.js) writes, since the Claude desktop app
// never runs a status line command. Only the two percentages and reset times; no
// session, project, cost or account data.
const FILE = 'usageloom-rate-limits.json'
const MIN_REWRITE_MS = 30000
const WINDOWS = ['five_hour', 'seven_day'] as const
type Window = { used_percentage: number; resets_at: number | null }
type Windows = Partial<Record<(typeof WINDOWS)[number], Window>>

let saved = ''
let savedAt = 0

export function windowsFrom(limits: readonly SessionRateLimit[]): Windows | undefined {
  const result: Windows = {}
  for (const key of WINDOWS) {
    const limit = limits.find(item => item.kind === key)
    const used = limit?.percentUsed
    if (used === undefined || !Number.isFinite(used) || used < 0 || used > 100) continue
    const reset = limit?.resetsAt ? Date.parse(limit.resetsAt) : NaN
    // Unix seconds, as the relay stores them; an unreadable time is dropped, not guessed.
    result[key] = { used_percentage: used, resets_at: Number.isFinite(reset) && reset > 0 ? Math.floor(reset / 1000) : null }
  }
  return Object.keys(result).length > 0 ? result : undefined
}

async function save($: EngineInterface, limits: readonly SessionRateLimit[]): Promise<void> {
  const windows = windowsFrom(limits)
  if (windows === undefined) return
  const key = JSON.stringify(windows)
  const now = Date.now()
  if (key === saved && now - savedAt < MIN_REWRITE_MS) return
  const configured = await $.env.get('CLAUDE_CONFIG_DIR')
  const home = (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME'))
  const folder = configured || (home ? `${home}/.claude` : undefined)
  if (folder === undefined) return
  await $.fs.write(`${folder}/${FILE}`, JSON.stringify({ version: 1, captured_at: now, ...windows }))
  saved = key
  savedAt = now
}

export const register: Register = on => {
  // Fires after each turn and whenever a window moves a whole point. A failed save
  // never touches the session.
  on('session.measure', async ($, e, next) => {
    try { await save($, e.rateLimits) } catch {}
    return next(e)
  })

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    try { await save($, (await $.session.usage()).rateLimits) } catch {}
    return result
  })
}
