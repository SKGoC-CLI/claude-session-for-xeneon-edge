using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ClaudeMeter;

/// <summary>Immutable snapshot of what the server currently knows, published by the poll
/// loop and read by listener threads via a single volatile reference — no shared mutable
/// state, no locking needed on the read side.</summary>
sealed record BridgeState(bool Ok, UsageSnapshot? Snapshot, bool Stale, string? Error);

static class Program
{
    // mutable (not const) so --selftest can point the listener at its own port instead of
    // the production bridge's 8975 — the real widget in iCUE keeps POSTing to the same
    // port, and those requests would otherwise land on the self-test's own listener
    internal static int Port = 8975;
    static string Prefix => $"http://127.0.0.1:{Port}/";
    const int PollSeconds = 180; // hard floor — the usage endpoint rate-limits aggressively

    static volatile BridgeState _state = new(false, null, false, "Waiting for first poll.");

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selftest"))
            return await RunSelfTestAsync();

        using var mutex = new Mutex(initiallyOwned: true, "Global\\ClaudeEdgeBridge", out bool createdNew);
        if (!createdNew)
            return 0; // another instance already serves this port

        Log.Info("ClaudeEdgeBridge starting.");

        using var http = new HttpClient();
        var client = new UsageClient(http, new CredentialStore());

        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();

        await Task.WhenAll(PollLoopAsync(client), ServeAsync(listener));
        return 0;
    }

    static async Task PollLoopAsync(UsageClient client)
    {
        while (true)
        {
            var wait = TimeSpan.FromSeconds(PollSeconds);
            try
            {
                var snapshot = await client.FetchAsync();
                _state = new BridgeState(true, snapshot, false, null);
            }
            catch (UsageException ex)
            {
                Log.Warn($"poll failed: {ex.Message}");
                _state = KeepStaleOr(ex.Message);
                if (ex.RetryAfter is { } retryAfter && retryAfter > wait)
                    wait = retryAfter; // never poll sooner than the server asked
            }
            catch (Exception ex)
            {
                Log.Error("poll failed", ex);
                _state = KeepStaleOr(ex.Message);
            }

            await Task.Delay(wait);
        }
    }

    // keep the last good snapshot (marked stale) on failure; only surface an error
    // outright when there has never been a successful poll to fall back on
    static BridgeState KeepStaleOr(string error)
    {
        var prev = _state;
        return prev.Snapshot is not null ? prev with { Stale = true } : new BridgeState(false, null, false, error);
    }

    static async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception ex)
            {
                // usually just the listener being stopped/disposed, but log it regardless —
                // if it's ever anything else, this is the only trace that the server went deaf
                Log.Error("accept loop stopped — server will not accept further requests", ex);
                break;
            }

            // dispatch off the accept loop — no handler blocks for long any more (the old
            // ask_on_edge MCP tool used to, up to 30s), but a slow/broken client must still
            // never hold up every other request queued behind it
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(ctx);
                }
                catch (Exception ex)
                {
                    // one slow/broken client must never take down the listener
                    Log.Warn($"request handling failed: {ex}");
                    // backstop: whatever handler failed left the client hanging — try to at
                    // least give it a 500 instead of a dropped connection. Guarded on its own:
                    // the response may already be written/closed, or the client may be gone.
                    try
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.ContentLength64 = 0;
                        ctx.Response.OutputStream.Close();
                    }
                    catch (Exception) { /* response already sent, or client gone */ }
                }
            });
        }
    }

    static Task HandleAsync(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        // session.json carries the user's latest prompt and Claude's reply, so a web page open in
        // any browser must not be able to read it. Only the iCUE widget (file:// sends Origin
        // "null") and local dev pages (the mock on localhost) get CORS; a real website gets none.
        var origin = request.Headers["Origin"];
        if (IsTrustedOrigin(origin))
            response.Headers["Access-Control-Allow-Origin"] = "*";
        else if (RefusedOrigins.TryAdd(origin!, 0))
            Log.Warn($"no CORS for Origin '{origin}' (logged once per origin)");
        response.Headers["Cache-Control"] = "no-store";

        // DNS rebinding: a website whose name resolves to 127.0.0.1 is same-origin and needs no
        // CORS, but its requests still carry its own name in Host — answer only local names
        if (request.Url?.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
        {
            WriteText(response, 403, "Forbidden");
            return Task.CompletedTask;
        }

        switch (request.HttpMethod, request.Url?.AbsolutePath)
        {
            case ("GET", "/usage.json"):
                WriteJson(response, BuildJson(_state));
                break;
            case ("GET", "/session.json"):
            {
                var snapshot = SessionTailer.GetSnapshot();
                WriteJson(response, BuildSessionJson(snapshot.Info, snapshot.Active, request.QueryString["session"]), SessionJsonOptions);
            }
                break;
            case ("POST", "/heartbeat"):
                // the widget still posts these on an interval; nothing reads them any more
                // since the MCP ask_on_edge liveness check was removed (ADR 0002), but the
                // endpoint must keep accepting them so the widget's fetch never errors
                WriteNoContent(response);
                break;
            case ("OPTIONS", "/heartbeat"):
                WriteCorsPreflight(response);
                break;
            default:
                WriteText(response, 404, "Not Found");
                break;
        }
        return Task.CompletedTask;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RefusedOrigins = new();

    // ponytail: "null" is also what a sandboxed iframe sends, so this blocks ordinary websites but
    // not that trick; a per-install secret in the widget would close it if it ever matters.
    // iCUE's widget browser sends "file://" (not "null") — a website can never send a file origin.
    internal static bool IsTrustedOrigin(string? origin) =>
        origin is null or "null"
        || origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    // widget deliberately sends no Content-Type (so the browser skips the CORS preflight) —
    // parse the body as JSON regardless of what Content-Type (if any) is on the request
    static string ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // Thai (and other non-ASCII) text must survive intact — the default encoder escapes it to
    // \uXXXX, which is still correct JSON but pointlessly bloats a local-only payload
    // TypeInfoResolver: JsonArray literals like `new JsonArray { "label" }` box their string
    // elements as `object` at the call site, and .NET 8 requires a resolver on the options
    // instance before it can write those — even though nothing here is ever actually reflected.
    static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    static void WriteJson(HttpListenerResponse response, JsonObject json, JsonSerializerOptions? options = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString(options));
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    static void WriteText(HttpListenerResponse response, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    static void WriteNoContent(HttpListenerResponse response)
    {
        response.StatusCode = 204;
        response.ContentLength64 = 0;
        response.OutputStream.Close();
    }

    static void WriteCorsPreflight(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "*";
        WriteNoContent(response);
    }

    static JsonObject BuildJson(BridgeState state) => new()
    {
        ["ok"] = state.Ok,
        ["fetched_at"] = state.Snapshot?.FetchedAt.ToUnixTimeSeconds(),
        ["stale"] = state.Stale,
        ["five_hour"] = WindowJson(state.Snapshot, "five_hour"),
        ["seven_day"] = WindowJson(state.Snapshot, "seven_day"),
        ["error"] = state.Error,
    };

    // `requestedId` selects which active session the top-level fields describe; absent, empty,
    // or not found among `active` silently falls back to the auto-selected winner — same
    // ok/error as before this feature existed, byte-for-byte, when no ?session= is given at all
    static JsonObject BuildSessionJson(SessionInfo auto, IReadOnlyList<TailInfo> active, string? requestedId)
    {
        TailInfo? tail = auto.Tail;
        var ok = auto.Ok;
        var error = auto.Error;

        if (!string.IsNullOrEmpty(requestedId))
        {
            var match = active.FirstOrDefault(t => t.SessionId == requestedId);
            if (match is not null)
            {
                tail = match;
                ok = true;
                error = null;
            }
        }

        // a Pending Ask wins the screen regardless of requestedId or the auto winner — a question
        // in another project would otherwise sit unseen behind a manually-cycled session, and
        // being seen is the entire point of this feature (ADR 0002). Most recent one if several.
        var asking = active.Where(t => t.Ask is not null).OrderByDescending(t => t.UpdatedAt).FirstOrDefault();
        if (asking is not null)
        {
            tail = asking;
            ok = true;
            error = null;
        }

        var sessions = new JsonArray();
        foreach (var t in active)
        {
            sessions.Add(new JsonObject
            {
                ["id"] = t.SessionId,
                ["project"] = t.Project,
                ["title"] = t.Title,
                ["status"] = t.Status,
                ["updated_at"] = t.UpdatedAt.ToUnixTimeSeconds(),
            });
        }

        return new JsonObject
        {
            ["ok"] = ok,
            ["session_id"] = tail?.SessionId,
            ["project"] = tail?.Project,
            ["title"] = tail?.Title,
            ["updated_at"] = tail?.UpdatedAt.ToUnixTimeSeconds(),
            ["status"] = tail?.Status,
            ["activity"] = tail?.Activity,
            ["text"] = tail?.Text,
            ["text_at"] = tail?.TextAt?.ToUnixTimeSeconds(),
            ["prompt"] = tail?.Prompt,
            ["prompt_at"] = tail?.PromptAt?.ToUnixTimeSeconds(),
            ["model"] = tail?.Model,
            ["effort"] = tail?.Effort,
            ["context_tokens"] = tail?.ContextTokens,
            ["context_window"] = tail?.ContextWindow,
            ["error"] = error,
            ["question"] = BuildQuestionJson(tail),
            ["sessions"] = sessions,
            ["steps"] = BuildStepsJson(tail),
            ["quiet_since"] = tail?.QuietSince?.ToUnixTimeSeconds(),
        };
    }

    static JsonArray BuildStepsJson(TailInfo? tail)
    {
        var arr = new JsonArray();
        foreach (var st in tail?.Steps ?? Array.Empty<StepInfo>())
            arr.Add(new JsonObject
            {
                ["name"] = st.Name,
                ["label"] = st.Label,
                ["start"] = st.Start.ToUnixTimeSeconds(),
                ["end"] = st.End?.ToUnixTimeSeconds(),
            });
        return arr;
    }

    static JsonObject? BuildQuestionJson(TailInfo? tail)
    {
        if (tail?.Ask is not { } ask) return null;

        var options = new JsonArray();
        foreach (var opt in ask.Options)
            options.Add(new JsonObject { ["label"] = opt.Label, ["description"] = opt.Description });

        return new JsonObject
        {
            ["id"] = ask.Id,
            ["question"] = ask.Question,
            ["header"] = ask.Header,
            ["from"] = tail.Project,
            ["options"] = options,
            ["asked_at"] = tail.UpdatedAt.ToUnixTimeSeconds(),
            // no timeout any more — the card stays until answered in the app (ADR 0002); the
            // widget only expires a question when `expires_at` is truthy, so null never expires
            ["expires_at"] = null,
        };
    }

    static JsonObject? WindowJson(UsageSnapshot? snapshot, string key)
    {
        var window = snapshot?.Windows.FirstOrDefault(w => w.Key == key);
        if (window is null) return null;
        return new JsonObject
        {
            ["percent"] = (int)Math.Round(window.Utilization),
            ["resets_at"] = window.ResetsAt?.ToUnixTimeSeconds(),
        };
    }

    static async Task<int> RunSelfTestAsync()
    {
        // own port, not 8975: the production bridge and the live iCUE widget own that port
        // (and using it would force killing the running exe first)
        Port = 8976;

        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        _ = ServeAsync(listener);

        // hand-made snapshot: five_hour has a reset time, seven_day doesn't — exercises
        // both the populated and the "no reset yet" branch of resets_at
        var fakeSnapshot = new UsageSnapshot(
            new[]
            {
                new UsageWindow("five_hour", "Session (5h)", 23.4, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
                new UsageWindow("seven_day", "Weekly", 3.2, null),
            },
            DateTimeOffset.FromUnixTimeSeconds(1_799_999_000));
        _state = new BridgeState(true, fakeSnapshot, false, null);

        var failures = new List<string>();
        try
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/usage.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();

            if (body is null)
                failures.Add("response body did not parse as JSON");
            else
            {
                if (body["ok"]?.GetValue<bool>() != true)
                    failures.Add("ok should be true");
                if (body["five_hour"]?["percent"]?.GetValue<int>() != 23)
                    failures.Add($"five_hour.percent should be 23, was {body["five_hour"]?["percent"]}");
                if (body["five_hour"]?["resets_at"]?.GetValue<long>() != 1_800_000_000)
                    failures.Add($"five_hour.resets_at should be 1800000000, was {body["five_hour"]?["resets_at"]}");
                if (body["seven_day"] is not JsonObject sevenDay)
                    failures.Add("seven_day should be an object");
                else if (sevenDay["resets_at"] is not null)
                    failures.Add($"seven_day.resets_at should be null, was {sevenDay["resets_at"]}");
            }

            var cors = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
                ? values.FirstOrDefault() : null;
            if (cors != "*")
                failures.Add($"Access-Control-Allow-Origin should be '*', was '{cors}'");

            foreach (var (origin, trusted) in new (string?, bool)[]
            {
                (null, true), ("null", true), ("http://localhost:8099", true), ("http://127.0.0.1:8099", true),
                ("https://evil.example", false), ("http://localhost.evil.example", false), ("file://", true),
            })
                if (IsTrustedOrigin(origin) != trusted)
                    failures.Add($"IsTrustedOrigin({origin ?? "<none>"}) should be {trusted}");

            using var webRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/session.json");
            webRequest.Headers.Add("Origin", "https://evil.example");
            using var webResponse = await http.SendAsync(webRequest);
            if (webResponse.Headers.Contains("Access-Control-Allow-Origin"))
                failures.Add("a website origin must not get Access-Control-Allow-Origin");

            using var rebind = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/session.json");
            rebind.Headers.Host = $"evil.example:{Port}";
            using var rebindResponse = await http.SendAsync(rebind);
            if (rebindResponse.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"a foreign Host header should get 403, got {(int)rebindResponse.StatusCode}");
        }
        catch (Exception ex)
        {
            failures.Add($"request failed: {ex.Message}");
        }

        await RunSessionSelfTestAsync(failures);
        await RunSessionSelectionSelfTestAsync(failures);
        await RunMarkdownStripSelfTestAsync(failures);
        await RunStepFeedSelfTestAsync(failures);
        await RunTailTruncationSelfTestAsync(failures);
        await RunBuriedPromptSelfTestAsync(failures);
        await RunCarryForwardSelfTestAsync(failures);
        await RunContextFillSelfTestAsync(failures);
        await RunPromptSelfTestAsync(failures);
        await RunMultiSessionSelfTestAsync(failures);
        await RunAskSelfTestAsync(failures);

        listener.Stop();

        if (failures.Count > 0)
        {
            Console.WriteLine("SELFTEST FAILED:");
            foreach (var f in failures) Console.WriteLine($"  - {f}");
            return 1;
        }

        Console.WriteLine("SELFTEST OK");
        return 0;
    }

    // exercises the session tailer end-to-end against a throwaway transcript tree instead of
    // the real ~/.claude/projects
    static async Task RunSessionSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false); // File.WriteAllLines(..., Encoding.UTF8) would prepend a BOM

        try
        {
            // active.jsonl: has the latest genuine human prompt, but an older mtime — must win
            var activeId = Guid.NewGuid().ToString();
            var activePath = Path.Combine(project, activeId + ".jsonl");
            File.WriteAllLines(activePath, new[]
            {
                """{"type":"custom-title","customTitle":"Test Session in English","timestamp":"2026-08-19T20:30:00.000Z"}""",
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T20:37:34.948Z","message":{"role":"user","content":"Hi test"}}""",
                """{"type":"assistant","timestamp":"2026-08-19T20:37:40.000Z","message":{"role":"assistant","content":[{"type":"text","text":"  Hello, testing  "},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"description":"Build bridge"}}]}}""",
            }, noBom);
            File.SetLastWriteTimeUtc(activePath, DateTime.UtcNow.AddMinutes(-10));

            // noisy.jsonl: newer mtime, but its only "user" line is a tool result (no
            // promptSource) — must NOT win despite being newer on disk
            var noisyPath = Path.Combine(project, Guid.NewGuid() + ".jsonl");
            File.WriteAllLines(noisyPath, new[]
            {
                """{"type":"user","timestamp":"2026-08-19T20:50:00.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_x","content":"ok"}]}}""",
            }, noBom);
            File.SetLastWriteTimeUtc(noisyPath, DateTime.UtcNow);

            // a subagent transcript with an even later human prompt — must be ignored entirely
            // since it sits one directory level too deep to be enumerated
            var subDir = Path.Combine(project, Guid.NewGuid().ToString(), "subagents");
            Directory.CreateDirectory(subDir);
            File.WriteAllLines(Path.Combine(subDir, "sub.jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T23:00:00.000Z","message":{"role":"user","content":"ignore me"}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                if (body is null)
                    failures.Add("session response body did not parse as JSON");
                else
                {
                    if (body["ok"]?.GetValue<bool>() != true)
                        failures.Add("session ok should be true");
                    if (body["session_id"]?.GetValue<string>() != activeId)
                        failures.Add($"session_id should be the file with the newest human prompt, was {body["session_id"]}");
                    if (body["title"]?.GetValue<string>() != "Test Session in English")
                        failures.Add($"title should round-trip Thai text unmangled, was {body["title"]}");
                    if (body["status"]?.GetValue<string>() != "working")
                        failures.Add($"status should be 'working' (unmatched tool_use), was {body["status"]}");
                    if (body["activity"]?.GetValue<string>() != "Bash")
                        failures.Add($"activity should be 'Bash', was {body["activity"]}");
                    if (body["text"]?.GetValue<string>() != "Hello, testing")
                        failures.Add($"text should be the trimmed latest assistant text, was {body["text"]}");
                    // message.content here is a plain string, not an array — must be captured too
                    if (body["prompt"]?.GetValue<string>() != "Hi test")
                        failures.Add($"prompt should be the human prompt text (string content), was {body["prompt"]}");
                    if (body["prompt_at"] is null)
                        failures.Add("prompt_at should not be null once a prompt was captured");
                    if (body["question"] is not null)
                        failures.Add($"question should be null when nothing is pending, was {body["question"]}");
                    var st = body["steps"]?.AsArray();
                    if (st?.Count != 1 || st[0]?["name"]?.GetValue<string>() != "Bash"
                        || st[0]?["label"]?.GetValue<string>() != "Build bridge" || st[0]?["end"] is not null)
                        failures.Add($"steps should hold one running Bash step labelled 'Build bridge', was {body["steps"]}");
                }
            }

            // now satisfy the pending tool_use with a matching tool_result, then close the turn
            // (the old timestamps alone would also read 'waiting' via the quiet cap — be explicit)
            File.AppendAllLines(activePath, new[]
            {
                """{"type":"user","timestamp":"2026-08-19T20:37:45.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"done"}]}}""",
                """{"type":"assistant","timestamp":"2026-08-19T20:37:50.000Z","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done"}]}}""",
            }, noBom);
            SessionTailer.ResetCacheForTest();

            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                if (body?["steps"]?.AsArray() is not { Count: 1 } st2 || st2[0]?["end"] is null)
                    failures.Add($"step should have an end once its tool_result landed, was {body?["steps"]}");
                if (body?["status"]?.GetValue<string>() != "waiting")
                    failures.Add($"status should be 'waiting' once every tool_use is matched, was {body?["status"]}");
                if (body?["activity"] is not null)
                    failures.Add($"activity should be null when waiting, was {body?["activity"]}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"session request failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // Step Feed + open-turn status: recent timestamps so the 15-minute quiet cap does not interfere
    static async Task RunStepFeedSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            string Ts(int secAgo) => DateTimeOffset.UtcNow.AddSeconds(-secAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var path = Path.Combine(project, Guid.NewGuid() + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                $$$"""{"type":"user","promptSource":"user","timestamp":"{{{Ts(30)}}}","message":{"role":"user","content":"go"}}""",
                $$$"""{"type":"assistant","timestamp":"{{{Ts(20)}}}","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_m","name":"mcp__srv__do_thing","input":{"file_path":"C:\\x\\y\\SessionTailer.cs"}}]}}""",
                $$$"""{"type":"user","timestamp":"{{{Ts(10)}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_m","content":"ok"}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            using var http = new HttpClient();

            async Task<JsonObject?> Fetch()
            {
                SessionTailer.ResetCacheForTest();
                using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            }

            var body = await Fetch();
            if (body?["status"]?.GetValue<string>() != "working")
                failures.Add($"open turn with recent lines should be 'working' (thinking gap), was {body?["status"]}");
            if (body?["activity"] is not null)
                failures.Add($"activity should be null in the thinking gap, was {body?["activity"]}");
            if (body?["quiet_since"] is null)
                failures.Add("quiet_since should be set");
            var st = body?["steps"]?.AsArray();
            if (st?.Count != 1 || st[0]?["name"]?.GetValue<string>() != "do_thing"
                || st[0]?["label"]?.GetValue<string>() != "SessionTailer.cs")
                failures.Add($"mcp tool should show name 'do_thing' and label 'SessionTailer.cs', was {body?["steps"]}");

            File.AppendAllLines(path, new[]
            {
                $$$"""{"type":"assistant","timestamp":"{{{Ts(2)}}}","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}}""",
            }, noBom);
            body = await Fetch();
            if (body?["status"]?.GetValue<string>() != "waiting")
                failures.Add($"status should be 'waiting' after end_turn, was {body?["status"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"step feed test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // the EDGE widget renders plain text, so a markdown table + bold must not survive into
    // the served /session.json text
    static async Task RunMarkdownStripSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            var id = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project, id + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-20T10:00:00.000Z","message":{"role":"user","content":"table test"}}""",
                """{"type":"assistant","timestamp":"2026-08-20T10:00:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"**Summary**\n| a | b |\n|---|---|\n| 1 | 2 |\n```diff\n- old line\n```"}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            var text = body?["text"]?.GetValue<string>();
            if (text is null || text.Contains('|'))
                failures.Add($"markdown table pipes should be stripped from text, was {text}");
            if (text is null || text.Contains("**"))
                failures.Add($"markdown bold markers should be stripped from text, was {text}");
            // inside a fence the rules would change meaning, not just looks: this "-" is a diff
            // removal marker, and turning it into a bullet says the opposite of what the diff says
            if (text is null || !text.Contains("- old line"))
                failures.Add($"a fenced code block must pass through untouched, was {text}");
            if (text is null || text.Contains("```"))
                failures.Add($"the fence markers themselves should be dropped, was {text}");
        }
        catch (Exception ex)
        {
            failures.Add($"markdown strip test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // covers the new session-selection rule from a throwaway transcript tree: a "bridge-session"
    // line (desktop-app UI sessions only) must win over a newer headless one, and when nothing
    // has that line at all, selection must degrade to the old newest-human-prompt rule
    static async Task RunSessionSelectionSelfTestAsync(List<string> failures)
    {
        var noBom = new UTF8Encoding(false);

        var root1 = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project1 = Path.Combine(root1, "test-project");
        Directory.CreateDirectory(project1);
        try
        {
            var attachedId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project1, attachedId + ".jsonl"), new[]
            {
                """{"type":"bridge-session"}""",
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T20:00:00.000Z","message":{"role":"user","content":"older but has UI"}}""",
            }, noBom);

            File.WriteAllLines(Path.Combine(project1, Guid.NewGuid() + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T23:00:00.000Z","message":{"role":"user","content":"claude -p newer but headless"}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root1;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            if (body?["session_id"]?.GetValue<string>() != attachedId)
                failures.Add($"a bridge-session transcript should win over a newer headless one, was {body?["session_id"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"session selection (bridge-session) test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root1, recursive: true); } catch { /* best effort */ }
        }

        var root2 = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project2 = Path.Combine(root2, "test-project");
        Directory.CreateDirectory(project2);
        try
        {
            var olderId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project2, olderId + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T20:00:00.000Z","message":{"role":"user","content":"older"}}""",
            }, noBom);

            var newerId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project2, newerId + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T23:00:00.000Z","message":{"role":"user","content":"newer"}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root2;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            if (body?["session_id"]?.GetValue<string>() != newerId)
                failures.Add($"with no bridge-session anywhere, the newest human prompt should still win, was {body?["session_id"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"session selection (fallback) test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root2, recursive: true); } catch { /* best effort */ }
        }
    }

    // Which END of an over-long message survives the cut is the whole point: the widget scrolls to
    // the bottom, so keeping the head meant the user was shown the middle of every long answer and
    // never its conclusion.
    static async Task RunTailTruncationSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            // multi-byte filler (3 bytes each in UTF-8) keeps the byte-offset tail read and the
            // non-ASCII round trip through the JSON encoder under test
            var body = "Start " + new string('日', 6000) + " last line";
            var assistant = new JsonObject
            {
                ["type"] = "assistant",
                ["timestamp"] = "2026-08-20T12:00:05.000Z",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = body } },
                },
            };

            File.WriteAllLines(Path.Combine(project, Guid.NewGuid() + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-20T12:00:00.000Z","message":{"role":"user","content":"long"}}""",
                assistant.ToJsonString(),
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var text = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject()?["text"]?.GetValue<string>();

            if (text is null || !text.EndsWith("last line", StringComparison.Ordinal))
                failures.Add("an over-long message must keep its ending, not its beginning");
            if (text is null || !text.StartsWith('…'))
                failures.Add("the cut end should be marked with a leading …");
            if (text is null || text.Contains("Start", StringComparison.Ordinal))
                failures.Add("the beginning of an over-long message should have been cut away");
            if (text is null || !text.EndsWith("日日 last line", StringComparison.Ordinal))
                failures.Add("multi-byte text must survive the tail read and JSON encoding intact");
        }
        catch (Exception ex)
        {
            failures.Add($"tail truncation test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // The failure this covers froze the EDGE screen on 3-hour-old text: an active session buried
    // its last human prompt under a megabyte of tool output, dropped out of the running (a session
    // with no human prompt is skipped), and a quieter older session took the screen.
    static async Task RunBuriedPromptSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            // the quiet rival: older prompt, but nothing ever buries it
            var quietId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project, quietId + ".jsonl"), new[]
            {
                """{"type":"bridge-session"}""",
                """{"type":"user","promptSource":"user","timestamp":"2026-08-20T10:00:00.000Z","message":{"role":"user","content":"quiet"}}""",
                """{"type":"assistant","timestamp":"2026-08-20T10:00:01.000Z","message":{"role":"assistant","content":[{"type":"text","text":"old and quiet"}]}}""",
            }, noBom);

            var busyId = Guid.NewGuid().ToString();
            var busyPath = Path.Combine(project, busyId + ".jsonl");
            File.WriteAllLines(busyPath, new[]
            {
                """{"type":"bridge-session"}""",
                """{"type":"user","promptSource":"user","timestamp":"2026-08-20T11:00:00.000Z","message":{"role":"user","content":"busy"}}""",
                """{"type":"assistant","timestamp":"2026-08-20T11:00:01.000Z","message":{"role":"assistant","content":[{"type":"text","text":"new and busy"}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var before = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await before.Content.ReadAsStringAsync())?.AsObject();
                if (body?["session_id"]?.GetValue<string>() != busyId)
                    failures.Add($"the newer session should win before anything buries its prompt, was {body?["session_id"]}");
            }

            // bury it: enough tool-result noise to push every line above out of the read window
            var filler = """{"type":"user","timestamp":"2026-08-20T11:00:02.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_x","content":"PADDING"}]}}""";
            var pad = new string('x', 4096);
            using (var writer = new StreamWriter(busyPath, append: true, noBom))
                for (var written = 0L; written < 2L * 1024 * 1024; written += pad.Length)
                    writer.WriteLine(filler.Replace("PADDING", pad));

            // the tailer holds its answer for a second, and ResetCacheForTest would throw away the
            // very memory under test, so wait it out instead of clearing
            await Task.Delay(1400);

            using var after = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var afterBody = JsonNode.Parse(await after.Content.ReadAsStringAsync())?.AsObject();
            if (afterBody?["session_id"]?.GetValue<string>() != busyId)
                failures.Add($"a session must not lose the screen just because its prompt scrolled out of the read window, was {afterBody?["session_id"]}");
            if (afterBody?["text"]?.GetValue<string>() != "new and busy")
                failures.Add($"the last text should survive being buried too, was {afterBody?["text"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"buried prompt test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // finding 1 (CarryForward): once a previous parse of this file is cached, a later parse with a
    // new text block but no `effort` field (Haiku doesn't send one) must get Effort = null — not
    // the previous (Opus) turn's effort borrowed across a mismatched model/text pair
    static async Task RunCarryForwardSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            var path = Path.Combine(project, Guid.NewGuid() + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-23T10:00:00.000Z","message":{"role":"user","content":"one"}}""",
                """{"type":"assistant","effort":"high","timestamp":"2026-08-23T10:00:01.000Z","message":{"role":"assistant","model":"claude-opus-5-20260101","content":[{"type":"text","text":"Answer from Opus"}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var first = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await first.Content.ReadAsStringAsync())?.AsObject();
                if (body?["effort"]?.GetValue<string>() != "high")
                    failures.Add($"carry-forward setup: first read should have effort 'high', was {body?["effort"]}");
            }

            // Haiku turn appended: has new text + model, but no top-level effort field at all.
            // Deliberately do NOT ResetCacheForTest here — that would clear FileCache and skip the
            // CarryForward call entirely, defeating the point of this test.
            File.AppendAllLines(path, new[]
            {
                """{"type":"assistant","timestamp":"2026-08-23T10:00:05.000Z","message":{"role":"assistant","model":"claude-haiku-4-5-20260101","content":[{"type":"text","text":"Answer from Haiku"}]}}""",
            }, noBom);
            await Task.Delay(1400); // let SessionTailer.Get()'s 1s recompute cache expire

            using var second = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var afterBody = JsonNode.Parse(await second.Content.ReadAsStringAsync())?.AsObject();
            if (afterBody?["text"]?.GetValue<string>() != "Answer from Haiku")
                failures.Add($"carry-forward: text should be the new Haiku line, was {afterBody?["text"]}");
            if (afterBody?["model"]?.GetValue<string>() != "claude-haiku-4-5-20260101")
                failures.Add($"carry-forward: model should be the new Haiku model, was {afterBody?["model"]}");
            if (afterBody?["effort"] is not null)
                failures.Add($"carry-forward: effort must not be borrowed from the previous Opus turn, was {afterBody?["effort"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"carry-forward test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // usage lives on every assistant line, independent of whether that line carries a text
    // block — this proves context_tokens updates on a tool_use-only line, ignores sidechains,
    // and picks the context window from the model on that same line
    static async Task RunContextFillSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            var path = Path.Combine(project, Guid.NewGuid() + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-09-13T10:00:00.000Z","message":{"role":"user","content":"one"}}""",
                """{"type":"assistant","timestamp":"2026-09-13T10:00:01.000Z","message":{"role":"assistant","model":"claude-opus-5-20260101","content":[{"type":"text","text":"First answer"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":5000,"cache_read_input_tokens":80000,"output_tokens":10}}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var first = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await first.Content.ReadAsStringAsync())?.AsObject();
                if (body?["context_tokens"]?.GetValue<long>() != 85002)
                    failures.Add($"context-fill a: context_tokens should be 85002, was {body?["context_tokens"]}");
                if (body?["context_window"]?.GetValue<long>() != 1_000_000)
                    failures.Add($"context-fill a: context_window should be 1000000 for opus-5, was {body?["context_window"]}");
            }

            // tool_use-only line, no text block at all — usage must still update, and text must
            // stay the first line's (proves usage is read independently of the text commit)
            File.AppendAllLines(path, new[]
            {
                """{"type":"assistant","timestamp":"2026-09-13T10:00:05.000Z","message":{"role":"assistant","model":"claude-opus-5-20260101","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{}}],"usage":{"input_tokens":1,"cache_creation_input_tokens":100,"cache_read_input_tokens":90000}}}""",
            }, noBom);
            await Task.Delay(1400);

            using (var second = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await second.Content.ReadAsStringAsync())?.AsObject();
                if (body?["context_tokens"]?.GetValue<long>() != 90101)
                    failures.Add($"context-fill b: context_tokens should be 90101, was {body?["context_tokens"]}");
                if (body?["text"]?.GetValue<string>() != "First answer")
                    failures.Add($"context-fill b: text should stay the first line's, was {body?["text"]}");
            }

            // haiku turn with its own usage — context_window must follow this line's model, not
            // stay pinned to the earlier opus-5 window
            File.AppendAllLines(path, new[]
            {
                """{"type":"assistant","timestamp":"2026-09-13T10:00:10.000Z","message":{"role":"assistant","model":"claude-haiku-4-5-20251001","content":[{"type":"text","text":"Second answer"}],"usage":{"input_tokens":1,"cache_read_input_tokens":1000}}}""",
            }, noBom);
            await Task.Delay(1400);

            using (var third = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await third.Content.ReadAsStringAsync())?.AsObject();
                if (body?["context_tokens"]?.GetValue<long>() != 1001)
                    failures.Add($"context-fill c: context_tokens should be 1001, was {body?["context_tokens"]}");
                if (body?["context_window"]?.GetValue<long>() != 200_000)
                    failures.Add($"context-fill c: context_window should be 200000 for haiku, was {body?["context_window"]}");
            }

            // isSidechain line with huge usage appended last — must be ignored entirely
            File.AppendAllLines(path, new[]
            {
                """{"type":"assistant","isSidechain":true,"timestamp":"2026-09-13T10:00:15.000Z","message":{"role":"assistant","model":"claude-opus-5-20260101","content":[{"type":"text","text":"subagent"}],"usage":{"input_tokens":999999}}}""",
            }, noBom);
            await Task.Delay(1400);

            using (var fourth = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await fourth.Content.ReadAsStringAsync())?.AsObject();
                if (body?["context_tokens"]?.GetValue<long>() != 1001)
                    failures.Add($"context-fill d: isSidechain line must be ignored, context_tokens was {body?["context_tokens"]}");
            }

            // fresh file, no assistant line at all — both fields must be JSON null
            var emptyId = Guid.NewGuid().ToString();
            var emptyPath = Path.Combine(project, emptyId + ".jsonl");
            File.WriteAllLines(emptyPath, new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-09-13T10:00:20.000Z","message":{"role":"user","content":"three"}}""",
            }, noBom);
            SessionTailer.ResetCacheForTest();

            using (var fifth = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await fifth.Content.ReadAsStringAsync())?.AsObject();
                if (body?["session_id"]?.GetValue<string>() != emptyId)
                    failures.Add($"context-fill e setup: session_id should be the newer empty file, was {body?["session_id"]}");
                if (body?["context_tokens"] is not null)
                    failures.Add($"context-fill e: context_tokens should be null with no assistant line, was {body?["context_tokens"]}");
                if (body?["context_window"] is not null)
                    failures.Add($"context-fill e: context_window should be null with no assistant line, was {body?["context_window"]}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"context-fill test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // message.content as an array of blocks (the normal shape once a prompt carries an image
    // or other attachment alongside text) — must be joined the same as the plain-string shape,
    // and a prompt that scrolls out of the 1MB tail window must still be remembered
    static async Task RunPromptSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            var path = Path.Combine(project, Guid.NewGuid() + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-24T09:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"first line"},{"type":"image","source":{}},{"type":"text","text":"second line"}]}}""",
                """{"type":"assistant","timestamp":"2026-08-24T09:00:01.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Reply"}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var first = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await first.Content.ReadAsStringAsync())?.AsObject();
                // non-text blocks (the image) are skipped, only the text blocks are joined
                if (body?["prompt"]?.GetValue<string>() != "first line\nsecond line")
                    failures.Add($"prompt should join only the text blocks (array content), was {body?["prompt"]}");
                if (body?["prompt_at"] is null)
                    failures.Add("prompt_at should not be null (array content)");
            }

            // append a turn with no promptSource line at all — the earlier prompt must survive
            // via CarryForward, not vanish just because this poll saw none
            File.AppendAllLines(path, new[]
            {
                """{"type":"assistant","timestamp":"2026-08-24T09:00:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Second reply"}]}}""",
            }, noBom);
            await Task.Delay(1400); // let SessionTailer.Get()'s 1s recompute cache expire

            using var second = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var afterBody = JsonNode.Parse(await second.Content.ReadAsStringAsync())?.AsObject();
            if (afterBody?["prompt"]?.GetValue<string>() != "first line\nsecond line")
                failures.Add($"carry-forward: prompt must survive a read with no promptSource line, was {afterBody?["prompt"]}");
            if (afterBody?["text"]?.GetValue<string>() != "Second reply")
                failures.Add($"carry-forward: text should still update to the new line, was {afterBody?["text"]}");

            // the harness injects subagent task-notifications as promptSource lines too — same
            // shape, only origin.kind differs. Letting one through puts "<task-notification>…"
            // on the screen instead of what the human asked.
            File.AppendAllLines(path, new[]
            {
                """{"type":"user","promptSource":"sdk","origin":{"kind":"task-notification"},"timestamp":"2026-08-24T09:00:09.000Z","message":{"role":"user","content":[{"type":"text","text":"<task-notification>Task finished</task-notification>"}]}}""",
            }, noBom);
            await Task.Delay(1400);

            using var third = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var notifBody = JsonNode.Parse(await third.Content.ReadAsStringAsync())?.AsObject();
            if (notifBody?["prompt"]?.GetValue<string>() != "first line\nsecond line")
                failures.Add($"a task-notification must not become the shown prompt, was {notifBody?["prompt"]}");

            // a prompt that starts with a slash command has no promptSource at all, only origin.kind=human -
            // if it is not accepted, a session opened with /grill vanishes from the screen entirely (no HumanPromptAt)
            File.AppendAllLines(path, new[]
            {
                """{"type":"user","origin":{"kind":"human"},"timestamp":"2026-08-24T09:00:12.000Z","message":{"role":"user","content":"<command-message>grill-with-docs</command-message>\n<command-name>/grill-with-docs</command-name>\n<command-args>read this file</command-args>"}}""",
            }, noBom);
            await Task.Delay(1400);

            using var fourth = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var slashBody = JsonNode.Parse(await fourth.Content.ReadAsStringAsync())?.AsObject();
            if (slashBody?["prompt"]?.GetValue<string>() != "/grill-with-docs read this file")
                failures.Add($"slash-command prompt should show as '/name args', was {slashBody?["prompt"]}");
        }
        catch (Exception ex)
        {
            failures.Add($"prompt test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // covers phase 4: GetAllActive()'s 30-minute window / cap / ordering, and /session.json's
    // ?session= selection (valid id, unknown id, absent) against a throwaway transcript tree
    static async Task RunMultiSessionSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        string Ts(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        string WriteSession(string dir, int minutesAgo, string label)
        {
            var id = Guid.NewGuid().ToString();
            var line = new JsonObject
            {
                ["type"] = "user",
                ["promptSource"] = "user",
                ["timestamp"] = Ts(minutesAgo),
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = label },
            };
            File.WriteAllLines(Path.Combine(dir, id + ".jsonl"), new[] { line.ToJsonString() }, noBom);
            return id;
        }

        try
        {
            // 3 sessions within the 30-minute active window, at distinct recency
            var newestId = WriteSession(project, 1, "newest");
            var middleId = WriteSession(project, 5, "middle");
            var oldestActiveId = WriteSession(project, 20, "oldest-active");
            // outside the window — must be excluded from `sessions`
            var staleId = WriteSession(project, 45, "stale");

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                var sessions = body?["sessions"] as JsonArray;
                var ids = sessions?.Select(s => s?["id"]?.GetValue<string>()).ToList();
                if (ids is null || ids.Count != 3 || ids[0] != newestId || ids[1] != middleId || ids[2] != oldestActiveId)
                    failures.Add($"sessions should be the 3 active entries ordered newest-human-prompt-first, was [{(ids is null ? "null" : string.Join(",", ids))}]");
                if (ids is not null && ids.Contains(staleId))
                    failures.Add("a session whose newest human prompt is older than 30 minutes must be excluded from sessions");
                if (body?["session_id"]?.GetValue<string>() != newestId)
                    failures.Add($"with no ?session=, session_id should be the auto winner, was {body?["session_id"]}");
            }

            // ?session=<valid id> — top-level fields describe that session, not the winner
            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json?session={middleId}"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                if (body?["session_id"]?.GetValue<string>() != middleId)
                    failures.Add($"?session=<valid id> should describe that session, not the winner, was {body?["session_id"]}");
                if (body?["prompt"]?.GetValue<string>() != "middle")
                    failures.Add($"?session=<valid id> should carry that session's own prompt, was {body?["prompt"]}");
                if (body?["ok"]?.GetValue<bool>() != true)
                    failures.Add("?session=<valid id> should report ok:true");
            }

            // ?session=<unknown id> — falls back to the auto winner, HTTP 200, no error
            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json?session=not-a-real-session-id"))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    failures.Add($"?session=<unknown id> should still be HTTP 200, was {(int)response.StatusCode}");
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                if (body?["session_id"]?.GetValue<string>() != newestId)
                    failures.Add($"?session=<unknown id> should fall back to the auto winner, was {body?["session_id"]}");
                if (body?["error"] is not null)
                    failures.Add($"?session=<unknown id> must not surface an error, was {body?["error"]}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"multi-session self-test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        // separate tree: more than 5 active sessions must be capped at 5
        var root2 = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project2 = Path.Combine(root2, "test-project");
        Directory.CreateDirectory(project2);
        try
        {
            for (var i = 0; i < 7; i++)
                WriteSession(project2, i, "s" + i);

            SessionTailer.ProjectsRoot = root2;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            var sessions = body?["sessions"] as JsonArray;
            if (sessions is null || sessions.Count != 5)
                failures.Add($"sessions should be capped at 5 even with 7 active candidates, was {sessions?.Count}");
        }
        catch (Exception ex)
        {
            failures.Add($"session cap self-test failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root2, recursive: true); } catch { /* best effort */ }
        }
    }

    // covers the Pending Ask feature (ADR 0002): a transcript-detected AskUserQuestion/
    // ExitPlanMode surfaces as `question`, clears once answered, and wins the screen across
    // sessions — the old MCP `ask_on_edge` path this replaced is gone entirely, no self-test for it
    static async Task RunAskSelfTestAsync(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var project = Path.Combine(root, "test-project");
        Directory.CreateDirectory(project);
        var noBom = new UTF8Encoding(false);

        try
        {
            var id = Guid.NewGuid().ToString();
            var path = Path.Combine(project, id + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T20:37:34.948Z","message":{"role":"user","content":"Continue working?"}}""",
                """{"type":"assistant","timestamp":"2026-08-19T20:37:40.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Which way?"},{"type":"tool_use","id":"toolu_ask","name":"AskUserQuestion","input":{"questions":[{"question":"What should we do next?","header":"Choose","multiSelect":false,"options":[{"label":"A","description":"First way"},{"label":"B","description":"Second way"}]}]}}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = root;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                var q = body?["question"] as JsonObject;
                if (q is null)
                    failures.Add("an unmatched AskUserQuestion should surface a question");
                else
                {
                    if (q["question"]?.GetValue<string>() != "What should we do next?")
                        failures.Add($"question text should be the AskUserQuestion question, was {q["question"]}");
                    var labels = (q["options"] as JsonArray)?.Select(o => o?["label"]?.GetValue<string>()).ToList();
                    if (labels is null || labels.Count != 2 || labels[0] != "A" || labels[1] != "B")
                        failures.Add($"options should carry both labels, was [{(labels is null ? "null" : string.Join(",", labels))}]");
                }
            }

            // now satisfy the tool_use with a matching tool_result — question must go back to
            // null (this is the regression CarryForward could reintroduce if Ask were carried)
            File.AppendAllLines(path, new[]
            {
                """{"type":"user","timestamp":"2026-08-19T20:37:45.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_ask","content":"A"}]}}""",
            }, noBom);
            SessionTailer.ResetCacheForTest();

            using (var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
                if (body?["question"] is not null)
                    failures.Add($"question should be null once the tool_use is matched, was {body?["question"]}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"ask self-test (AskUserQuestion) failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        // separate tree: an unmatched ExitPlanMode surfaces too, with an empty options array
        var planRoot = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var planProject = Path.Combine(planRoot, "test-project");
        Directory.CreateDirectory(planProject);
        try
        {
            File.WriteAllLines(Path.Combine(planProject, Guid.NewGuid() + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"2026-08-19T20:37:34.948Z","message":{"role":"user","content":"Try planning"}}""",
                """{"type":"assistant","timestamp":"2026-08-19T20:37:40.000Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_plan","name":"ExitPlanMode","input":{"plan":"1. Do A\n2. Do B"}}]}}""",
            }, noBom);

            SessionTailer.ProjectsRoot = planRoot;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            var q = body?["question"] as JsonObject;
            if (q is null)
                failures.Add("an unmatched ExitPlanMode should surface a question");
            else
            {
                if (string.IsNullOrEmpty(q["question"]?.GetValue<string>()))
                    failures.Add("ExitPlanMode question should carry the plan text");
                if (q["options"] is not JsonArray planOptions || planOptions.Count != 0)
                    failures.Add($"ExitPlanMode has no options, should be an empty array, was {q["options"]}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"ask self-test (ExitPlanMode) failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(planRoot, recursive: true); } catch { /* best effort */ }
        }

        // separate tree: two active sessions, the NON-winner (older human prompt) has the
        // pending ask — /session.json must still return the asking session, not the auto winner
        var multiRoot = Path.Combine(Path.GetTempPath(), "ClaudeEdgeBridgeSelfTest-" + Guid.NewGuid());
        var multiProject = Path.Combine(multiRoot, "test-project");
        Directory.CreateDirectory(multiProject);
        try
        {
            // must be inside the 30-minute active window (see SessionTailer.ActiveWindow) so
            // both sessions land in `active` — a fixed past date would fall outside it and drop
            // straight out of the pool this override reads from
            string Ts(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var winnerId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(multiProject, winnerId + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"TS_WINNER","message":{"role":"user","content":"This job has no pending question"}}""".Replace("TS_WINNER", Ts(1)),
            }, noBom);

            var askingId = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(multiProject, askingId + ".jsonl"), new[]
            {
                """{"type":"user","promptSource":"user","timestamp":"TS_ASKING","message":{"role":"user","content":"Another project has a pending question"}}""".Replace("TS_ASKING", Ts(5)),
                """{"type":"assistant","timestamp":"TS_ASKING","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_ask2","name":"AskUserQuestion","input":{"questions":[{"question":"Which one?","header":"Ask","options":[{"label":"A"}]}]}}]}}""".Replace("TS_ASKING", Ts(5)),
            }, noBom);

            SessionTailer.ProjectsRoot = multiRoot;
            SessionTailer.ResetCacheForTest();

            using var http = new HttpClient();
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/session.json");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            if (body?["session_id"]?.GetValue<string>() != askingId)
                failures.Add($"a pending ask in the non-winner session should win the screen, session_id was {body?["session_id"]}");
            if (body?["question"] is null)
                failures.Add("the asking session's question should be surfaced when it wins the screen");
        }
        catch (Exception ex)
        {
            failures.Add($"ask self-test (cross-session override) failed: {ex.Message}");
        }
        finally
        {
            SessionTailer.ResetCacheForTest();
            try { Directory.Delete(multiRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
