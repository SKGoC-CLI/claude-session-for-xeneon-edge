# Changelog

All notable changes to Claude Session for XENEON EDGE are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-01

### Added

- **Bridge** (ClaudeEdgeBridge.exe) — .NET 8 WinExe that runs as a background process, tails your Claude Code session transcripts, and serves live session data via HTTP.
- **Widget** — iCUE HTML widget that polls the bridge and displays your current session on the CORSAIR XENEON EDGE dashboard.
- **Session display** — current prompt, latest reply, status (working/waiting), model name, context window gauge, and 5-hour and 7-day usage percentages.
- **Step Feed** — displays the last 5 tool calls of the current turn with execution time.
- **Question alerts** — when Claude asks a question, the widget shows a blinking border and "ASK" header to flag your attention (the question text and options are read-only; you answer in Claude Code).
- **Multi-session support** — cycle button to switch between active sessions; selected session is pinned for 60 seconds.
- **Session title** — optionally shows your custom session title (configurable in iCUE).
- **Installer scripts** — `Install.cmd` and `Uninstall.cmd` for one-click setup and removal on Windows.
- **Browser isolation** — cross-origin access is restricted to the iCUE widget (`file://`) and localhost pages, and foreign `Host` headers are refused, so websites cannot read your session.
- **Self-test** — `--selftest` flag to verify the bridge is working correctly before deployment.
- **Logging** — bridge logs to `%APPDATA%\ClaudeEdgeBridge\logs\` for troubleshooting.

### First Public Release

This is the first public release of Claude Session for XENEON EDGE, providing live session monitoring on your CORSAIR XENEON EDGE touchscreen without interrupting your main workflow.

[1.0.0]: https://github.com/SKGoC-CLI/claude-session-for-xeneon-edge/releases/tag/v1.0.0
