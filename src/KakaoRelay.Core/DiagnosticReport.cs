using System.Text.Json;
using System.Text.Json.Serialization;

namespace KakaoRelay.Core;

public sealed class DiagnosticReport
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public string Stage { get; set; } = "starting";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string ProbeArchitecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public int SessionId { get; set; } = System.Diagnostics.Process.GetCurrentProcess().SessionId;
    public bool ReadOnly { get; set; } = true;
    public bool MessageContentsCollected { get; set; }
    public bool BackgroundInputTested { get; set; }
    public bool SendingTested { get; set; }
    public string ForegroundWindowBefore { get; set; } = "";
    public string ForegroundWindowAfter { get; set; } = "";
    public List<ProcessSnapshot> Processes { get; set; } = [];
    public List<WindowSnapshot> Windows { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    [JsonIgnore] public int InputCandidateCount => Windows.Sum(w => w.Controls.Count(c => c.IsInputCandidate));
    [JsonIgnore] public int NativeInputCandidateCount => Windows.Sum(w => w.NativeChildren.Count(c => c.IsInputCandidate));
    [JsonIgnore] public int InvokeCandidateCount => Windows.Sum(w => w.Controls.Count(c => c.InvokeAvailable));
    [JsonIgnore] public int ControlCount => Windows.Sum(w => w.Controls.Count);
    [JsonIgnore] public string Summary => ReportAssessment.Summarize(this);
}

public sealed record ProcessSnapshot(int Id, int SessionId, string Version);
public sealed class WindowSnapshot
{
    public string Handle { get; set; } = "";
    public int ProcessId { get; set; }
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public bool Visible { get; set; }
    public bool Minimized { get; set; }
    public bool Enabled { get; set; }
    public string AutomationStatus { get; set; } = "pending";
    public List<NativeControlSnapshot> NativeChildren { get; set; } = [];
    public List<AutomationControlSnapshot> Controls { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "제목 없는 창" : Title;
    [JsonIgnore] public string DisplayState => !Visible ? "숨김" : Minimized ? "최소화" : "표시됨 · 가려짐 여부 미판정";
    [JsonIgnore] public string DisplayAutomationStatus => AutomationStatus switch
    {
        "complete" => "탐색 완료", "pending" => "대기", "running" => "탐색 중",
        "skipped-hidden" => "숨김 · 생략", "skipped-limit" => "개수 제한", "limited" => "일부 탐색",
        "stale" => "창 변경됨", "interrupted" => "중단", "error" => "탐색 오류", _ => AutomationStatus
    };
}
public sealed record NativeControlSnapshot(string Handle, string ClassName, bool Visible, bool Enabled)
{
    public bool IsInputCandidate => ClassName.Contains("edit", StringComparison.OrdinalIgnoreCase);
}
public sealed class AutomationControlSnapshot
{
    public int Depth { get; set; }
    public string ControlType { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string FrameworkId { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Offscreen { get; set; }
    public bool Password { get; set; }
    public bool KeyboardFocusable { get; set; }
    public bool ValueAvailable { get; set; }
    public bool? ValueReadOnly { get; set; }
    public bool TextAvailable { get; set; }
    public bool InvokeAvailable { get; set; }
    public bool IsInputCandidate => !Password && (ControlType == "Edit" || (ValueAvailable && ValueReadOnly == false));
    [JsonIgnore] public string Patterns => string.Join(" · ", new[] { ValueAvailable ? "Value" : null, TextAvailable ? "Text" : null, InvokeAvailable ? "Invoke" : null }.OfType<string>());
    [JsonIgnore] public string CandidateLabel => IsInputCandidate ? "입력 후보" : InvokeAvailable ? "호출 후보" : "—";
}

public static class ReportAssessment
{
    public static string Summarize(DiagnosticReport report)
    {
        if (report.Status == "timeout") return "응답 지연으로 진단을 종료했습니다. 수집된 결과만 표시합니다.";
        if (report.Status == "cancelled") return "진단을 중지했습니다. 수집된 결과만 표시합니다.";
        if (report.Status == "error") return "진단을 완료하지 못했습니다. 상세 기록을 확인해 주세요.";
        if (report.Processes.Count == 0) return "현재 사용자 세션에서 카카오톡을 찾지 못했습니다.";
        if (report.Windows.Count == 0) return "카카오톡은 실행 중이지만 창을 찾지 못했습니다.";
        if (report.InputCandidateCount > 0) return "입력 후보를 발견했습니다. 본문 입력창 식별과 실제 동작 검증이 필요합니다.";
        if (report.NativeInputCandidateCount > 0) return "Win32 입력 후보가 있습니다. 지원 메시지와 실제 동작은 미검증입니다.";
        return "자동화 입력 후보를 찾지 못했습니다. 창 상태와 컨트롤 구조를 추가 확인해야 합니다.";
    }
}

public static class ReportStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Save(string path, DiagnosticReport report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporaryPath, fullPath, true);
    }
    public static DiagnosticReport? Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize<DiagnosticReport>(File.ReadAllText(path), JsonOptions) : null;
}
