# claude-taskbar

Native AOT C# (net11.0, raw Win32 P/Invoke, no WinForms/WPF). Footprint is a feature: no polling loops, no new
threads, no UI frameworks, no reflection-based JSON. Never add network calls (zero-token promise).

## Build / run
- `dotnet publish src/ClaudeTaskbar.csproj -c Release -o out` (needs VS "Desktop development with C++" for the linker).
  Stop the running `ClaudeTaskbar` first or the copy fails (exe locked).
- `.\install.ps1` builds + installs to `~/.claude/taskbar` + HKCU Run + starts it.
- `nuget.config` clears machine feeds on purpose (a broken local VS feed broke restore).

## Testing without touching the real ~/.claude
- Point `CLAUDE_CONFIG_DIR` at a fake folder with `sessions/<n>.json` (use a live pid, e.g. explorer's, and omit
  `procStart`) and `jobs/<id>/state.json`, plus `taskbar/usage.json` (written by the `mod/` plugin on `session.measure`; tests: `node --test mod/test/`). Only one instance runs (named mutex).
- `CLAUDE_TASKBAR_DEBUG=1` logs renders/toasts to `taskbar.log`; `CLAUDE_TASKBAR_DEBUG_TIP=working|gauge|...` opens a
  tooltip at startup (no mouse needed).
- Screenshots must BitBlt with CAPTUREBLT (0x40CC0020) or the layered strip is invisible in the capture.

## Gotchas
- The strip must be WS_EX_TOPMOST *and* owned by `Shell_TrayWnd`; setting the owner after creation does not make it
  topmost, and without topmost the taskbar draws over it.
- In PowerShell, pass `[NullString]::Value` (not `$null`) for FindWindow's title, or it searches for an empty title.
- P/Invoke structs holding `fixed char` buffers (e.g. `PROCESSENTRY32W`) need `CharSet = CharSet.Unicode`, or the default
  ANSI marshalling reads 1-char names.
- "Claude is open" (`Sessions.ClaudeOpen`) = a client process (claude.exe not parented by claude.exe and not the daemon)
  exists, or an interactive session file, or a session is working/waiting. Every `bg` session belongs to the daemon,
  which outlives the terminals, so session files alone never say "closed".
