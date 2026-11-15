// Usage snapshot for Claude Taskbar. Paste into a Claude Code status line script (Node), and call
// snapshotUsage(input) with the parsed status JSON. It costs no tokens: the status line already receives
// rate_limits. It only writes when the taskbar folder exists (so nothing happens without the app).
//
// Every open session runs its status line, but each one reports the rate limits from ITS OWN last API call,
// so idle sessions carry stale (lower) numbers. Usage within a window only goes up, so the readings are
// merged instead of overwritten: per window the newest window wins (later reset), and within the same window
// the highest percentage wins. Writes happen only when the merged result changes, atomically, never throwing.
import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const SAME_WINDOW_S = 600 // reset times within 10 min of each other are the same window

const pickWindow = (old, neu, now) => {
  const ok = w => w && typeof w.used_percentage === 'number' && typeof w.resets_at === 'number'
  if (!ok(neu) || neu.resets_at <= now) return ok(old) ? old : null // no reading, or one for a window that ended
  if (!ok(old) || old.resets_at <= now) return neu
  if (neu.resets_at - old.resets_at > SAME_WINDOW_S) return neu // a newer window started
  if (old.resets_at - neu.resets_at > SAME_WINDOW_S) return old // neu is from an older window
  return neu.used_percentage >= old.used_percentage ? neu : old
}

export const snapshotUsage = input => {
  const limits = input?.rate_limits
  if (!limits?.five_hour && !limits?.seven_day) return
  try {
    const dir = join(process.env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude'), 'taskbar')
    if (!existsSync(dir)) return
    const file = join(dir, 'usage.json')
    let old = {}
    try {
      old = JSON.parse(readFileSync(file, 'utf8'))
    } catch {}
    const now = Date.now() / 1000
    const five_hour = pickWindow(old.five_hour, limits.five_hour, now)
    const seven_day = pickWindow(old.seven_day, limits.seven_day, now)
    if (JSON.stringify(five_hour) === JSON.stringify(old.five_hour ?? null) &&
        JSON.stringify(seven_day) === JSON.stringify(old.seven_day ?? null)) return
    const tmp = `${file}.${process.pid}.tmp`
    writeFileSync(tmp, JSON.stringify({ five_hour, seven_day, writtenAt: Date.now() }))
    renameSync(tmp, file)
  } catch {}
}
