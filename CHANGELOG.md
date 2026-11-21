# Changelog

All notable changes to this project. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

Usage plugin `taskbar-usage` 0.2.1 only (the exe is unchanged). Get it with `claude plugin update taskbar-usage@claude-taskbar`.

### Added
- A **Claude Taskbar** Start menu shortcut, made by the installer and removed by the uninstaller, to reopen the strip by itself after closing it (search "Claude Taskbar"). Installer change only; the exe is unchanged.

### Fixed
- The weekly (and 5-hour) bar could stay above the number on claude.ai's Usage page: within a window the plugin only ever let the percentage go up, so a drop mid-window (a limit adjustment or correction) stayed stuck at the old peak until the reset. A lower reading is now accepted once the stored one is older than 15 minutes. A stuck value from 0.2.0 clears on the first reading after the update.

## [1.3.0] - 2026-11-17

Updates without the command line.

### Added
- **Check for updates** in the right-click menu, which also shows the version you have. If a newer release exists it asks, downloads the exe, checks it against the release's SHA-256, swaps it in and restarts the strip, then updates the usage plugin with `claude plugin update`. Settings are kept.

### Changed
- "No network" now has one exception: the update check, which runs only when you click it. There is still no background request of any kind, and still no tokens.
- A release is published automatically when a merge to `main` carries a version that has no release yet; pushing a tag by hand is no longer needed.
- A copy started with `CLAUDE_CONFIG_DIR` set (tests) can run next to the installed one.

## [1.2.0] - 2026-11-16

### Added
- Works next to [Taskbar Glance](https://github.com/lucaslobasso/taskbar-glance) (weather and calendar strip). Glance keeps the anchored edge (the left edge or next to the tray) and this strip docks just outward of it, so they never overlap. It moves back by itself when Glance hides or exits. Nothing changes if Glance is not running.

## [1.1.1] - 2026-11-16

### Changed
- When the 5-hour window has reset and no new session has started yet, the 5-hour bar and its percentage are hidden instead of showing an empty bar with "–". They come back with the first reading of the next window. The tooltip still says "reset · waiting for fresh data".

### Fixed
- The release workflow failed for normal (non-pre-release) tags; 1.1.0 was published by hand.

## [1.1.0] - 2026-11-16

### Added
- One-line install from a GitHub release: `irm https://raw.githubusercontent.com/lucaslobasso/claude-taskbar/main/bootstrap.ps1 | iex`. No .NET SDK or C++ tools needed; the exe is checked against the release's SHA-256. `-NoAutostart`, `-Uninstall`, `-Purge`.
- Releases: a `v*` tag builds the Native AOT exe in CI and publishes it with its checksum.
- The `taskbar-usage` plugin installs from this repo's marketplace (`taskbar-usage@claude-taskbar`) instead of a local copy. Installing or uninstalling removes the old local `taskbar-usage@taskbar-usage`.
- Hidden `--render` mode that draws the strip for made-up values into a file, used by `docs/render.py` for the README pictures.
- CI (Native AOT build, plugin tests and validation, install scripts parse), CHANGELOG, issue template.

### Changed
- README in the layout shared by the six tools of the set, with a picture per setting and an "Install the whole set" section.
- `install.ps1` is the from-source path; it shares `install-lib.ps1` with `bootstrap.ps1` and `uninstall.ps1`. New `-LocalMod` installs the plugin from the clone.

### Fixed
- A click finds Claude Launcher in `%USERPROFILE%\.claude\launcher` even when `CLAUDE_CONFIG_DIR` points elsewhere.
- The "no usage data" tooltip and code comments still talked about a status line and a usage ring.

## [1.0.0] - 2026-11-15

### Added
- First release: status dots with counts (needs input, plan ready, failed, working, done, idle), 5-hour and weekly usage bars, tooltips, notifications, left/right position, hide when Claude is closed, the taskbar-usage plugin.

[1.1.1]: https://github.com/lucaslobasso/claude-taskbar/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/lucaslobasso/claude-taskbar/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/lucaslobasso/claude-taskbar/releases/tag/v1.0.0
