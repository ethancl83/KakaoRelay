using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ComposeSession compose = new();
    private bool working, targetReady, shuttingDown;
    private string messageBody = "";
    private ConversationTarget? selectedConversation;
    private TestSendReceipt? currentReceipt, selectedReceipt;
    private string? reportPath;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ConversationTarget> Conversations { get; } = [];
    public ObservableCollection<TestSendReceipt> History { get; } = [];
    public bool Working => working || ChatbotPanel.Busy || ChatbotPanel.BotsRunning || ChatbotPanel.ImagesBusy;
    public bool NotWorking => !working && !shuttingDown;
    public bool CanEdit => !working && !shuttingDown;
    public void PrepareShutdown() { shuttingDown = true; ChatbotPanel.Stop(); ApiStatus = "작업 완료 후 종료 중"; RefreshAll(); }
    public bool ShowSendResult => compose.Submitted || currentReceipt is not null;
    public bool ShowObserveCurrent => currentReceipt is { Status: "needs-review", EnterPosted: true };
    public bool CanSend => CanEdit && targetReady && selectedConversation?.Selectable == true && !string.IsNullOrWhiteSpace(messageBody);
    public bool CanObserveCurrent => !working && currentReceipt is { Status: "needs-review", EnterPosted: true };
    public bool CanObserveHistory => !working && selectedReceipt is { Status: "needs-review", EnterPosted: true };
    public string ApiStatus { get; private set; } = "AI 연결 준비 중";
    public string ApiDocumentation { get; } = ReadBundledDocument("API.md");
    public string OpenApiDocument { get; } = ReadBundledDocument("openapi.json");
    public string ApiCopyStatus { get; private set; } = "내용을 선택해 복사하거나 복사 버튼으로 전체를 복사하세요.";
    public void SetApiStatus(string status) { ApiStatus = status; RefreshAll(); }
    public string ConnectionText { get; private set; } = "열린 대화방을 확인하고 있습니다.";
    public string RecipientHeading => selectedConversation is null ? "메시지 작성" : $"{selectedConversation.Title}에게 보내기";
    public string CharacterCount => $"{messageBody.Length:N0}자";
    public string TargetHint { get; private set; } = "카카오톡에서 대화방을 열고 위에서 선택하세요.";
    public string SendStatus { get; private set; } = "새 메시지";
    public string SendDetail { get; private set; } = "받는 대화방과 본문을 확인한 뒤 보내기를 누르세요.";
    public string HistoryHint { get; private set; } = "대화 확인은 실제 새 말풍선을 확인한 경우에만 기록하세요.";
    public string DiagnosticTitle { get; private set; } = "연결 진단";
    public string DiagnosticSummary { get; private set; } = "문제가 있을 때 창과 자동화 기능을 확인합니다.";
    public List<WindowSnapshot> DiagnosticWindows { get; private set; } = [];
    public List<AutomationControlSnapshot> DiagnosticControls { get; private set; } = [];
    public bool HasReport => reportPath is not null && File.Exists(reportPath);
    public string MessageBody { get => messageBody; set { if (!CanEdit || value == messageBody) return; messageBody = value; compose.Reset(); RefreshAll(); } }
    public ConversationTarget? SelectedConversation
    {
        get => selectedConversation;
        set
        {
            if (selectedConversation == value) return;
            selectedConversation = value; targetReady = false;
            compose.Reset();
            TargetHint = value is null ? "카카오톡에서 대화방을 열고 위에서 선택하세요." : "대화방을 확인하고 있습니다.";
            RefreshAll();
            if (value is not null) _ = CheckTargetAsync(value);
        }
    }
    public TestSendReceipt? SelectedReceipt
    {
        get => selectedReceipt;
        set { selectedReceipt = value; HistoryHint = value is null ? "확인할 기록을 선택하세요." : DescribeReceipt(value); RefreshAll(); }
    }
    public MainWindow()
    {
        InitializeComponent(); DataContext = this;
        Title = "KakaoRelay v0.7.4";
        Loaded += (_, _) => RestoreFixedSize();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(RestoreFixedSize);
        Loaded += async (_, _) => { LoadHistory(); await RefreshRoomsAsync(); };
        ChatbotPanel.ComposeRequested += async (room, text) =>
        {
            if (!CanEdit) return;
            if (!string.IsNullOrWhiteSpace(MessageBody)) { SetApiStatus("기존 작성 내용을 먼저 처리한 뒤 AI 답변을 가져오세요."); return; }
            await RefreshRoomsAsync();
            var matches = Conversations.Where(r => r.Title == room.Title && r.Selectable).ToList();
            if (matches.Count != 1) { SetApiStatus("AI 답변을 받을 카카오톡 방을 열고 다시 가져오세요."); return; }
            MessageBody = text; SelectedConversation = matches[0]; MainTabs.SelectedIndex = 1;
        };
    }
    internal void RestoreFixedSize()
    {
        // Keep the requested logical size after native DPI/restore size suggestions.
        if (WindowState != WindowState.Normal) return;
        Width = MinWidth;
        Height = MinHeight;
    }
    public void InitializeAi(AiService ai, Func<ApiSendCommand, Task<TestSendReceipt>> send) => ChatbotPanel.Initialize(ai, send);
    private void Refresh([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void RefreshAll() => Refresh(string.Empty);
    private void SetWorking(bool value) { working = value; RefreshAll(); }
    private async void RefreshRooms_Click(object sender, RoutedEventArgs e) => await RefreshRoomsAsync();
    private async Task RefreshRoomsAsync()
    {
        if (!CanEdit) return;
        SetWorking(true);
        try
        {
            SelectedConversation = null;
            var rooms = await Task.Run(ConversationCatalog.Scan);
            Conversations.Clear(); foreach (var room in rooms) Conversations.Add(room);
            ConnectionText = rooms.Count == 0 ? "열린 대화방이 없습니다." : $"열린 대화방 {rooms.Count}개";
            TargetHint = rooms.Count == 0 ? "카카오톡에서 대화방을 열고 ↻를 눌러주세요." : "받는 대화방을 선택하세요.";
        }
        catch { TargetHint = ConnectionText = "대화방을 읽지 못했습니다. ↻를 눌러 다시 확인하세요."; }
        finally { SetWorking(false); }
    }
    private async Task CheckTargetAsync(ConversationTarget target)
    {
        if (working) return;
        SetWorking(true);
        try
        {
            if (!target.Selectable) throw new InvalidOperationException("Recipient window is missing or ambiguous");
            var draft = await Task.Run(() =>
            {
                var probe = new KakaoTestTransport();
                probe.ValidateTarget(new("preflight", target.Title, "preflight", target.ProcessId, target.Handle, DateTimeOffset.Now.AddMinutes(5)));
                return probe.ReadDraft();
            });
            if (selectedConversation != target) return;
            targetReady = draft.Length == 0 || draft == "메시지 입력";
            TargetHint = targetReady ? "보낼 대화방을 확인했습니다." : "카카오톡에 작성 중인 초안이 있습니다. 먼저 해당 초안을 정리하세요.";
        }
        catch (Exception error) { targetReady = false; TargetHint = Friendly(error.Message); }
        finally { SetWorking(false); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSend) return;
        TestSendRequest request;
        try { request = compose.Request ?? compose.Begin(selectedConversation, messageBody, verifiedPlaceholder: true); }
        catch (Exception error) { SendDetail = Friendly(error.Message); RefreshAll(); return; }
        SetWorking(true); SendStatus = "앱이 메시지를 보내는 중"; SendDetail = "대화방과 본문을 다시 확인한 뒤 전송을 요청합니다."; RefreshAll();
        try
        {
            currentReceipt = await Task.Run(() => TestSender.SendOnce(request, new KakaoTestTransport(), TestSender.DefaultLedger));
            SendStatus = currentReceipt.EnterPosted ? "전송 요청 처리 · 대화 확인 필요" : "전송 중단";
            SendDetail = DescribeReceipt(currentReceipt);
            if (compose.Complete(currentReceipt))
            {
                messageBody = "";
            }
            LoadHistory();
        }
        catch (Exception error) { SendStatus = "처리 결과 확인 필요"; SendDetail = Friendly(error.Message) + " 자동으로 다시 보내지 않습니다."; LoadHistory(); }
        finally { SetWorking(false); }
    }
    private void LoadHistory()
    {
        try
        {
            var selectedId = selectedReceipt?.RequestId;
            var items = TestSender.ReadHistory(TestSender.DefaultLedger);
            History.Clear(); foreach (var item in items) History.Add(item);
            SelectedReceipt = items.FirstOrDefault(r => r.RequestId == selectedId);
        }
        catch { HistoryHint = "기록을 읽을 수 없습니다. 이력 새로고침을 눌러주세요."; RefreshAll(); }
    }
    public async Task<TestSendReceipt> SendFromApiAsync(Func<Task<TestSendReceipt>> send)
    {
        if (shuttingDown) throw new ApiFailure(503, "shutting_down", "앱이 종료 중입니다. 같은 요청 ID를 유지하세요.");
        // The dispatcher serializes UI and API work before any native operation starts.
        if (working) throw new ApiFailure(409, "busy", "다른 작업을 처리 중입니다. 같은 요청 ID로 다시 조회하거나 요청하세요.");
        SetWorking(true);
        try
        {
            var receipt = await send();
            currentReceipt = receipt;
            SendStatus = $"AI · {receipt.Recipient} · {receipt.DisplayStatus}";
            SendDetail = DescribeReceipt(receipt);
            LoadHistory();
            return receipt;
        }
        finally { SetWorking(false); }
    }
    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => LoadHistory();
    private void ObserveCurrent_Click(object sender, RoutedEventArgs e) { if (CanObserveCurrent) Observe(currentReceipt!); }
    private void ObserveHistory_Click(object sender, RoutedEventArgs e) { if (CanObserveHistory) Observe(selectedReceipt!); }
    private void Observe(TestSendReceipt receipt)
    {
        try
        {
            TestSender.RecordObserved(receipt.RequestId, TestSender.DefaultLedger, receipt.Fingerprint);
            receipt.Status = "observed-in-chat";
            if (currentReceipt?.RequestId == receipt.RequestId) { currentReceipt.Status = "observed-in-chat"; SendStatus = "대화 확인 완료"; SendDetail = "새 발신 말풍선 확인을 기록했습니다. 상대방 읽음 여부는 별도입니다."; }
            LoadHistory();
        }
        catch (Exception error) { HistoryHint = SendDetail = Friendly(error.Message); }
        RefreshAll();
    }
    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (working || shuttingDown) return;
        SetWorking(true); DiagnosticTitle = "연결 진단 중"; RefreshAll();
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "diagnostics");
            var path = Path.Combine(folder, $"diagnostic-{Guid.NewGuid():N}.json");
            var result = await ProbeRunner.RunAsync(Environment.ProcessPath!, path, TimeSpan.FromSeconds(20));
            ApplyReport(path, result);
        }
        catch (Exception error) { DiagnosticTitle = "진단 중단"; DiagnosticSummary = Friendly(error.Message); }
        finally { SetWorking(false); }
    }
    public void LoadReport(string path)
    {
        MainTabs.SelectedIndex = 2;
        DiagnosticPanel.IsExpanded = true;
        try { ApplyReport(path, ReportStore.Load(path) ?? throw new FileNotFoundException()); }
        catch { DiagnosticTitle = "결과를 열 수 없습니다"; RefreshAll(); }
    }
    private void ApplyReport(string path, DiagnosticReport report)
    {
        reportPath = path; DiagnosticWindows = report.Windows; DiagnosticControls = [];
        DiagnosticTitle = report.Status is "complete" or "partial" ? "진단 완료" : "진단 중단 · 부분 결과";
        DiagnosticSummary = report.Summary;
        RefreshAll(); WindowsGrid.SelectedItem = report.Windows.FirstOrDefault(w => w.Visible);
    }
    private void DiagnosticSelection_Changed(object sender, SelectionChangedEventArgs e) { DiagnosticControls = (WindowsGrid.SelectedItem as WindowSnapshot)?.Controls ?? []; Refresh(nameof(DiagnosticControls)); }
    private void OpenReport_Click(object sender, RoutedEventArgs e) => OpenPath(reportPath);
    private static string ReadBundledDocument(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"KakaoRelay.Docs.{name}")
            ?? throw new InvalidOperationException($"Missing bundled documentation: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private void CopyApiDocs_Click(object sender, RoutedEventArgs e)
    {
        var isOpenApi = (sender as Button)?.Tag as string == "openapi";
        try
        {
            Clipboard.SetText(isOpenApi ? OpenApiDocument : ApiDocumentation);
            ApiCopyStatus = isOpenApi ? "OpenAPI 명세 전체를 복사했습니다." : "API 사용법과 예제 전체를 복사했습니다.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ApiCopyStatus = "클립보드를 사용 중입니다. 복사 버튼을 다시 눌러주세요.";
        }
        Refresh(nameof(ApiCopyStatus));
    }
    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(TestSender.DefaultLedger); OpenPath(TestSender.DefaultLedger); }
    private void OpenPath(string? path)
    {
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { HistoryHint = "파일을 열 수 없습니다."; RefreshAll(); }
    }
    private static string DescribeReceipt(TestSendReceipt receipt) => receipt.Status switch
    {
        "observed-in-chat" => "새 발신 말풍선 확인이 기록되었습니다. 상대방 수신·읽음 여부는 별도입니다.",
        "blocked-before-input" => Friendly(receipt.Detail),
        "needs-review" when receipt.Kind == "image" => receipt.EnterPosted ? "이미지 전송을 요청했습니다. 대화의 새 첨부 이미지를 확인하세요. 자동 재전송하지 않습니다." : "이미지 첨부 결과 확인이 필요합니다. 대화를 확인하고 중복 전송하지 마세요.",
        "needs-review" when receipt.EnterPosted => (receipt.InputCleared == true ? "입력창이 비워졌습니다. " : "입력창 비움을 확인하지 못했습니다. ") + "새 발신 말풍선을 확인한 뒤 ‘대화에서 확인 완료’를 누르세요. 자동 재전송하지 않습니다.",
        "continued-after-visual-review" => "이전 시험에서 남은 초안을 별도 요청으로 이어 처리한 기록입니다.",
        _ => "완료되지 않은 작업 기록입니다. 대화를 먼저 확인하고 중복 전송하지 마세요."
    };
    private static string Friendly(string message)
    {
        if (message.Contains("draft", StringComparison.OrdinalIgnoreCase)) return "카카오톡 입력 내용이 예상과 다릅니다. 기존 초안은 덮어쓰지 않았습니다.";
        if (message.Contains("ambiguous")) return "받는 대화방을 하나로 확인할 수 없습니다. 대화방 목록을 새로고침하세요.";
        if (message.Contains("identity") || message.Contains("changed") || message.Contains("exists") || message.Contains("editor")) return "대화방 또는 입력창 상태가 바뀌었습니다. 목록을 새로고침하세요.";
        if (message.Contains("desktop")) return "PC가 잠겨 있거나 현재 화면을 사용할 수 없습니다.";
        if (message.Contains("modifier")) return "Shift, Ctrl 또는 Alt 키가 눌려 있어 전송을 중단했습니다.";
        if (message.Contains("progress")) return "다른 작업을 처리 중입니다. 잠시 후 다시 확인하세요.";
        return "작업을 완료하지 못했습니다. 발송 이력과 대화방 상태를 확인하세요.";
    }
}
