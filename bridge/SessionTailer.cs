using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeMeter;

/// <summary>One transcript file's relevant tail state, memoized by (path, mtime, length) so an
/// unchanged file is never re-read.</summary>
sealed record TailInfo(
    DateTimeOffset? HumanPromptAt,
    string? Prompt,
    DateTimeOffset? PromptAt,
    string? Title,
    string? Text,
    DateTimeOffset? TextAt,
    string? Model,
    string? Effort,
    long? ContextTokens,
    long? ContextWindow,
    string Status,
    string? Activity,
    string SessionId,
    string Project,
    DateTimeOffset UpdatedAt,
    bool HasBridgeSession,
    AskInfo? Ask,
    IReadOnlyList<StepInfo> Steps,
    DateTimeOffset? QuietSince,
    string? Cwd = null);

// one tool call of the current turn (Step Feed); End null = still running
sealed record StepInfo(string Name, string? Label, DateTimeOffset Start, DateTimeOffset? End);

sealed record SessionInfo(bool Ok, TailInfo? Tail, string? Error);

sealed record AskOption(string Label, string? Description);

// the Pending Ask this instant (see CONTEXT.md) — an unmatched AskUserQuestion/ExitPlanMode
// tool_use, already stripped of markdown and truncated for the widget
sealed record AskInfo(string Id, string Question, string? Header, IReadOnlyList<AskOption> Options);

/// <summary>Figures out which Claude Code session the human is actually driving right now, by
/// tailing the most recently touched transcripts under ~/.claude/projects. Scheduled/background
/// tasks also write transcripts, so "newest file" alone is not enough — the winner is whichever
/// candidate has the most recent genuine human-typed prompt (type=="user" with a promptSource
/// property; tool-result "user" lines lack it). But a headless `claude -p` run also writes a
/// promptSource line, so that alone can hijack the screen from the session actually open in the
/// desktop app. Sessions attached to the desktop UI additionally contain a "bridge-session" line,
/// so those are preferred; only when none of the candidates have one (older transcripts predate
/// that feature) does selection fall back to the plain newest-human-prompt rule.</summary>
static class SessionTailer
{
    // 256 KB held only ~27 lines of a real working session (tool results and base64 screenshots
    // are fat), which is minutes — the human prompt scrolled out while the user was still sitting
    // there. CarryForward below covers the extremes; this just makes the common case not need it.
    const int TailBytes = 1024 * 1024;
    const int MaxCandidates = 8;
    // 1200 cut a normal answer in half. At ~330 characters per screenful this is about a dozen
    // taps of the pager to read from the top — which is fine, because nobody reads from the top:
    // the widget lands you at the bottom, and paging up is the deliberate act.
    const int MaxTextChars = 4000;
    // for a very long prompt the first sentence is the gist: cut the tail, not the head (the opposite of MaxTextChars)
    const int MaxPromptChars = 400;
    // the EDGE screen follows whoever typed last; a session nobody has touched in half an
    // hour is not something the user is actively cycling back to look at
    static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(30);
    const int MaxActiveSessions = 5;
    const int MaxSteps = 6;
    const int MaxLabelChars = 80;
    // an open turn (no end_turn) counts as working for at most this long, measured from the latest line
    // ponytail: keeps a session that crashed/closed mid-turn from staying "working" forever
    static readonly TimeSpan OpenTurnCap = TimeSpan.FromMinutes(15);

    // overridable so --selftest can point this at a throwaway tree
    internal static string ProjectsRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    static readonly object CacheLock = new();
    static readonly Dictionary<string, (DateTime lastWriteUtc, long length, TailInfo tail)> FileCache = new();
    static DateTime _lastComputeUtc = DateTime.MinValue;
    static SessionInfo _cached = new(false, null, "no active session");
    static List<TailInfo> _cachedActive = new();

    // the widget polls every 2s; re-scanning 8 files every time is wasteful, so recompute at
    // most once a second (ponytail: a plain lock, not a file watcher — fine at this scale)
    public static SessionInfo Get()
    {
        lock (CacheLock)
        {
            RecomputeIfStale();
            return _cached;
        }
    }

    // the sessions the widget can cycle through — same 1s recompute cache as Get(), never a
    // second scan of the same files
    public static IReadOnlyList<TailInfo> GetAllActive()
    {
        lock (CacheLock)
        {
            RecomputeIfStale();
            return _cachedActive;
        }
    }

    // Both halves of one /session.json response, from ONE lock acquisition. Calling Get() and
    // GetAllActive() separately can straddle a recompute if the thread is descheduled for more
    // than a second in between, and then the winner is missing from its own sessions list —
    // which is exactly what Compute() returning a tuple exists to prevent.
    public static (SessionInfo Info, IReadOnlyList<TailInfo> Active) GetSnapshot()
    {
        lock (CacheLock)
        {
            RecomputeIfStale();
            return (_cached, _cachedActive);
        }
    }

    static void RecomputeIfStale()
    {
        var now = DateTime.UtcNow;
        if (now - _lastComputeUtc < TimeSpan.FromSeconds(1))
            return;

        (_cached, _cachedActive) = Compute();
        _lastComputeUtc = now;
    }

    // test-only: drop both caches so a freshly-pointed ProjectsRoot is picked up immediately
    internal static void ResetCacheForTest()
    {
        lock (CacheLock)
        {
            FileCache.Clear();
            GoalReader.ResetForTest();
            _lastComputeUtc = DateTime.MinValue;
        }
    }

    static (SessionInfo, List<TailInfo>) Compute()
    {
        List<string> candidates;
        try
        {
            if (!Directory.Exists(ProjectsRoot))
                return (new SessionInfo(false, null, "no active session"), new List<TailInfo>());

            // one level deep only — *.jsonl directly under each project-slug dir. This is what
            // excludes "<slug>\<session>\subagents\*.jsonl" without an explicit filter.
            candidates = Directory.GetDirectories(ProjectsRoot)
                .SelectMany(dir => Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxCandidates)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"session scan failed: {ex.Message}");
            return (new SessionInfo(false, null, "no active session"), new List<TailInfo>());
        }

        var tails = new List<TailInfo>();
        foreach (var path in candidates)
        {
            try
            {
                tails.Add(GetTail(path));
            }
            catch (Exception ex)
            {
                Log.Warn($"session parse failed for {path}: {ex.Message}");
            }
        }

        // drop cache entries for files no longer among the candidates — a new transcript per
        // session, over a weeks-long-running process, would otherwise leak forever
        lock (CacheLock)
        {
            var candidateSet = new HashSet<string>(candidates);
            foreach (var stale in FileCache.Keys.Where(p => !candidateSet.Contains(p)).ToList())
            {
                FileCache.Remove(stale);
                GoalReader.Forget(stale);
            }
        }

        // "attached to the desktop UI" preference, shared by winner selection and the active
        // list below — the winner is always one of these too, never picked from the fallback
        // pool while an attached one exists
        var pool = PreferBridgeSession(tails);

        TailInfo? winner = null;
        foreach (var tail in pool)
        {
            if (tail.HumanPromptAt is null) continue;
            if (winner is null || tail.HumanPromptAt > winner.HumanPromptAt)
                winner = tail;
        }

        var info = winner is null ? new SessionInfo(false, null, "no active session") : new SessionInfo(true, winner, null);

        var activeSince = DateTimeOffset.UtcNow - ActiveWindow;
        var active = pool
            .Where(t => t.HumanPromptAt is not null && t.HumanPromptAt >= activeSince)
            .OrderByDescending(t => t.HumanPromptAt)
            .Take(MaxActiveSessions)
            .ToList();

        return (info, active);
    }

    // if any candidate has a bridge-session line (attached to the desktop app UI), only those
    // are eligible; otherwise fall back to every candidate — shared so Compute()'s winner and
    // GetAllActive()'s list can never disagree about which pool a session is drawn from
    static List<TailInfo> PreferBridgeSession(List<TailInfo> tails)
    {
        var attached = tails.Where(t => t.HasBridgeSession).ToList();
        return attached.Count > 0 ? attached : tails;
    }

    static TailInfo GetTail(string path)
    {
        var info = new FileInfo(path);
        var lastWriteUtc = info.LastWriteTimeUtc;
        var length = info.Length;

        lock (CacheLock)
        {
            if (FileCache.TryGetValue(path, out var cached) && cached.lastWriteUtc == lastWriteUtc && cached.length == length)
            {
                // Thinking Gap depends on the clock, not the file - a session that died mid-turn never touches the file again,
                // so the 15-minute cap must also be applied at read time, or the cache stays "working" forever
                var t = cached.tail;
                if (t.Status == "working" && t.Activity is null && t.QuietSince is { } q && DateTimeOffset.UtcNow - q >= OpenTurnCap)
                    return t with { Status = "waiting" };
                return t;
            }
        }

        var tail = ParseTail(path, length, lastWriteUtc);

        lock (CacheLock)
        {
            // A fact does not stop being true because the read window moved past the line that
            // carried it. A busy session buries its last human prompt under megabytes of tool
            // output, and a session with no human prompt is dropped from the running entirely —
            // so without this carry-forward the screen silently defects to an older, quieter
            // session and then sits there frozen on hours-old text.
            if (FileCache.TryGetValue(path, out var prev)) tail = CarryForward(prev.tail, tail);
            FileCache[path] = (lastWriteUtc, length, tail);
        }

        return tail;
    }

    // Only the facts that scroll out of the window are remembered. Status, activity, and Ask are
    // deliberately NOT carried (nor Steps/QuietSince): they describe this instant and must go stale. Getting Ask wrong
    // here would leave an answered question stuck on the screen forever — once the tool_result
    // line lands, the next read's `now.Ask` is null and stays null, never patched back from `old`.
    // ponytail: memory lives in the per-file cache, so a bridge restart during a long quiet
    // stretch starts blind again and recovers on the user's next prompt. Persist it to disk only
    // if that turns out to matter.
    static TailInfo CarryForward(TailInfo old, TailInfo now)
    {
        // text/textAt/model/effort are one set of facts (see ParseTail, which commits all four together) -
        // borrow the whole set or none of it, otherwise new text gets paired with a stale effort: Haiku sends no effort,
        // so the screen shows "Haiku 4 5 · high" where the high belongs to the previous Opus turn
        var borrowText = now.Text is null;
        // the user's prompt easily slides out of the 1MB read window in tool-heavy sessions - borrowed separately from text
        // because prompt and text come from different lines and need not move together
        var borrowPrompt = now.Prompt is null;
        var borrowCtx = now.ContextTokens is null;
        return now with
        {
            HumanPromptAt = now.HumanPromptAt ?? old.HumanPromptAt,
            Prompt = borrowPrompt ? old.Prompt : now.Prompt,
            PromptAt = borrowPrompt ? old.PromptAt : now.PromptAt,
            Title = now.Title ?? old.Title,
            Text = borrowText ? old.Text : now.Text,
            TextAt = borrowText ? old.TextAt : now.TextAt,
            Model = borrowText ? old.Model : now.Model,
            Effort = borrowText ? old.Effort : now.Effort,
            // usage is on every assistant line (even a line with only tool_use and no text), so it is read separately from
            // text/model/effort above - it must be borrowed separately for the same reason as prompt
            ContextTokens = borrowCtx ? old.ContextTokens : now.ContextTokens,
            ContextWindow = borrowCtx ? old.ContextWindow : now.ContextWindow,
            HasBridgeSession = now.HasBridgeSession || old.HasBridgeSession,
            Cwd = now.Cwd ?? old.Cwd,
        };
    }

    static TailInfo ParseTail(string path, long length, DateTimeOffset updatedAt)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var project = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";

        DateTimeOffset? humanPromptAt = null;
        string? prompt = null;
        DateTimeOffset? promptAt = null;
        string? title = null;
        string? text = null;
        DateTimeOffset? textAt = null;
        string? model = null;
        string? effort = null;
        long? contextTokens = null;
        string? contextModel = null;
        var toolUses = new List<(string Id, string Name)>();
        var matchedIds = new HashSet<string>();
        var hasBridgeSession = false;
        // input of every AskUserQuestion/ExitPlanMode tool_use block, by id — the only tool
        // names a Pending Ask can come from (see CONTEXT.md), so nothing else needs its input kept
        var askInputs = new Dictionary<string, JsonNode?>();
        var steps = new List<StepInfo>();
        var stepIndex = new Dictionary<string, int>(); // tool_use id -> index in steps
        DateTimeOffset? quietSince = null;
        var turnOpen = false;
        string? cwd = null; // where NEXT-SESSION.md lives (Goal Progress fallback)

        foreach (var line in ReadTailLines(path, length))
        {
            // covers both a bad-JSON line (transcripts can end mid-write) and a well-formed
            // line whose field is the wrong type (e.g. "timestamp":12345) — either must skip
            // just this line, not drop the whole transcript
            try
            {
                var obj = JsonNode.Parse(line) as JsonObject;
                if (obj is null) continue;
                var type = obj["type"]?.GetValue<string>();
                DateTimeOffset? lineTs = null;
                // subagent lines are not Steps and neither open nor close this session's turn
                var side = obj["isSidechain"]?.GetValue<bool>() == true;
                if (obj["cwd"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs.Length > 0) cwd = cs;
                if (type is "assistant" or "user" && TryGetTimestamp(obj, out var lts))
                {
                    lineTs = lts;
                    quietSince = lts;
                }

                // a prompt that starts with a slash command (/grill-with-docs etc.) has no promptSource at all,
                // only origin.kind=human - so a session opened with a slash command would vanish from the screen
                // entirely. origin appears only on lines a human typed (tool_result has none), so it can stand in
                if (type == "user" && (obj.ContainsKey("promptSource") || obj["origin"]?["kind"]?.GetValue<string>() == "human"))
                {
                    var hasTs = TryGetTimestamp(obj, out var ts);
                    if (hasTs) humanPromptAt = ts;

                    // not every promptSource line is typed by a human: the harness injects task-notification
                    // (subagent work finished) and coordinator messages in the exact same shape, differing only in
                    // origin.kind - letting them through would show "<task-notification><task-id>…"
                    // instead of the user's question, which defeats the whole purpose of this feature
                    // older transcripts have no origin at all: accepted, since those versions had no task-notification yet
                    var kind = obj["origin"]?["kind"]?.GetValue<string>();
                    // a task-notification also wakes Claude to continue on its own - it opens the turn too, but does not clear steps
                    if (!side) turnOpen = true;
                    if (kind is not null && kind != "human") continue;

                    // a real human prompt = a new turn, the previous turn's steps can be dropped
                    if (!side)
                    {
                        steps.Clear();
                        stepIndex.Clear();
                    }

                    // content can be either a plain string or an array of blocks - support both
                    var contentNode = obj["message"]?["content"];
                    string? promptText = contentNode switch
                    {
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        JsonArray arr => string.Join("\n",
                            arr.Where(b => b?["type"]?.GetValue<string>() == "text")
                               .Select(b => b!["text"]?.GetValue<string>())
                               .Where(t => !string.IsNullOrEmpty(t))),
                        _ => null,
                    };
                    // never set when the text is empty/null (keep the old value), but humanPromptAt is always set either way
                    if (hasTs && !string.IsNullOrEmpty(promptText))
                    {
                        prompt = promptText;
                        promptAt = ts;
                    }
                }
                else if (type == "bridge-session")
                {
                    hasBridgeSession = true; // no timestamp on this line — presence alone is the signal
                }
                else if (type == "custom-title")
                {
                    title = obj["customTitle"]?.GetValue<string>() ?? title;
                }
                else if (type == "user")
                {
                    foreach (var block in obj["message"]?["content"] as JsonArray ?? new JsonArray())
                    {
                        if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                        var id = block["tool_use_id"]?.GetValue<string>();
                        if (id is null) continue;
                        matchedIds.Add(id);
                        if (lineTs is not null && stepIndex.TryGetValue(id, out var si))
                            steps[si] = steps[si] with { End = lineTs };
                    }
                    if (!side && UserText(obj).StartsWith("[Request interrupted by user", StringComparison.Ordinal))
                        turnOpen = false;
                }
                else if (type == "assistant")
                {
                    string? blockText = null;
                    foreach (var block in obj["message"]?["content"] as JsonArray ?? new JsonArray())
                    {
                        var blockType = block?["type"]?.GetValue<string>();
                        if (blockType == "text")
                        {
                            var t = block!["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(t)) blockText = t;
                        }
                        else if (blockType == "tool_use")
                        {
                            var id = block!["id"]?.GetValue<string>();
                            var name = block["name"]?.GetValue<string>();
                            if (id is not null && name is not null)
                            {
                                toolUses.Add((id, name));
                                // a question is not a Step: the screen already alerts with a card, the human reads it in the app (2026-10-01)
                                if (lineTs is not null && !side && name is not ("AskUserQuestion" or "ExitPlanMode"))
                                {
                                    stepIndex[id] = steps.Count;
                                    steps.Add(new StepInfo(DisplayName(name), StepLabel(block["input"]), lineTs.Value, null));
                                }
                                if (name is "AskUserQuestion" or "ExitPlanMode") askInputs[id] = block["input"];
                            }
                        }
                    }
                    // tool_use = still going (also opens the turn, in case the prompt has slid out of the 1MB window)
                    // end_turn / stop_sequence / max_tokens / refusal = finished · null = says nothing
                    var stop = side ? null : obj["message"]?["stop_reason"]?.GetValue<string>();
                    if (stop == "tool_use") turnOpen = true;
                    else if (stop is not null) turnOpen = false;

                    // usage is on every assistant line, including lines with only tool_use and no text at all -
                    // read separately from text/model/effort below, do not tie them together (deliberately separate)
                    // isSidechain lines belong to a subagent - that usage is its own window, not this session's
                    // skip only the usage, not the whole line: the text/tool_use above must still be counted as before
                    var usage = obj["isSidechain"]?.GetValue<bool>() == true ? null : obj["message"]?["usage"];
                    if (usage is not null)
                    {
                        // read all three fully before adding, like the model/effort pattern below
                        var inputTok = usage["input_tokens"]?.GetValue<long>();
                        var cacheCreateTok = usage["cache_creation_input_tokens"]?.GetValue<long>();
                        var cacheReadTok = usage["cache_read_input_tokens"]?.GetValue<long>();
                        if (inputTok is not null || cacheCreateTok is not null || cacheReadTok is not null)
                        {
                            contextTokens = (inputTok ?? 0) + (cacheCreateTok ?? 0) + (cacheReadTok ?? 0);
                            contextModel = obj["message"]?["model"]?.GetValue<string>();
                        }
                    }

                    if (blockText is not null && TryGetTimestamp(obj, out var ts))
                    {
                        // Read both before committing any of them: GetValue<string> throws if the
                        // field is present but not a string, and the catch below would then leave
                        // the new text paired with the previous line's model.
                        var lineModel = obj["message"]?["model"]?.GetValue<string>();
                        var lineEffort = obj["effort"]?.GetValue<string>(); // top level of the line, not under message
                        text = blockText;
                        textAt = ts;
                        model = lineModel;
                        effort = lineEffort;
                    }
                }
            }
            catch (Exception) { continue; }
        }

        if (text is not null)
        {
            text = Plain(text).Trim();
            // Keep the END, not the beginning. The widget scrolls to the bottom, so whichever end
            // survives the cut is the end the user actually reads — and the last paragraph is
            // where the question or the recommendation lives. Cutting the tail meant the one line
            // worth glancing at was always the line that got thrown away.
            if (text.Length > MaxTextChars) text = "…" + text[^MaxTextChars..];
        }

        if (prompt is not null)
        {
            prompt = SlashCommand.Replace(prompt, m => $"/{m.Groups[1].Value} {m.Groups[2].Value}").Trim();
            prompt = Plain(prompt).Trim();
            // cut the tail, not the head - unlike text above, because for a very long prompt the first sentence is the gist
            if (prompt.Length > MaxPromptChars) prompt = prompt[..MaxPromptChars] + "…";
        }

        var unmatched = toolUses.Where(t => !matchedIds.Contains(t.Id)).ToList();
        var thinking = turnOpen && quietSince is not null && DateTimeOffset.UtcNow - quietSince.Value < OpenTurnCap;
        var status = unmatched.Count > 0 || thinking ? "working" : "waiting";
        var activity = unmatched.Count > 0 ? unmatched[^1].Name : null;

        AskInfo? ask = null;
        var lastAsk = unmatched.LastOrDefault(t => t.Name is "AskUserQuestion" or "ExitPlanMode");
        if (lastAsk.Id is not null)
        {
            // a malformed/missing field here must leave Ask null, never throw out of ParseTail —
            // this runs outside the per-line try/catch above, so it needs its own
            try
            {
                ask = BuildAskInfo(lastAsk.Id, lastAsk.Name, askInputs.GetValueOrDefault(lastAsk.Id));
            }
            catch (Exception) { ask = null; }
        }

        long? contextWindow = contextTokens is null ? null : WindowFor(contextModel);

        return new TailInfo(humanPromptAt, prompt, promptAt, title, text, textAt, model, effort, contextTokens, contextWindow, status, activity, sessionId, project, updatedAt, hasBridgeSession, ask,
            steps.Count > MaxSteps ? steps.GetRange(steps.Count - MaxSteps, MaxSteps) : steps, quietSince, cwd);
    }

    // text of a user line (content is a string or an array of text blocks)
    static string UserText(JsonObject obj) => obj["message"]?["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray arr => string.Join("\n", arr.Select(b => b?["type"]?.GetValue<string>() == "text" ? b["text"]?.GetValue<string>() : null).Where(t => !string.IsNullOrEmpty(t))),
        _ => "",
    };

    // mcp__server__tool_name -> tool_name
    static string DisplayName(string name)
    {
        var i = name.LastIndexOf("__", StringComparison.Ordinal);
        return i >= 0 && i + 2 < name.Length ? name[(i + 2)..] : name;
    }

    // short label saying what this tool does - on any failure returns null, never breaks ParseTail
    static string? StepLabel(JsonNode? input)
    {
        try
        {
            if (input is null) return null;
            string? Str(string key) => input[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

            var label = Str("description");
            if (label is null && (Str("file_path") ?? Str("notebook_path")) is { } file) label = Path.GetFileName(file);
            // do not use the raw command or full URL: anyone walking past can see the EDGE screen, and commands often carry tokens/passwords
            label ??= Str("pattern") ?? (Uri.TryCreate(Str("url"), UriKind.Absolute, out var u) ? u.Host : null)
                      ?? Str("query") ?? Str("skill");
            label ??= (input["questions"] as JsonArray)?.FirstOrDefault()?["header"]?.GetValue<string>();
            if (label is null) return null;

            label = Regex.Replace(label, @"\s+", " ").Trim();
            if (label.Length > MaxLabelChars) label = label[..MaxLabelChars] + "…";
            return label.Length == 0 ? null : label;
        }
        catch (Exception) { return null; }
    }

    // context window size per model (figures from the claude-api skill, 2026-09) - unknown models are assumed to be 200k
    // ponytail: fixed table, no config; edit here when a new model arrives
    static long WindowFor(string? model) =>
        model is not null && (model.Contains("opus-5") || model.Contains("sonnet-5") || model.Contains("fable") || model.Contains("[1m]"))
            ? 1_000_000 : 200_000;

    static AskInfo? BuildAskInfo(string id, string name, JsonNode? input)
    {
        if (input is null) return null;

        string? question;
        string? header;
        var options = new List<AskOption>();

        if (name == "AskUserQuestion")
        {
            var questions = input["questions"] as JsonArray;
            var q0 = questions?.Count > 0 ? questions[0] : null;
            if (q0 is null) return null;

            question = q0["question"]?.GetValue<string>();
            header = q0["header"]?.GetValue<string>();
            if (questions!.Count > 1) header = $"{header} (1/{questions.Count})";

            foreach (var opt in q0["options"] as JsonArray ?? new JsonArray())
            {
                var label = opt?["label"]?.GetValue<string>();
                if (label is null) continue;
                var description = opt!["description"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(description)) description = Plain(description).Trim();
                options.Add(new AskOption(Plain(label).Trim(), string.IsNullOrEmpty(description) ? null : description));
            }
        }
        else // ExitPlanMode
        {
            question = input["plan"]?.GetValue<string>();
            header = "Plan awaiting approval";
        }

        if (string.IsNullOrEmpty(question)) return null;

        question = Plain(question).Trim();
        // cut the tail, keep the head, like prompt above - the first sentence of a question is the gist
        if (question.Length > MaxTextChars) question = question[..MaxTextChars] + "…";

        return new AskInfo(id, question, header, options);
    }

    // The EDGE widget renders plain text, no markdown renderer, so this has to run before the
    // MaxTextChars cut — stripping widget-side can't recover text the bridge already truncated.
    static readonly Regex TableSep = new(@"^\s*\|[\s:|-]+\|\s*$");
    static readonly Regex TableRow = new(@"^\s*\|.*\|\s*$");
    static readonly Regex Heading = new(@"^\s*#{1,6}\s+");
    static readonly Regex Bullet = new(@"^\s*[-*]\s+");
    // One level of nested parens, so a wikipedia-style ...(disambiguation) URL does not end the
    // match early and leave a stray ")" sitting in the visible text.
    static readonly Regex Link = new(@"\[([^\]]+)\]\((?:[^()]|\([^()]*\))*\)");
    static readonly Regex Fence = new(@"^\s*```");
    // a slash command's prompt arrives wrapped in three layers of XML: keep only the command name + the args the human typed
    static readonly Regex SlashCommand = new(
        @"<command-message>[\s\S]*?</command-message>\s*<command-name>/?(.*?)</command-name>\s*(?:<command-args>([\s\S]*?)</command-args>)?",
        RegexOptions.IgnoreCase);
    static readonly Regex Bold = new(@"\*\*(.+?)\*\*");
    static readonly Regex Code = new(@"`([^`]+)`");

    // ponytail: line-at-a-time, so a **bold** or a `code` span that wraps across a newline keeps its
    // markers, and an escaped \| inside a table cell splits the cell. Both are rare in a chat reply
    // and cost a real parser to fix — revisit only if they actually show up on the screen.
    internal static string Plain(string s)
    {
        var outLines = new List<string>();
        var inFence = false;
        foreach (var raw in s.Split('\n'))
        {
            // Inside a ``` fence every rule here would change what the content MEANS, not just how
            // it looks: a diff's leading "-" would become a bullet, a shell comment's "#" would
            // vanish, and column-separating "|" would be re-flowed. Pass those lines through whole.
            if (Fence.IsMatch(raw)) { inFence = !inFence; continue; } // the ``` marker itself is noise
            if (inFence) { outLines.Add(raw); continue; }

            if (TableSep.IsMatch(raw)) continue; // drop the |---|---| separator row entirely

            var line = raw;
            if (TableRow.IsMatch(line))
            {
                var trimmed = line.Trim();
                var cells = trimmed[1..^1].Split('|').Select(c => c.Trim()).Where(c => c.Length > 0);
                line = string.Join(" · ", cells);
            }
            line = Heading.Replace(line, "");
            line = Bullet.Replace(line, "· ");
            line = Link.Replace(line, "$1");
            line = Bold.Replace(line, "$1");
            line = Code.Replace(line, "$1");
            outLines.Add(line);
        }
        return string.Join('\n', outLines);
    }

    static bool TryGetTimestamp(JsonObject obj, out DateTimeOffset ts)
    {
        var s = obj["timestamp"]?.GetValue<string>();
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out ts);
    }

    // never reads more than the last 1MB, even for multi-MB transcripts — MaxCandidates = 8 makes
    // the worst case per Compute() round 8MB, not 2MB
    static IEnumerable<string> ReadTailLines(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = Math.Max(0, length - TailBytes);
        // step back 1 byte, then always discard up to the next newline: if the offset falls mid-line, what is dropped is a line fragment;
        // if the offset falls exactly at a line start, the previous byte is the '\n' closing the prior line, so what is dropped is an empty string;
        // no real line is lost in either case
        stream.Seek(offset > 0 ? offset - 1 : 0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (offset > 0) reader.ReadLine();

        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.Length > 0) yield return line;
    }
}
