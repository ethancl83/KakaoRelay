using KakaoRelay.Core;
using System.Diagnostics;
using System.Text.Json;

if (args.Contains("app-server") || args.Contains("--input-format"))
{
    await ResidentCliChecks.Fixture(args);
    return 0;
}

if (args.Contains("--output-last-message"))
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var input = await Console.In.ReadToEndAsync();
    if (input == "HANG") await Task.Delay(Timeout.Infinite);
    await File.WriteAllTextAsync(args[Array.IndexOf(args, "--output-last-message") + 1], input);
    return 0;
}
if (args.Length == 1 && args[0] == "--local-read")
{
    using var reader = new LocalChatReader();
    var rooms = await reader.RoomsAsync();
    Console.WriteLine($"Local rooms: {rooms.Count}; readable: {rooms.Count(r => r.Readable)}");
    foreach (var room in rooms.Where(r => r.Readable))
    { var context = await reader.ReadAsync(room.Profile, room.Id, 500); Console.WriteLine($"Read {context.Messages.Count} messages; newest {context.Messages.LastOrDefault()?.Time:O}"); }
    return 0;
}
if (args.Length == 1 && args[0] == "--ai-smoke")
{
    var answer = await new CliAiRunner().RunAsync(new() { Id = "codex" }, "외부 도구를 사용하지 말고 정확히 '연결 확인'만 답하세요.", 90, CancellationToken.None);
    Console.WriteLine(answer); return 0;
}
if (args.Length == 2 && args[0] == "--ai-room")
{
    using var reader = new LocalChatReader();
    var rooms = await reader.RoomsAsync();
    var matches = rooms.Where(r => r.Title == args[1] && r.Readable).ToList();
    if (matches.Count != 1) { Console.WriteLine($"Exact readable room matches: {matches.Count}"); return 2; }
    var room = matches.Single();
    var service = new AiService(reader, new AiSettingsStore(AiSettingsStore.DefaultPath), new CliAiRunner());
    var progress = new Progress<string>(Console.WriteLine);
    var timer = Stopwatch.StartNew();
    var first = await service.GenerateAsync(new(room.Profile, room.Id, "analyze", "핵심 내용과 미결 사항을 간단하게 정리하세요."), default, progress);
    var firstSeconds = timer.Elapsed.TotalSeconds;
    var second = await service.GenerateAsync(new(room.Profile, room.Id, "chat", "방금 분석한 내용을 한 문장으로 요약하세요."), default, progress);
    var output = Path.GetFullPath("artifacts/private/ai-room-test.json"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Room = room.Title, FirstSeconds = firstSeconds, TotalSeconds = timer.Elapsed.TotalSeconds, First = first, Resumed = second }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS actual analysis: provider={first.Provider}, messages={first.MessageCount}, chars={first.Text.Length}, seconds={firstSeconds:F1}; follow-up chars={second.Text.Length}; report={output}");
    return 0;
}

if (args.Length == 2 && args[0] == "--worker")
{
    var partial = new DiagnosticReport { Stage = "uia:test", Processes = [new(1234, 1, "test")], Windows = [new() { Handle = "0x1234", AutomationStatus = "running" }] };
    ReportStore.Save(args[1], partial);
    if (args[1].Contains("hang-", StringComparison.Ordinal)) { Thread.Sleep(Timeout.Infinite); return 0; }
    if (args[1].Contains("crash-", StringComparison.Ordinal)) return 7;
    partial.Status = "complete";
    partial.Windows[0].AutomationStatus = "complete";
    ReportStore.Save(args[1], partial);
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "KakaoRelay.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; Console.WriteLine($"PASS {description}"); }
try
{
    if (args.Contains("--chat-tools"))
    {
        await ChatToolChecks.RunAsync(root, Check);
        await AiChecks.RunAsync(root, Check);
        await AutoReplyChecks.RunAsync(root, Check);
        Console.WriteLine($"All {checks} chat tool checks passed.");
        return 0;
    }
    if (args.Contains("--api-checks"))
    {
        await ApiChecks.RunAsync(root, Check);
        Console.WriteLine($"All {checks} API checks passed.");
        return 0;
    }
    var exposed = new DiagnosticReport { Status = "complete", Processes = [new(1, 1, "test")], Windows = [new() { Controls = [new() { ControlType = "Edit", ValueAvailable = true, ValueReadOnly = false, InvokeAvailable = true }] }] };
    Check(exposed.InputCandidateCount == 1 && exposed.Summary.Contains("검증"), "Exposed controls remain unverified, not send-ready");
    Check(!exposed.BackgroundInputTested && !exposed.SendingTested && exposed.ReadOnly && !exposed.MessageContentsCollected, "Read-only diagnosis never claims an input or send test");
    exposed.Windows[0].Controls[0].Password = true;
    Check(exposed.InputCandidateCount == 0, "Password controls are excluded from input candidates");
    var path = Path.Combine(root, "report.json");
    ReportStore.Save(path, exposed);
    var restored = ReportStore.Load(path)!;
    Check(restored.Windows.Count == 1 && !File.Exists(path + ".tmp"), "Atomic JSON report survives a round trip");
    using (var parsed = JsonDocument.Parse(File.ReadAllText(path)))
    {
        var control = parsed.RootElement.GetProperty("windows")[0].GetProperty("controls")[0];
        Check(!control.TryGetProperty("name", out _) && !control.TryGetProperty("value", out _) && !control.TryGetProperty("text", out _), "Control schema contains no message text fields");
    }
    var executable = Environment.ProcessPath!;
    var completed = await ProbeRunner.RunAsync(executable, Path.Combine(root, "complete.json"), TimeSpan.FromSeconds(5));
    Check(completed.Status == "complete", "Coordinator receives a successful worker result");
    var timer = Stopwatch.StartNew();
    var timedOut = await ProbeRunner.RunAsync(executable, Path.Combine(root, "hang-timeout.json"), TimeSpan.FromSeconds(3));
    Check(timedOut.Status == "timeout" && timedOut.Windows.Count == 1 && timedOut.Windows[0].AutomationStatus == "interrupted" && timer.Elapsed < TimeSpan.FromSeconds(8), "Hung worker is terminated while partial evidence is preserved");
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
    {
        var cancelled = await ProbeRunner.RunAsync(executable, Path.Combine(root, "hang-cancel.json"), TimeSpan.FromSeconds(15), cancellation.Token);
        Check(cancelled.Status == "cancelled", "User cancellation differs from timeout");
    }
    var crashed = await ProbeRunner.RunAsync(executable, Path.Combine(root, "crash-worker.json"), TimeSpan.FromSeconds(5));
    Check(crashed.Status == "error" && crashed.Windows.Count == 1, "Worker failure preserves partial data and is not reported as success");
    var stalePath = Path.Combine(root, "stale.json");
    ReportStore.Save(stalePath, new() { Status = "complete", Processes = [new(7777, 1, "old")] });
    var missing = await ProbeRunner.RunAsync(Path.Combine(root, "missing.exe"), stalePath, TimeSpan.FromSeconds(1));
    Check(missing.Status == "error" && missing.Processes.Count == 0, "Previous success is not reused when a new worker cannot start");
    Check(Directory.GetFiles(root, "*.worker.json*").Length == 0, "Worker scratch files are cleaned up");
    TestSendChecks.Run(root, Check);
    WorkspaceChecks.Run(root, Check);
    await ApiChecks.RunAsync(root, Check);
    await AiChecks.RunAsync(root, Check);
    await ChatToolChecks.RunAsync(root, Check);
    await PersonaChecks.RunAsync(root, Check);
    await KnowledgeChecks.RunAsync(root, Check);
    await AutoKnowledgeChecks.RunAsync(root, Check);
    await ReplyModeChecks.RunAsync(root, Check);
    await AutoReplyChecks.RunAsync(root, Check);
    CipherChecks.Run(Check);
    Console.WriteLine($"All {checks} checks passed.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { Directory.Delete(root, recursive: true); }
