// Usage snapshot for Claude Taskbar. Paste into a Claude Code status line script (Node), and call
// snapshotUsage(input) with the parsed status JSON. It costs no tokens: the status line already receives
// rate_limits. It only writes when the taskbar folder exists (so nothing happens without the app),
// at most every 30 seconds, atomically (temp file + rename), and it never throws.
import { existsSync, renameSync, statSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

export const snapshotUsage = input => {
  const limits = input?.rate_limits
  if (!limits?.five_hour && !limits?.seven_day) return
  try {
    const dir = join(process.env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude'), 'taskbar')
    if (!existsSync(dir)) return
    const file = join(dir, 'usage.json')
    try {
      if (Date.now() - statSync(file).mtimeMs < 30_000) return
    } catch {}
    const tmp = `${file}.${process.pid}.tmp`
    writeFileSync(tmp, JSON.stringify({ five_hour: limits.five_hour ?? null, seven_day: limits.seven_day ?? null, writtenAt: Date.now() }))
    renameSync(tmp, file)
  } catch {}
}
