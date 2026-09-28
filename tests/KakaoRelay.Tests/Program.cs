using KakaoRelay.Core;
using System.Diagnostics;
using System.Text.Json;

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
    Console.WriteLine($"All {checks} checks passed.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { Directory.Delete(root, recursive: true); }
