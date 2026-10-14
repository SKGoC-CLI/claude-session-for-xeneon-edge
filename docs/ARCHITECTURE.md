# Architecture

Claude Session for XENEON EDGE consists of a bridge process and an iCUE HTML widget.

## Bridge (ClaudeEdgeBridge.exe)

A .NET 8 WinExe that:
- Listens on `http://127.0.0.1:8975` (localhost only)
- Scans `%USERPROFILE%\.claude\projects\` and tails session transcripts
- Selects the "active" session (most recently typed in by the user, not by file modification time)
- Parses transcripts to extract prompt, reply, status, model, and pending questions
- Fetches 5-hour and 7-day usage every 180 seconds from the Anthropic API
- Serves three HTTP endpoints
- Logs to `%APPDATA%\ClaudeEdgeBridge\logs\`

## Widget (iCUE HTML)

Runs inside iCUE on the XENEON EDGE dashboard:
- Polls bridge every 1.5 seconds for session state
- Renders prompt, reply, status, model, context gauge, and usage
- Cycles through active sessions (2+ sessions show a cycle button)
- Shows question alerts (blinking border + "WAITING FOR YOU" banner; read-only; answer in Claude Desktop)
- Optional Auto-cycle: every N seconds requests the next working session via `?session=` (paused by a pending question, a pin, or a recent touch)

Configurable: bridge port, session title visibility, gauge color mode, text/background/accent colors, transparency.

## Data Flow

```
Claude Code Transcript
         ↓
   SessionTailer
(Select active session,
parse tail of .jsonl)
         ↓
    BridgeState
         ↓
GET /session.json ← Widget (polls 1.5s)
         ↓
XENEON EDGE display
```

Usage data: `credentials.json` / Claude Desktop token → `UsageClient` → Anthropic API (`/api/oauth/usage`) → `GET /usage.json` ← Widget.

## HTTP Endpoints

All responses carry `Cache-Control: no-store`. `Access-Control-Allow-Origin: *` is sent only to the iCUE widget (`Origin: file://`; `null` is accepted too) and to localhost pages, so websites open in a browser cannot read the data. Requests whose `Host` is not `127.0.0.1`, `localhost` or `[::1]` get `403` (DNS-rebinding guard).

| Endpoint | Method | Returns |
|----------|--------|---------|
| `/session.json` | GET | Current session (prompt, reply, status, model, context fill, pending question if any) + array of active sessions for cycling |
| `/usage.json` | GET | 5-hour and 7-day usage percentages (stale flag if last poll failed) |
| `/heartbeat` | POST | 204 (keep-alive) |

Query `?session=<id>` on `/session.json` to request a specific session; uses auto-selected if ID not found or stale.

## Active Session Selection

1. Scans `~/.claude/projects/*/` for `*.jsonl` files (last 8 by mtime)
2. Reads last 1 MB and parses lines; extracts most recent human prompt (`type=="user"` with `promptSource`)
3. **Winner:**
   - If any session has `bridge-session` line (open in Claude Desktop), pick newest prompt among those
   - Else pick newest prompt globally
4. **Active list:** sessions with human prompt in last 30 minutes, newest first, max 5

Sessions with pending questions always win, even if other sessions have newer prompts.

## Pending Questions

A Pending Ask is an `AskUserQuestion` or `ExitPlanMode` tool call without a matching `tool_result`. Bridge:
1. Parses tail of transcript for these `tool_use` objects
2. Extracts question, header, and options
3. Strips markdown, truncates to 4000 characters
4. Sends via `/session.json`

Widget displays a read-only alert; user answers in Claude Desktop.

## Shared Code

`bridge/Shared/` (reused from [claude-meter-for-windows](https://github.com/SKGoC-CLI/claude-meter-for-windows)):
- `UsageClient.cs` — fetch usage from Anthropic API
- `CredentialStore.cs` — read Claude token from local storage
- `DesktopCredentialStore.cs` — decrypt Claude Desktop's cached token (Windows DPAPI)

These are verbatim copies (see the header line in each file); change them upstream, not here.

## Self-Test

Run `ClaudeEdgeBridge.exe --selftest` to verify session parsing, selection logic, truncation, and Pending Ask extraction. Prints `SELFTEST OK` on success.

## Logging

Startup, poll failures and errors go to `%APPDATA%\ClaudeEdgeBridge\logs\`. Logs never include transcripts or tokens.
