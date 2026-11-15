# Changelog

All notable changes to this project. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

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

[1.1.0]: https://github.com/lucaslobasso/claude-taskbar/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/lucaslobasso/claude-taskbar/releases/tag/v1.0.0
