# Dev-only mock of the bridge's /session.json, so the widget layout can be iterated
# on in a browser without a live Claude session. Port 8977 leaves the real bridge
# on 8975 alone. Switch scenes by hitting /scene/<name>.
#   working | waiting | asking | asking-long | broken | table | multi
$ErrorActionPreference = 'Stop'
$scene = 'working'

$long = @'
All done - the widget is now live on the XENEON EDGE screen and working: it shows 5H 91% with a 1:48:33 countdown and WEEK 10%

What got fixed this round is the "use strict" problem that silently killed the whole script with no visible error. It is removed now, and icueEvents is declared as a plain variable, the same way all 18 of Corsair's standard widgets do it.

The only open item is that I have not yet tested a machine restart to see whether the Bridge comes up by itself and the widget shows numbers within 3 minutes.
'@

# Raw markdown, exactly as the assistant would actually write it — this scene proves
# the bridge's Plain() strip (mirrored below) turns it into something the EDGE's
# plain-text renderer can show without raw "| a | b |" / "**x**" punctuation.
$tableMd = @'
## Today's WORK-LOG summary

I checked `SessionTailer.cs`. Status of each task, per the table:

| Task | File | Status | Estimate | Owner | Notes |
|---|---|---|---|---|---|
| 1 | SessionTailer.cs | **Done** | 20 min | coder | add model/effort |
| 2 | SessionTailer.cs | **Done** | 15 min | coder | strip markdown before cutting at 1200 chars |
| 3 | index.html | In progress | 30 min | coder | auto-scroll + model chip |
| 3.5 | edge.css | Queued | 15 min | coder | two pager buttons |
| 5 | mock-session.ps1 | Queued | 10 min | coder | add scene table |
| 6 | selftest | Queued | 10 min | main | add new assertion |

Details are referenced at [WORK-LOG-2026-08-20-edge-session-widget.md:405](WORK-LOG-2026-08-20-edge-session-widget.md:405). Key points to watch:

- A markdown table eats `20-30%` of the 1200-character budget if not stripped first, so the tail of the message gets lost
- The table separator row `|---|---|` must be dropped as a whole line, not just have the `|` characters removed
- A multiplication like `3 * 4 = 12` must not be stripped, since it is not a bold marker

In short, every task lives in `bridge/` and `widget-session/`, and nothing touches files outside the project. I plan to finish the rest in this round, then run `dotnet build` and `--selftest` to show the results again. I am working through the tasks one by one in the planned order.

One more thing to check: `MaxTextChars = 1200` is unchanged, so if the text is still too long after stripping it gets cut and ends with `…` exactly as before - no behavior change here.
'@

# Mirrors bridge/SessionTailer.cs `Plain()` — dev-only, keep it short and in sync by eye.
function Plain($s) {
    # a plain foreach, not ForEach-Object: the fence flag has to persist across lines, and a
    # pipeline block writing $inFence would silently create its own copy per iteration
    $out = @()
    $inFence = $false
    foreach ($line in ($s -split "`n")) {
        if ($line -match '^\s*```') { $inFence = -not $inFence; continue }
        if ($inFence) { $out += $line; continue }        # fence content means what it says — hands off
        if ($line -match '^\s*\|[\s:|-]+\|\s*$') { continue }  # table separator row
        if ($line -match '^\s*\|.*\|\s*$') {
            $trimmed = $line.Trim()
            $cells = $trimmed.Substring(1, $trimmed.Length - 2) -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 }
            $line = $cells -join ' · '
        }
        $line = $line -replace '^\s*#{1,6}\s+', ''
        $line = $line -replace '^\s*[-*]\s+', '· '
        $line = $line -replace '\[([^\]]+)\]\((?:[^()]|\([^()]*\))*\)', '$1'
        $line = $line -replace '\*\*(.+?)\*\*', '$1'
        $line = $line -replace '`([^`]+)`', '$1'
        $out += $line
    }
    return $out -join "`n"
}

function Scene($name) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $base = [ordered]@{
        ok = $true
        session_id = '928ddb46-aeed-4ab6-89fa-6ebda54fc1b6'
        project = 'C--Users-you-projects-demo-app'
        title = 'iCUE widget for Claude integration'
        updated_at = $now
        status = 'working'
        activity = 'Bash'
        text = $long
        text_at = $now - 134
        model = $null
        effort = $null
        context_tokens = 90123
        context_window = 1000000
        error = $null
        question = $null
        # Step Feed: five done steps and one still running
        steps = @(
            [ordered]@{ name = 'Read'; label = 'SessionTailer.cs'; start = $now - 95; end = $now - 94 }
            [ordered]@{ name = 'Grep'; label = 'activity|Activity'; start = $now - 90; end = $now - 89 }
            [ordered]@{ name = 'Edit'; label = 'index.html'; start = $now - 70; end = $now - 69 }
            [ordered]@{ name = 'Agent'; label = 'Bridge: steps[] + open-turn status'; start = $now - 60; end = $now - 20 }
            [ordered]@{ name = 'Bash'; label = 'Build bridge and run the self-test with a very long description that must ellipsize'; start = $now - 12; end = $null }
        )
        quiet_since = $now - 12
        # Goal Progress: third of six steps under way
        goal = [ordered]@{ done = 2; total = 6; current = 'Draw the goal progress row under the context gauge'; marks = 'ddcppp' }
    }
    switch ($name) {
        'thinking' { $base.activity = $null; $base.steps[4].start = $now - 35; $base.steps[4].end = $now - 23; $base.quiet_since = $now - 23 }
        'waiting' { $base.status = 'waiting'; $base.activity = $null; $base.goal = $null }
        'broken'  { $base.ok = $false; $base.error = 'no active session'; $base.text = $null; $base.title = $null }
        'table'   {
            $base.status = 'waiting'; $base.activity = $null
            $body = Plain $tableMd
            # same rule as the bridge: keep the END, because that is the end the widget shows
            if ($body.Length -gt 4000) { $body = '…' + $body.Substring($body.Length - 4000) }
            $base.text = $body
            $base.model = 'claude-opus-5'
            $base.effort = 'high'
        }
        'asking-long' {
            # The reason the card was rebuilt: a real grilling question runs many lines and
            # five options, which the old two-line clamp threw away without a trace.
            # Carries `sessions` as well, so the card and the cycle button can be seen on
            # screen together -- they share the second row now.
            $base.status = 'waiting'; $base.activity = $null
            $base.model = 'claude-opus-5'; $base.effort = 'high'
            $base.sessions = $multiSessions | ForEach-Object {
                [ordered]@{ id = $_.id; project = $_.project; title = $_.title; status = $_.status; updated_at = $now }
            }
            $base.question = [ordered]@{
                id = 'q_long'
                header = 'Pin storage'
                question = @'
Right now the Bridge picks the Active Session itself from the file most recently typed into, but when several sessions run at once the screen jumps around depending on who typed last, so there is no way to leave one in view to read.

The question is where the "pin" state should live: in the widget (only knows about this screen, lost on refresh, but the Bridge needs to know nothing more) or in the Bridge (every screen agrees, but it becomes one more piece of state that needs a lock and must be cleared when the session dies).

Also consider the case where iCUE reloads the widget while a pin is active, and the case where the pinned session disappears from the active list while still pinned.
'@
                asked_at = $now
                expires_at = $now + 30
                options = @(
                    [ordered]@{ label = 'A. Store in widget'; description = 'Least code; the Bridge needs to know nothing about pins' }
                    [ordered]@{ label = 'B. Store in Bridge'; description = 'Every screen agrees, but adds state that needs a lock and must be cleared' }
                    [ordered]@{ label = 'C. Store in widget + remember in localStorage'; description = 'Survives iCUE reloads, but can linger across days if not cleared' }
                    [ordered]@{ label = 'D. No pin at all'; description = 'The cycle button switches immediately, and auto can always override' }
                )
            }
        }
        'asking'  {
            $base.status = 'waiting'; $base.activity = $null
            $base.question = [ordered]@{
                id = 'q_demo'
                header = 'Reply channel'
                from = 'CORSAIR XENEON EDGE Claude App'
                question = 'Which way should the answer you tap on the EDGE screen travel back into Claude?'
                asked_at = $now
                expires_at = $now + 30
                options = @(
                    [ordered]@{ label = 'A. MCP tool (recommended)'; description = 'The Bridge exposes an extra MCP server and Claude calls this tool instead of AskUserQuestion; does not steal focus from the game' }
                    [ordered]@{ label = 'B. Inject keystrokes into the window'; description = 'Uses SendInput to type the answer straight into the app; must pull focus away from the game, very fragile' }
                    [ordered]@{ label = 'C. Display only'; description = 'The screen shows what Claude asked, but you still alt-tab and answer on the main monitor; cheapest and fastest' }
                    [ordered]@{ label = 'D. Use the phone instead'; description = 'Remote control is already on, 0 lines of code, but you have to pick up the phone' }
                )
            }
        }
    }
    return $base
}

# Phase 4 mock data: 3 concurrent sessions, so the cycle button ("2/3 ▶") and ?session=
# can be exercised without three real Claude Code sessions running at once.
$multiSessions = @(
    [ordered]@{
        id = 'multi-session-aaa'; project = 'project-alpha'; title = 'Session A - UI work'
        status = 'working'; activity = 'Edit'
        text = 'Editing index.html now'
        # Long on purpose: the only scene that puts a two-line prompt, the model chip and
        # the cycle button in the second row at once — the worst case for that row.
        prompt = 'Please fix the cycle button, and also check whether the spacing under the divider is too tight. Make it readable from the chair'
        model = 'claude-opus-5'; effort = 'high'; minutesAgo = 1; ctx = 640000; win = 1000000
    }
    [ordered]@{
        id = 'multi-session-bbb'; project = 'project-beta'; title = 'Session B - bridge work'
        status = 'waiting'; activity = $null
        text = 'GetAllActive() has been added'
        prompt = 'Add a function to pick among multiple sessions'
        model = 'claude-sonnet-5'; effort = $null; minutesAgo = 8; ctx = 12000; win = 1000000
    }
    [ordered]@{
        id = 'multi-session-ccc'; project = 'project-gamma'; title = 'Session C - docs'
        status = 'working'; activity = 'Write'   # 2 working sessions, so auto-cycle has something to rotate
        text = 'SPEC.md has been updated'
        prompt = 'Please update the spec'
        model = 'claude-haiku-4-5'; effort = $null; minutesAgo = 15; ctx = 185000; win = 200000
    }
)

function MultiSessionResponse($requestedId) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $sessions = $multiSessions | ForEach-Object {
        [ordered]@{
            id = $_.id; project = $_.project; title = $_.title
            status = $_.status; updated_at = $now - ($_.minutesAgo * 60)
        }
    }

    $selected = $null
    if ($requestedId) { $selected = $multiSessions | Where-Object { $_.id -eq $requestedId } | Select-Object -First 1 }
    if (-not $selected) { $selected = $multiSessions[0] }  # auto winner = most recent

    return [ordered]@{
        ok = $true
        session_id = $selected.id
        project = $selected.project
        title = $selected.title
        updated_at = $now - ($selected.minutesAgo * 60)
        status = $selected.status
        activity = $selected.activity
        text = $selected.text
        text_at = $now - ($selected.minutesAgo * 60)
        prompt = $selected.prompt
        prompt_at = $now - ($selected.minutesAgo * 60)
        model = $selected.model
        effort = $selected.effort
        context_tokens = $selected.ctx
        context_window = $selected.win
        error = $null
        question = $null
        sessions = $sessions
    }
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add('http://127.0.0.1:8977/')
$listener.Start()
Write-Host "mock session server on http://127.0.0.1:8977/session.json  (scene: $scene)"

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath
    $res = $ctx.Response
    $res.Headers.Add('Access-Control-Allow-Origin', '*')
    $res.Headers.Add('Cache-Control', 'no-store')

    if ($path -like '/scene/*') {
        $scene = $path.Substring(7)
        Write-Host "scene -> $scene"
        $body = "scene: $scene"
        $res.ContentType = 'text/plain; charset=utf-8'
    }
    elseif ($path -eq '/session.json') {
        if ($scene -eq 'multi') {
            $body = MultiSessionResponse $ctx.Request.QueryString['session'] | ConvertTo-Json -Depth 8
        } else {
            $body = Scene $scene | ConvertTo-Json -Depth 8
        }
        $res.ContentType = 'application/json; charset=utf-8'
    }
    elseif ($ctx.Request.HttpMethod -eq 'GET') {
        # Serve the widget itself so the page runs from an http:// origin; opening it
        # as file:// leaves fetch blocked and the layout untestable.
        $rel = if ($path -eq '/') { 'index.html' } else { $path.TrimStart('/') }
        $file = Join-Path (Split-Path $PSScriptRoot -Parent) $rel
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $res.ContentType = switch ([IO.Path]::GetExtension($file)) {
                '.html' { 'text/html; charset=utf-8' }
                '.css'  { 'text/css; charset=utf-8' }
                '.svg'  { 'image/svg+xml' }
                default { 'application/octet-stream' }
            }
            $bytes = [IO.File]::ReadAllBytes($file)
            $res.ContentLength64 = $bytes.Length
            $res.OutputStream.Write($bytes, 0, $bytes.Length)
            $res.Close()
            continue
        }
        $res.StatusCode = 404
        $res.Close()
        continue
    }
    else {
        # /answer and /heartbeat just get acknowledged
        Write-Host "$($ctx.Request.HttpMethod) $path"
        $res.StatusCode = 204
        $res.Close()
        continue
    }

    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        $res.ContentLength64 = $bytes.Length
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
        $res.Close()
    } catch {
        Write-Host "  (client dropped)"
    }
}
