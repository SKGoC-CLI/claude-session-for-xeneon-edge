using System.Text;
using System.Text.Json;

namespace ClaudeMeter;

// Goal Progress (see CONTEXT.md): steps done of the session's step list, and the one being worked on.
// Marks = one letter per step in list order, for the widget's dots: d = done, c = current, p = not started
sealed record Goal(int Done, int Total, string? Current, string Marks);

/// <summary>Goal Progress for one session — ported from Claude Meter's ContextMonitor (v1.11.0) so the
/// popup and the EDGE agree. The step list comes from the transcript's update_plan / TaskCreate /
/// TaskUpdate calls; without one, an active NEXT-SESSION.md in the session's folder stands in.</summary>
static class GoalReader
{
    const int MaxCurrentChars = 120;

    // The plan can sit anywhere in a transcript that grows to tens of MB — far outside the 1 MB
    // tail SessionTailer reads — so each file is read once and then only its new bytes.
    sealed class Plan
    {
        public long Pos;
        public readonly List<(string Text, string Status)> Steps = new();
    }

    static readonly Dictionary<string, Plan> Plans = new(StringComparer.OrdinalIgnoreCase);

    public static Goal? Read(string transcriptPath, string? cwd)
    {
        var goal = ReadPlan(transcriptPath) ?? (string.IsNullOrEmpty(cwd) ? null : ReadNextSession(cwd));
        if (goal?.Current is { } cur)
        {
            cur = SessionTailer.Plain(cur).Replace('\n', ' ').Trim();
            if (cur.Length > MaxCurrentChars) cur = cur[..MaxCurrentChars] + "…";
            goal = goal with { Current = cur.Length == 0 ? null : cur };
        }
        return goal;
    }

    // SessionTailer drops a transcript that left the candidate list; its plan goes with it
    public static void Forget(string transcriptPath)
    {
        lock (Plans) Plans.Remove(transcriptPath);
    }

    internal static void ResetForTest()
    {
        lock (Plans) Plans.Clear();
    }

    static Goal? ReadPlan(string path)
    {
        Plan plan;
        lock (Plans)
            if (!Plans.TryGetValue(path, out plan!)) Plans[path] = plan = new Plan();
        lock (plan)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length == plan.Pos) return ToGoal(plan.Steps); // nothing new since last poll
                if (fs.Length < plan.Pos) { plan.Pos = 0; plan.Steps.Clear(); } // rewritten
                fs.Seek(plan.Pos, SeekOrigin.Begin);
                // 1 MB chunks, so a 37 MB transcript's first read never holds the whole file;
                // only complete lines are consumed — a half-written last line waits for next time
                var buf = new byte[1 << 20];
                var line = new MemoryStream();
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    int start = 0;
                    for (int nl; (nl = Array.IndexOf(buf, (byte)'\n', start, read - start)) >= 0; start = nl + 1)
                    {
                        line.Write(buf, start, nl - start);
                        plan.Pos += line.Length + 1;
                        if (line.Length > 0) ApplyIfPlanLine(plan.Steps, Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                        line.SetLength(0);
                    }
                    line.Write(buf, start, read - start);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // goal is extra: a locked/rotating file must not break /session.json
            }
            return ToGoal(plan.Steps);
        }
    }

    static void ApplyIfPlanLine(List<(string Text, string Status)> steps, string line)
    {
        if (line.Contains("\"tool_use\"") && (line.Contains("update_plan") || line.Contains("wrap_up") || line.Contains("\"name\":\"Task")))
            ApplyPlanLine(steps, line);
    }

    static void ApplyPlanLine(List<(string Text, string Status)> steps, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var b in content.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object || Str(b, "type") != "tool_use"
                    || !b.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;
                string name = Str(b, "name") ?? "";
                if (name.EndsWith("update_plan", StringComparison.Ordinal)) ApplyUpdatePlan(steps, input);
                else if (name.EndsWith("__wrap_up", StringComparison.Ordinal)) // the app checks the list off
                    for (int i = 0; i < steps.Count; i++) { if (steps[i].Status != "deleted") steps[i] = (steps[i].Text, "completed"); }
                else if (name == "TaskCreate")
                    steps.Add((Str(input, "activeForm") ?? Str(input, "subject") ?? "", "pending"));
                else if (name == "TaskUpdate" && int.TryParse(Str(input, "taskId"), out int id) && id >= 1 && id <= steps.Count
                         && Str(input, "status") is { } st)
                    steps[id - 1] = (steps[id - 1].Text, st);
            }
        }
        // a line we can't read just doesn't move the plan; InvalidOperationException =
        // a field of the wrong JSON kind (e.g. "start":"2" makes TryGetInt32 throw)
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
    }

    /// <summary>items = new list; advance = finish the in_progress step, start the next pending; done:[n] / start:n are 1-based.</summary>
    static void ApplyUpdatePlan(List<(string Text, string Status)> steps, JsonElement input)
    {
        if (input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            steps.Clear();
            foreach (var it in items.EnumerateArray())
                if (it.ValueKind == JsonValueKind.Object)
                    steps.Add((Str(it, "content") ?? "", Str(it, "status") ?? "pending"));
            return;
        }
        void Set(int i, string status) { if (i >= 0 && i < steps.Count) steps[i] = (steps[i].Text, status); }
        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True)
        {
            Set(steps.FindIndex(s => s.Status == "in_progress"), "completed");
            Set(steps.FindIndex(s => s.Status == "pending"), "in_progress");
        }
        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
            foreach (var n in done.EnumerateArray())
                if (n.TryGetInt32(out int k)) Set(k - 1, "completed");
        if (input.TryGetProperty("start", out var start) && start.TryGetInt32(out int s1)) Set(s1 - 1, "in_progress");
    }

    static Goal? ToGoal(List<(string Text, string Status)> steps)
    {
        var live = steps.Where(s => s.Status != "deleted").ToList();
        // a finished list is history: hide it (and let an active NEXT-SESSION.md show instead)
        if (live.Count == 0 || live.All(s => s.Status == "completed")) return null;
        int cur = live.FindIndex(s => s.Status == "in_progress");
        if (cur < 0) cur = live.FindIndex(s => s.Status != "completed");
        var marks = string.Concat(live.Select((s, i) => s.Status == "completed" ? 'd' : i == cur ? 'c' : 'p'));
        return new Goal(live.Count(s => s.Status == "completed"), live.Count, live[cur].Text, marks);
    }

    /// <summary>
    /// Fallback for sessions without a step list: the sprint brief NEXT-SESSION.md in the
    /// session's folder, when its "status: active" line says a sprint is running. Table rows
    /// with ⬜ are steps to do, rows with ✅ are done; the first ⬜ row's task cell names the step.
    /// </summary>
    internal static Goal? ReadNextSession(string cwd)
    {
        try
        {
            string path = Path.Combine(cwd, "NEXT-SESSION.md");
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            if (!lines.Take(15).Any(l => l.StartsWith("status: active", StringComparison.OrdinalIgnoreCase))) return null;
            int done = 0, total = 0;
            string? current = null;
            var marks = new StringBuilder();
            foreach (var l in lines.Where(l => l.TrimStart().StartsWith('|')))
            {
                if (l.Contains('⬜'))
                {
                    total++;
                    marks.Append(current is null ? 'c' : 'p');
                    if (current is null)
                    {
                        var cells = l.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
                        current = (cells.Length > 1 ? cells[1] : cells[0]).Replace("`", "").Replace("**", "");
                    }
                }
                else if (l.Contains('✅')) { total++; done++; marks.Append('d'); }
            }
            return total > done ? new Goal(done, total, current, marks.ToString()) : null; // all ✅ = finished, hidden like a done task list
        }
        catch
        {
            return null; // locked mid-save etc.; the next poll tries again
        }
    }

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
