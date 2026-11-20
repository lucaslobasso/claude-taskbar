// Pure merge logic for the usage snapshot (kept free of `$` so node can test it).
//
// Every open session reports the rate limits from ITS OWN last API call, so a reading from a session that has not
// talked to the API for a while can be lower than the truth. Readings are therefore merged instead of overwritten:
// per window the newest window wins (later reset), and within the same window a higher % wins. A LOWER % within the
// same window is accepted only once the stored one is older than STALE_S, because the real number can go down too
// (a limit adjustment or correction mid-week) and must not stay stuck at an old peak until the window resets.

export type Window = { used_percentage: number; resets_at: number; at?: number } // at = epoch s of the reading
export type Snapshot = { five_hour: Window | null; seven_day: Window | null }
export type Reading = { kind: string; percentUsed: number; resetsAt?: string }

const SAME_WINDOW_S = 600 // reset times within 10 min of each other are the same window
export const STALE_S = 900 // a stored reading older than 15 min no longer blocks a lower one

const ok = (w: Window | null | undefined): w is Window =>
  !!w && typeof w.used_percentage === 'number' && typeof w.resets_at === 'number'

export const pickWindow = (old: Window | null | undefined, neu: Window | null | undefined, now: number): Window | null => {
  if (!ok(neu) || neu.resets_at <= now) return ok(old) ? old : null // no reading, or one for a window that ended
  if (!ok(old) || old.resets_at <= now) return neu
  if (neu.resets_at - old.resets_at > SAME_WINDOW_S) return neu // a newer window started
  if (old.resets_at - neu.resets_at > SAME_WINDOW_S) return old // neu is from an older window
  if (neu.used_percentage >= old.used_percentage) return neu
  const stale = typeof old.at !== 'number' || now - old.at > STALE_S // files from 0.2.0 carry no time: stale
  return stale ? neu : old
}

const toWindow = (r: Reading | undefined, now: number): Window | null => {
  if (!r || !r.resetsAt) return null
  const ms = Date.parse(r.resetsAt)
  return Number.isFinite(ms) ? { used_percentage: r.percentUsed, resets_at: Math.round(ms / 1000), at: Math.round(now) } : null
}

// Merge a session's readings into the stored snapshot. Returns null when nothing changed.
export const merge = (old: Partial<Snapshot> | null | undefined, readings: Reading[], now: number): Snapshot | null => {
  const five_hour = pickWindow(old?.five_hour, toWindow(readings.find(r => r.kind === 'five_hour'), now), now)
  const seven_day = pickWindow(old?.seven_day, toWindow(readings.find(r => r.kind === 'seven_day'), now), now)
  const same =
    JSON.stringify(five_hour) === JSON.stringify(old?.five_hour ?? null) &&
    JSON.stringify(seven_day) === JSON.stringify(old?.seven_day ?? null)
  return same ? null : { five_hour, seven_day }
}
