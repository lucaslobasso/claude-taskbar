import { test } from 'node:test'
import assert from 'node:assert/strict'
import { merge, pickWindow } from '../hooks/merge.ts'

const NOW = 1_800_000_000
const iso = (s: number) => new Date(s * 1000).toISOString()

test('maps readings to the file shape (epoch seconds)', () => {
  const out = merge(null, [{ kind: 'five_hour', percentUsed: 42, resetsAt: iso(NOW + 3600) }], NOW)
  assert.deepEqual(out, { five_hour: { used_percentage: 42, resets_at: NOW + 3600 }, seven_day: null })
})

test('same window: the highest percentage wins', () => {
  const old = { five_hour: { used_percentage: 80, resets_at: NOW + 3600 }, seven_day: null }
  assert.equal(merge(old, [{ kind: 'five_hour', percentUsed: 43, resetsAt: iso(NOW + 3601) }], NOW), null)
  const up = merge(old, [{ kind: 'five_hour', percentUsed: 85, resetsAt: iso(NOW + 3600) }], NOW)
  assert.equal(up?.five_hour?.used_percentage, 85)
})

test('a newer window replaces an older one, even with a lower percentage', () => {
  const old = { five_hour: { used_percentage: 90, resets_at: NOW + 100 }, seven_day: null }
  const out = merge(old, [{ kind: 'five_hour', percentUsed: 3, resetsAt: iso(NOW + 18000) }], NOW)
  assert.equal(out?.five_hour?.used_percentage, 3)
})

test('a stale reading from an older window is ignored; an ended window with no new reading is kept (the app shows the dash)', () => {
  const old = { used_percentage: 60, resets_at: NOW + 18000 }
  assert.equal(pickWindow(old, { used_percentage: 99, resets_at: NOW + 100 }, NOW), old)
  const ended = { used_percentage: 60, resets_at: NOW - 1 }
  assert.equal(pickWindow(ended, null, NOW), ended)
  assert.equal(pickWindow(ended, { used_percentage: 5, resets_at: NOW + 99 }, NOW)?.used_percentage, 5)
})

test('readings without a reset time or with a bad one are skipped', () => {
  assert.equal(merge(null, [{ kind: 'five_hour', percentUsed: 10 }, { kind: 'seven_day', percentUsed: 5, resetsAt: 'nope' }], NOW), null)
})
