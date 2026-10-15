# Changelog

All notable changes to Claude Session for XENEON EDGE are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-10-15

### Added

- **Step progress** — a new row right under the context gauge: `STEP 3/6`, one dot per step (green = done, blinking orange = in progress, grey = not started) and the name of the step Claude is working on. Read from the session's step list (the app's plan, or TaskCreate/TaskUpdate); without one, from an active `NEXT-SESSION.md` in the session's folder. Hidden when there is no unfinished list. New iCUE setting *Show Step Progress* (on by default). Needs bridge 1.0.4 and widget 1.0.25.

### Notes

- Upgrading: run `Install.cmd` again (the bridge changed this time), then import `claudesession-v1.0.25.icuewidget` in iCUE.

## [1.1.0] - 2026-10-15

### Added

- **Waiting Banner** — a large blinking "⚠ WAITING FOR YOU" banner above the step line whenever Claude has a pending question, so it is obvious from the corner of your eye.
- **Auto-cycle** — new iCUE setting *Auto-cycle Working Sessions*: Off (default) / every 5, 10, 15, 30 or 60 seconds. Rotates only through sessions that are working, pauses while a question is pending, while a session is pinned, and for 60 seconds after you touch the text.

### Changed

- **Step Feed** shows only the latest step (was the last 5), giving Claude's reply more room.

### Notes

- The bridge (`ClaudeEdgeBridge.exe`) behaves the same as in 1.0.0; it was rebuilt so the binary matches the source (shared files now from claude-meter-for-windows v1.10.0, which adds a usage row this widget does not show). Upgrading: run `Install.cmd` again, then import the new `.icuewidget` in iCUE.

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

[1.1.0]: https://github.com/SKGoC-CLI/claude-session-for-xeneon-edge/releases/tag/v1.1.0
[1.0.0]: https://github.com/SKGoC-CLI/claude-session-for-xeneon-edge/releases/tag/v1.0.0
