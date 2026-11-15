import { expect, test } from 'claude-code/testing'
import { windowsFrom } from './register'

test('keeps the two windows in the relay format', () => {
  const windows = windowsFrom([
    { kind: 'five_hour', percentUsed: 23.5, resetsAt: '2026-11-15T14:30:00Z' },
    { kind: 'seven_day', percentUsed: 7, resetsAt: 'not a time' },
    { kind: 'spend_limit', percentUsed: 50 },
  ])
  expect(windows).toEqual({
    five_hour: { used_percentage: 23.5, resets_at: Date.parse('2026-11-15T14:30:00Z') / 1000 },
    seven_day: { used_percentage: 7, resets_at: null },
  })
})

test('drops out-of-range values and saves nothing without a window', () => {
  expect(windowsFrom([{ kind: 'five_hour', percentUsed: 101 }, { kind: 'seven_day', percentUsed: -1 }])).toBe(undefined)
  expect(windowsFrom([])).toBe(undefined)
})
