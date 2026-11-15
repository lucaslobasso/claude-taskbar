import type { Register } from 'claude-code'
import { merge, type Snapshot } from './merge'

// Claude Taskbar reads ~/.claude/taskbar/usage.json. Claude Code hands the rate limits to a session, and
// `session.measure` fires when one of them moves a whole point (or appears, or leaves), so this costs nothing
// while a session is idle. Nothing is written unless the taskbar folder exists (the app is installed).
export const register: Register = (on) => {
  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits') && e.rateLimits.length > 0) {
      try {
        const configDir = (await $.env.get('CLAUDE_CONFIG_DIR')) ?? `${(await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME'))}/.claude`
        const dir = `${configDir}/taskbar`
        if (await $.fs.exists(dir)) {
          const file = `${dir}/usage.json`
          let old: Partial<Snapshot> | null = null
          try {
            old = JSON.parse(await $.fs.read(file))
          } catch {}
          const next_ = merge(old, e.rateLimits, Date.now() / 1000)
          if (next_) await $.fs.write(file, JSON.stringify({ ...next_, writtenAt: Date.now() }))
        }
      } catch {}
    }
    return next(e)
  })
}
