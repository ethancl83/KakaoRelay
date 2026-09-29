using System.Diagnostics;
using System.Text.Json;
using KakaoRelay.Core;

internal static class ResidentCliChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Task Emit(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Json));
    public static async Task Fixture(string[] args)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        var claude = args.Contains("--input-format");
        var sid = claude ? args[Array.IndexOf(args, args.Contains("--resume") ? "--resume" : "--session-id") + 1] : "";
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line); var item = document.RootElement;
            JsonElement id = default, p = default;
            var prompt = "";
            if (!claude)
            {
                if (!item.TryGetProperty("method", out var method) || !item.TryGetProperty("id", out id)) continue;
                p = item.GetProperty("params");
                switch (method.GetString())
                {
                    case "initialize": await Emit(new { id, result = new { } }); continue;
                    case "config/read": await Emit(new { id, result = new { config = new { mcp_servers = new { test = new { command = "fake" } }, plugins = new Dictionary<string, object> { ["plugin@fixture"] = new { enabled = true } } } } }); continue;
                    case "thread/start":
                    case "thread/resume":
                        if (p.GetProperty("config").GetProperty("web_search").GetString() != "live") throw new Exception("Live web search not enabled");
                        if (p.GetProperty("config").GetProperty("mcp_servers.test.enabled").GetBoolean()
                            || p.GetProperty("config").GetProperty("plugins.plugin@fixture.enabled").GetBoolean()) throw new Exception("Integrations not disabled");
                        sid = method.GetString() == "thread/start" ? Guid.NewGuid().ToString() : p.GetProperty("threadId").GetString()!;
                        await Emit(new { id, result = new { thread = new { id = sid } } }); continue;
                    case "turn/start": prompt = p.GetProperty("input")[0].GetProperty("text").GetString()!; break;
                    default: await Emit(new { id, error = new { code = -32601, message = "unsupported" } }); continue;
                }
            }
            else
            {
                if (item.GetProperty("type").GetString() != "user") continue;
                prompt = item.GetProperty("message").GetProperty("content").GetString()!;
            }
            if (prompt == "HANG") { await Task.Delay(Timeout.Infinite); return; }
            if (prompt == "CRASH") return;
            if (prompt == "BADJSON") { await Console.Out.WriteLineAsync("invalid JSON"); continue; }
            var stateFile = Path.Combine(Environment.CurrentDirectory, "fixture-" + sid + ".txt");
            int count = File.Exists(stateFile) ? int.Parse(await File.ReadAllTextAsync(stateFile)) : 0;
            await File.WriteAllTextAsync(stateFile, (++count).ToString());
            var answer = JsonSerializer.Serialize(new { pid = Environment.ProcessId, count, prompt }, Json);
            if (claude) await Emit(new { type = "result", subtype = "success", is_error = prompt == "FAIL", session_id = sid, result = answer });
            else
            {
                await Emit(new { id, result = new { turn = new { id = "turn-" + count } } });
                await Emit(new { method = "item/completed", @params = new { threadId = sid, item = new { type = "agentMessage", phase = "commentary", text = "do not send this progress" } } });
                await Emit(new { method = "item/completed", @params = new { threadId = sid, item = new { type = "agentMessage", phase = "final_answer", text = answer } } });
                await Emit(new { method = "turn/completed", @params = new { threadId = sid, turn = new { status = prompt == "FAIL" ? "failed" : "completed" } } });
            }
        }
    }
    private static int Field(string json, string key) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty(key).GetInt32(); }
    private static bool Running(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (var id in new[] { "codex", "claude" })
        {
            var provider = new AiProviderSettings { Id = id, Executable = Environment.ProcessPath!, Model = "fixture", Effort = "high" };
            var folder = Path.Combine(root, "resident", id);
            var runner = new CliAiRunner();
            var first = await runner.RunConversationAsync(provider, "한글 \"따옴표\"\n줄바꿈 $(literal)", 10, default, folder, null);
            var second = await runner.RunConversationAsync(provider, "next", 10, default, folder, first.SessionId);
            var pid = Field(first.Text, "pid");
            check(first.SessionId == second.SessionId && pid == Field(second.Text, "pid") && Field(second.Text, "count") == 2, $"{id} reuses the same live process and session for the next turn");
            using (var doc = JsonDocument.Parse(first.Text)) check(doc.RootElement.GetProperty("prompt").GetString() == "한글 \"따옴표\"\n줄바꿈 $(literal)", $"{id} resident stdin preserves Unicode, quoting and multiline text");
            var other = await runner.RunConversationAsync(provider, "another room", 10, default, folder + "-other", null);
            check(Field(other.Text, "pid") != pid && other.SessionId != first.SessionId && Field(other.Text, "count") == 1, $"{id} rooms have independent resident processes and conversations");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var hung = runner.RunConversationAsync(provider, "HANG", 10, cancel.Token, folder, first.SessionId);
            var independent = await runner.RunConversationAsync(provider, "still works", 10, default, folder + "-other", other.SessionId);
            try { await hung; check(false, "resident cancel"); }
            catch (OperationCanceledException) { check(!Running(pid) && Field(independent.Text, "count") == 2, $"{id} cancelling a turn kills only its own process while another room continues"); }
            var restored = await runner.RunConversationAsync(provider, "resume", 10, default, folder, first.SessionId);
            check(restored.SessionId == first.SessionId && Field(restored.Text, "count") == 3 && Field(restored.Text, "pid") != pid, $"{id} reconnects using the explicit saved session after process termination");
            try { await runner.RunConversationAsync(provider, "HANG", 1, default, folder, first.SessionId); check(false, "resident timeout"); }
            catch (OperationCanceledException) { check(!Running(Field(restored.Text, "pid")), $"{id} timeout terminates its resident process"); }
            foreach (var fault in new[] { "FAIL", "CRASH", "BADJSON" })
            {
                try { await runner.RunConversationAsync(provider, fault, 10, default, folder, first.SessionId); check(false, "resident fault"); }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                { check(true, $"{id} {fault} invalidates the connection without replaying the turn"); }
            }
            var final = await runner.RunConversationAsync(provider, "final", 10, default, folder, first.SessionId);
            var lastPid = Field(final.Text, "pid");
            var activeAtShutdown = runner.RunConversationAsync(provider, "HANG", 10, default, folder, first.SessionId);
            runner.Dispose(); runner.Dispose();
            try { await activeAtShutdown; check(false, "resident shutdown"); }
            catch (OperationCanceledException) { check(true, $"{id} shutdown cancels an in-flight turn"); }
            check(!Running(lastPid) && !Running(Field(other.Text, "pid")), $"{id} disposal stops all owned resident processes");
        }
        var claudeArgs = CliAiRunner.ResidentArguments(new() { Id = "claude", Effort = "high" }, "saved-id", "unused");
        check(claudeArgs.Contains("--input-format") && claudeArgs.Count(x => x == "stream-json") == 2 && claudeArgs.Contains("--resume") && claudeArgs.Contains("saved-id") && claudeArgs.Contains("high") && !claudeArgs.Contains("--no-session-persistence"), "Claude resident arguments preserve effort and persisted explicit resume");
    }
}
