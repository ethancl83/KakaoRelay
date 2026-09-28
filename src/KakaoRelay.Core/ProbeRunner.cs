using System.Diagnostics;

namespace KakaoRelay.Core;

public static class ProbeRunner
{
    public static async Task<DiagnosticReport> RunAsync(string executable, string outputPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        // A per-run worker file prevents stale reports from being mistaken for this run.
        var workerPath = outputPath + $".{Guid.NewGuid():N}.worker.json";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--worker");
        process.StartInfo.ArgumentList.Add(workerPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        DiagnosticReport report;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("진단 프로세스를 시작할 수 없습니다.");
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                report = LoadPartial(workerPath);
                report.Status = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
                report.Warnings.Add("응답이 없는 진단 작업만 종료했습니다. 카카오톡은 종료하지 않았습니다.");
                return Finish(outputPath, report);
            }
            report = LoadPartial(workerPath);
            if (process.ExitCode != 0 || report.Status == "running")
            {
                report.Status = "error";
                report.Warnings.Add($"진단 프로세스 종료 코드: {process.ExitCode}");
            }
            return Finish(outputPath, report);
        }
        catch (Exception error)
        {
            report = LoadPartial(workerPath);
            report.Status = error is OperationCanceledException ? "cancelled" : "error";
            report.Warnings.Add($"진단 실행 오류: {error.GetType().Name}");
            return Finish(outputPath, report);
        }
        finally
        {
            foreach (var temporaryPath in new[] { workerPath, workerPath + ".tmp" })
                try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static DiagnosticReport LoadPartial(string path)
    {
        try { return ReportStore.Load(path) ?? new DiagnosticReport(); }
        catch { return new DiagnosticReport { Warnings = ["부분 진단 파일을 읽을 수 없습니다."] }; }
    }
    private static DiagnosticReport Finish(string path, DiagnosticReport report)
    {
        report.FinishedAt ??= DateTimeOffset.Now;
        foreach (var window in report.Windows.Where(w => w.AutomationStatus == "running")) window.AutomationStatus = "interrupted";
        ReportStore.Save(path, report);
        return report;
    }
}
