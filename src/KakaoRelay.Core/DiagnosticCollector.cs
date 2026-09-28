using System.Diagnostics;
using System.Windows.Automation;

namespace KakaoRelay.Core;

public static class DiagnosticCollector
{
    // Called only in a disposable worker process on an MTA thread. UIA providers may hang.
    public static DiagnosticReport Collect(string outputPath)
    {
        var report = new DiagnosticReport { ForegroundWindowBefore = NativeWindows.Format(NativeWindows.GetForegroundWindow()) };
        ReportStore.Save(outputPath, report);
        try
        {
            foreach (var process in Process.GetProcessesByName("KakaoTalk"))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != report.SessionId) continue;
                        string version;
                        try { version = process.MainModule?.FileVersionInfo.FileVersion ?? "unknown"; }
                        catch { version = "unavailable"; }
                        report.Processes.Add(new(process.Id, process.SessionId, version));
                    }
                    catch (InvalidOperationException) { report.Warnings.Add("탐색 중 카카오톡 프로세스가 종료되었습니다."); }
                }
            }
            report.Stage = "native-windows";
            report.Windows = NativeWindows.Enumerate(report.Processes.Select(p => p.Id).ToHashSet());
            if (report.Windows.Count >= 64) report.Warnings.Add("최상위 창 탐색이 64개 제한에 도달했습니다.");
            ReportStore.Save(outputPath, report);
            var inspected = 0;
            foreach (var window in report.Windows)
            {
                if (!window.Visible) { window.AutomationStatus = "skipped-hidden"; continue; }
                if (inspected++ >= 12) { window.AutomationStatus = "skipped-limit"; continue; }
                report.Stage = $"uia:{window.Handle}";
                window.AutomationStatus = "running";
                ReportStore.Save(outputPath, report);
                InspectAutomation(window, () => ReportStore.Save(outputPath, report));
                ReportStore.Save(outputPath, report);
            }
            report.Status = report.Warnings.Count > 0 || report.Windows.Any(w => w.Warnings.Count > 0 || w.AutomationStatus is "error" or "limited" or "skipped-limit" or "stale") ? "partial" : "complete";
            report.Stage = "finished";
        }
        catch (Exception error)
        {
            report.Status = "error";
            report.Warnings.Add($"진단 오류: {error.GetType().Name}");
        }
        report.ForegroundWindowAfter = NativeWindows.Format(NativeWindows.GetForegroundWindow());
        report.FinishedAt = DateTimeOffset.Now;
        ReportStore.Save(outputPath, report);
        return report;
    }

    private static void InspectAutomation(WindowSnapshot window, Action checkpoint)
    {
        var handle = NativeWindows.Parse(window.Handle);
        NativeWindows.GetWindowThreadProcessId(handle, out var currentProcessId);
        if (!NativeWindows.IsWindow(handle) || currentProcessId != window.ProcessId) { window.AutomationStatus = "stale"; return; }
        try
        {
            var clock = Stopwatch.StartNew();
            var root = AutomationElement.FromHandle(handle);
            var walker = TreeWalker.RawViewWalker;
            // Iterative depth-first traversal avoids an unbounded child enumeration or recursion.
            var stack = new Stack<(AutomationElement Element, int Depth, bool IncludeSibling)>();
            stack.Push((root, 0, false));
            while (stack.Count > 0 && window.Controls.Count < 250 && clock.Elapsed < TimeSpan.FromSeconds(4))
            {
                var (element, depth, includeSibling) = stack.Pop();
                if (element.Current.ProcessId != window.ProcessId) continue;
                var info = element.Current;
                var entry = new AutomationControlSnapshot
                {
                    Depth = depth, ControlType = info.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                    ClassName = info.ClassName, FrameworkId = info.FrameworkId,
                    Enabled = info.IsEnabled, Offscreen = info.IsOffscreen, Password = info.IsPassword,
                    KeyboardFocusable = info.IsKeyboardFocusable,
                    ValueAvailable = (bool)element.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty),
                    TextAvailable = (bool)element.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty),
                    InvokeAvailable = (bool)element.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)
                };
                if (!entry.Password && entry.ValueAvailable && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                    entry.ValueReadOnly = ((ValuePattern)pattern).Current.IsReadOnly;
                // Intentionally never read Name, Value, TextPattern document text, or selection.
                window.Controls.Add(entry);
                if (window.Controls.Count % 10 == 0) checkpoint();
                if (includeSibling && walker.GetNextSibling(element) is { } sibling) stack.Push((sibling, depth, true));
                if (depth < 16 && walker.GetFirstChild(element) is { } child) stack.Push((child, depth + 1, true));
                else if (depth >= 16) window.Warnings.Add("UIA 탐색 깊이 제한에 도달했습니다.");
            }
            window.AutomationStatus = stack.Count > 0 || window.Warnings.Count > 0 ? "limited" : "complete";
            if (stack.Count > 0) window.Warnings.Add("UIA 탐색 시간 또는 250개 컨트롤 제한에 도달했습니다.");
        }
        catch (Exception error)
        {
            window.AutomationStatus = "error";
            window.Warnings.Add($"UIA 탐색 불가: {error.GetType().Name}");
        }
    }
}
