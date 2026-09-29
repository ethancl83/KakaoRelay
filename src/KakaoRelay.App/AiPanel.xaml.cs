using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class AiPanel : UserControl, INotifyPropertyChanged
{
    private AiService? service;
    private Func<ApiSendCommand, CancellationToken, Task<TestSendReceipt>>? send;
    private CancellationTokenSource? operation;
    private LocalRoom? selectedRoom, answerRoom;
    private string answer = "";
    private LocalRoom? summaryRoom;
    private string? summaryPersonaId;
    public string KnowledgeQuery { get; set; } = "";
    public string KnowledgePreview { get; private set; } = "검색은 로컬에서만 실행하며 AI에 전송하지 않습니다.";
    public bool CanSaveKnowledgeNote => Idle && summaryRoom is not null && summaryPersonaId is not null && !string.IsNullOrWhiteSpace(Answer);
    private bool settingsLoaded, restoringRooms;
    private readonly System.Windows.Threading.DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool generating;
    public Visibility AnswerWaitingVisibility => generating ? Visibility.Visible : Visibility.Collapsed;
    private CancellationTokenSource? bots;
    private int activeBots;
    public ObservableCollection<string> BotStatuses { get; } = [];
    public bool BotsRunning => bots is not null;
    public bool CanStartBots => service is not null && !BotsRunning && Idle;
    private readonly System.Diagnostics.Stopwatch elapsed = new();
    private readonly System.Windows.Threading.DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    public string BotState => BotsRunning ? $"● 자동 답장 가동 중 · {activeBots}/3개 방" : "○ 자동 답장 꺼짐";
    public string BotBadge => BotsRunning ? $"● 자동답장 {activeBots}/3" : "○ 자동답장 꺼짐";
    public int PersonaBindingsRevision { get; private set; }
    public string Activity => Busy ? $"{Status} · {elapsed.Elapsed:mm\\:ss} 경과" : Status;
    public Visibility ProgressVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<LocalRoom, string>? ComposeRequested;
    public AiSettings Settings { get; private set; } = new();
    private PersonaProfile? editingProfile;
    public PersonaProfile? EditingProfile { get => editingProfile; set { if (value is null || PersonaImages.Busy) return; editingProfile = value; KnowledgePreview = "검색은 로컬에서만 실행하며 AI에 전송하지 않습니다."; PersonaImages.SwitchPersona(); Changed(); } }
    public PersonaProfile? RoomProfile { get; set; }
    public string RoomReplyMode { get; set; } = "trigger";
    private readonly List<LocalRoom> botSelectionOrder = [];
    public LocalRoom? BotBindingRoom { get; private set; }
    public bool CanBindBot => Idle && BotBindingRoom is not null;
    private void UpdateBotBindingRoom()
    {
        BotBindingRoom = botSelectionOrder.LastOrDefault();
        RoomProfile = BotBindingRoom is null ? null : Settings.ResolvePersona(BotBindingRoom.Profile, BotBindingRoom.Id);
        RoomReplyMode = BotBindingRoom is null ? Settings.ReplyMode : Settings.ReplyModeForRoom(BotBindingRoom.Profile, BotBindingRoom.Id);
        Changed();
    }
    public bool CanEditPersona => Idle && !PersonaImages.Busy;
    public bool ImagesBusy => PersonaImages.Busy;
    public System.Windows.Media.ImageSource? Avatar { get; private set; }
    public string RoomPersonaHint => SelectedRoom is null ? "연결할 방을 선택하세요." : $"현재 봇: {Settings.ResolvePersona(SelectedRoom.Profile, SelectedRoom.Id).Persona.Name} · 호출어 {Settings.TriggerForRoom(SelectedRoom.Profile, SelectedRoom.Id)}";
    public Dictionary<string, string> ReplyModes { get; } = new() { ["immediate"] = "즉시 답변", ["trigger"] = "호출어 답변", ["context"] = "맥락 판단" };
    public string[] ProviderChoices { get; } = ["auto", "codex", "grok", "claude"];
    public string[] EffortChoices { get; } = ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "ultracode"];
    public ObservableCollection<LocalRoom> Rooms { get; } = [];
    public LocalRoom? SelectedRoom { get => selectedRoom; set { selectedRoom = value; if (settingsLoaded && !restoringRooms) { Settings.SelectedChatRoom = value is null ? null : AiSettings.RoomKey(value.Profile, value.Id); QueueSave(); } Transcript = ""; ContextHint = "대화 읽기로 문맥을 확인하세요."; Changed(); } }
    public string Question { get; set; } = "";
    public string Answer { get => answer; set { answer = value; Changed(); } }
    public string Transcript { get; private set; } = "";
    public string ContextHint { get; private set; } = "카카오톡에서 원하는 방을 열고 방 새로고침을 누르세요.";
    public string Status { get; private set; } = "설정 준비 중";
    public string CliInfo { get; private set; } = "";
    public string SelfId { get => Settings.SelfId; set { Settings.SelfId = value; QueueSave(); } }
    public string Trigger { get => Settings.Trigger; set { Settings.Trigger = value; QueueSave(); } }
    public bool Busy => operation is not null;
    public bool Idle => !Busy && service is not null && settingsLoaded;
    public bool CanRead => Idle && SelectedRoom is not null;
    public bool CanCompose => Idle && answerRoom is not null && !string.IsNullOrWhiteSpace(Answer);
    public AiPanel()
    {
        InitializeComponent(); DataContext = this; ticker.Tick += (_, _) => Changed();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); if (settingsLoaded) Save(); };
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueSave()));
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueSave()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        BotRooms.SelectionChanged += (_, change) =>
        {
            if (!settingsLoaded || restoringRooms) return;
            foreach (var room in change.RemovedItems.OfType<LocalRoom>()) botSelectionOrder.Remove(room);
            foreach (var room in change.AddedItems.OfType<LocalRoom>()) { botSelectionOrder.Remove(room); botSelectionOrder.Add(room); }
            Settings.SelectedBotRooms = botSelectionOrder.Select(r => AiSettings.RoomKey(r.Profile, r.Id)).ToList();
            UpdateBotBindingRoom();
            QueueSave();
        };
    }
    private void QueueSave() { if (!settingsLoaded || restoringRooms) return; saveTimer.Stop(); saveTimer.Start(); }
    public void Initialize(AiService ai, Func<ApiSendCommand, CancellationToken, Task<TestSendReceipt>> sender)
    {
        service = ai; send = sender;
        try { Settings = ai.Settings.Load(); settingsLoaded = true; Status = "준비"; }
        catch (Exception e) { Status = "설정 읽기 실패: " + e.Message; }
        Settings.EnsurePersonas(); editingProfile = Settings.Personas[0];
        foreach (var profile in Settings.Personas) profile.Persona.PropertyChanged += (_, _) => { PersonaBindingsRevision++; QueueSave(); Changed(); };
        PersonaImages.AvatarChanged += avatar => { Avatar = avatar; Changed(); };
        PersonaImages.PropertyChanged += (_, _) => Changed();
        PersonaImages.Initialize(() => EditingProfile!, Save, () => Settings);
        Detect(); Changed();
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    private bool Save()
    {
        if (!settingsLoaded) return false;
        try { service!.Settings.Save(Settings); return true; }
        catch (Exception e) { Status = e.Message; Changed(); return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) { if (Idle && Save()) { Status = "페르소나·프로바이더·지식베이스 설정을 저장했습니다."; Changed(); } }
    private void KnowledgeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || EditingProfile is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "봇이 참고할 옵시디언 보관함 또는 하위 폴더 선택" };
        if (dialog.ShowDialog() != true) return;
        EditingProfile.Knowledge.VaultPath = dialog.FolderName;
        Save(); Changed();
    }
    private void KnowledgeOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = EditingProfile?.Knowledge.VaultPath;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) throw new InvalidOperationException("먼저 보관함 폴더를 선택하세요.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.GetFullPath(path)) { UseShellExecute = true });
        }
        catch (Exception error) { Status = error.Message; Changed(); }
    }
    private async void KnowledgeSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || EditingProfile is null || !Save()) return;
        var settings = new KnowledgeSettings { Enabled = true, VaultPath = EditingProfile.Knowledge.VaultPath };
        var query = KnowledgeQuery;
        await Work(async ct =>
        {
            var result = await ObsidianKnowledge.SearchAsync(settings, query, ct);
            KnowledgePreview = result.Excerpts.Count == 0 ? "관련 노트를 찾지 못했습니다. 노트에 있는 주제어나 제목으로 검색하세요."
                : string.Join("\n\n", result.Excerpts.Select(x => $"[노트: {x.Source}] · 발췌 {x.Part}\n{x.Text}"));
            Status = $"노트 {result.Notes}개 · 관련 발췌 {result.Excerpts.Count}개 · 건너뜀 {result.Skipped}개{(result.Limited ? " · 검색 한도 도달: 더 작은 폴더를 선택하세요." : "")}";
        });
    }
    private async void SaveKnowledgeNote_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSaveKnowledgeNote || !Save()) return;
        var room = summaryRoom!;
        var knowledge = Settings.Personas.Single(p => p.Id == summaryPersonaId).Knowledge;
        var body = Answer;
        await Work(async ct =>
        {
            var path = await ObsidianKnowledge.SaveNoteAsync(knowledge, room.Title + " 대화 요약", body, room, ct);
            Status = "요약 노트 저장: " + path;
            summaryRoom = null; summaryPersonaId = null;
        });
    }
    private void BindPersona_Click(object sender, RoutedEventArgs e)
    {
        if (!CanBindBot || RoomProfile is null || BotBindingRoom is not { } room || !BotRooms.SelectedItems.Contains(room)) return;
        Settings.RoomPersonas[AiSettings.RoomKey(room.Profile, room.Id)] = RoomProfile.Id;
        Settings.RoomReplyModes[AiSettings.RoomKey(room.Profile, room.Id)] = RoomReplyMode;
        if (Save()) Status = $"{room.Title} · {RoomProfile.Persona.Name} · {ReplyModes[RoomReplyMode]} 저장됨";
        PersonaBindingsRevision++;
        Changed();
    }
    private void Detect() { CliInfo = string.Join("\n", CliAiRunner.Status(Settings).Select(s => $"{s.Provider}: {s.Detail}")); Changed(); }
    private void Detect_Click(object sender, RoutedEventArgs e) => Detect();
    private async Task Work(Func<CancellationToken, Task> work)
    {
        if (!Idle) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; elapsed.Restart(); ticker.Start(); Changed();
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { Status = "중지했습니다."; }
        catch (Exception error) { Status = "실패: " + error.Message; }
        finally { operation = null; generating = false; ticker.Stop(); elapsed.Stop(); Status += $" · {elapsed.Elapsed.TotalSeconds:F1}초"; Changed(); }
    }
    private async void Rooms_Click(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        Status = "카카오톡 로컬 대화방 확인 중"; Changed();
        var list = await service!.Chats.RoomsAsync(ct);
        ApplyRooms(list);
        Status = $"{list.Count}개 방 · 키 확인 {list.Count(r => r.Readable)}개. 읽을 수 없는 방은 카카오톡에서 열어주세요.";
    });
    private void ApplyRooms(List<LocalRoom> list)
    {
        var previous = SelectedRoom;
        restoringRooms = true;
        try
        {
            Rooms.Clear(); foreach (var r in list) Rooms.Add(r);
            SelectedRoom = list.FirstOrDefault(r => r.Id == previous?.Id && r.Profile == previous.Profile)
                ?? list.FirstOrDefault(r => AiSettings.RoomKey(r.Profile, r.Id) == Settings.SelectedChatRoom) ?? list.FirstOrDefault(r => r.Readable);
            botSelectionOrder.Clear();
            foreach (var key in Settings.SelectedBotRooms)
            {
                var room = list.FirstOrDefault(r => AiSettings.RoomKey(r.Profile, r.Id) == key);
                if (room is null || botSelectionOrder.Contains(room)) continue;
                botSelectionOrder.Add(room); BotRooms.SelectedItems.Add(room);
            }
            UpdateBotBindingRoom();
        }
        finally { restoringRooms = false; }
    }
    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRead || !Save()) return; var room = SelectedRoom!;
        await Work(async ct =>
        {
            Status = "로컬 대화 읽는 중"; Changed();
            var context = await service!.Chats.ReadAsync(room.Profile, room.Id, Settings.ContextMessages, ct);
            ApplyRooms(Rooms.Select(r => r.Profile == room.Profile && r.Id == room.Id ? context.Room : r).ToList());
            Transcript = string.Join("\n\n", context.Messages.Select(m => $"{m.Time.ToOffset(TimeSpan.FromHours(9)):MM-dd HH:mm} · {m.Author} ({m.AuthorId})\n{(m.Deleted ? "[삭제된 메시지]" : m.Text)}"));
            ContextHint = $"{context.Messages.Count}개 메시지 · 한국 시간 · 첨부 미디어 제외";
            Status = "로컬 대화 읽기 완료 · 아직 AI에 전달하지 않았습니다.";
        });
    }
    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRead || !Save()) return;
        var room = SelectedRoom!; var mode = (string)((Button)sender).Tag;
        await Work(async ct =>
        {
            generating = true; answerRoom = null; summaryRoom = null; summaryPersonaId = null; Answer = ""; Status = "대화 문맥으로 AI 응답 생성 중"; Changed();
            var progress = new Progress<string>(message => { if (operation?.Token == ct) { Status = message; Changed(); } });
            var result = await service!.GenerateAsync(new(room.Profile, room.Id, mode, Question), ct, progress);
            ApplyRooms(Rooms.Select(r => r.Profile == room.Profile && r.Id == room.Id ? result.Room : r).ToList());
            Answer = result.Text; answerRoom = mode == "reply" ? result.Room : null;
            summaryRoom = mode == "analyze" ? result.Room : null;
            summaryPersonaId = mode == "analyze" ? result.PersonaId : null;
            Status = $"{result.Provider} · {result.Model} · effort {result.Effort} · {result.MessageCount}개 문맥 · " + string.Join(" → ", result.Attempts.Select(a => $"{a.Provider} {a.Status}"));
            if (result.ImagePath is not null) Status += " · 생성 이미지 저장: " + result.ImagePath;
            if (result.ImageError is not null) Status += " · 이미지 생성 실패: " + result.ImageError;
        });
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRead || !Save()) return; var room = SelectedRoom!;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "KakaoRelay 대화 JSON|*.json", FileName = "kakao-conversation.json" };
        if (dialog.ShowDialog() != true) return;
        await Work(async ct =>
        {
            var context = await service!.Chats.ReadAsync(room.Profile, room.Id, Settings.ContextMessages, ct);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(context, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = PromptJson.Encoder }), ct);
            Status = "선택한 문맥을 JSON으로 저장했습니다. macOS·Linux 버전에서 가져올 수 있습니다.";
        });
    }
    private void Compose_Click(object sender, RoutedEventArgs e) { if (CanCompose) ComposeRequested?.Invoke(answerRoom!, Answer); }
    private async void StartBot_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartBots || !Save() || send is null) return;
        var selected = BotRooms.SelectedItems.Cast<LocalRoom>().ToList();
        if (selected.Count is < 1 or > 3) { Status = "자동 답장할 방을 1~3개 선택하세요."; Changed(); return; }
        if (!System.Numerics.BigInteger.TryParse(SelfId.Trim(), out var self) || self <= 0) { Status = "내 사용자 ID를 입력하세요."; Changed(); return; }
        var selfId = SelfId.Trim(); var trigger = Trigger.Trim();
        List<LocalRoom>? prepared = null;
        await Work(async ct =>
        {
            Status = "자동답장 시작 전 · 선택한 방의 대화 읽기와 발송 창 확인 중"; Changed();
            var result = await AutoReplyReadiness.PrepareAsync(service!.Chats, selected, ConversationCatalog.Scan, ct);
            ct.ThrowIfCancellationRequested();
            ApplyRooms(result.Rooms);
            prepared = result.Selected;
        });
        if (prepared is null) return;
        selected = prepared;
        using var cancellation = new CancellationTokenSource(); bots = cancellation; activeBots = selected.Count;
        Status = "자동 답장 시작 · 수동 분석도 사용할 수 있습니다.";
        BotStatuses.Clear(); foreach (var room in selected) BotStatuses.Add($"{room.Title} · 시작 준비 중"); Changed();
        async Task RunRoom(LocalRoom room, int index)
        {
            var ct = cancellation.Token;
            var stage = $"{room.Title} · 시작 준비 중";
            var stageTime = System.Diagnostics.Stopwatch.StartNew();
            var roomTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            roomTimer.Tick += (_, _) => BotStatuses[index] = $"{stage} · {stageTime.Elapsed:mm\\:ss} 경과";
            void SetStage(string message) { stage = message; stageTime.Restart(); BotStatuses[index] = message; }
            roomTimer.Start();
            async Task<TestSendReceipt> SendQueued(ApiSendCommand command)
            {
                SetStage($"{room.Title} · 전송 순서 대기");
                ct.ThrowIfCancellationRequested();
                return await send(command, ct);
            }
            var session = new AutoReplySession(service!, SendQueued);
            session.StatusChanged += message => Dispatcher.Invoke(() => { SetStage(message); Changed(); });
            try { await session.RunAsync(room, selfId, trigger, ct); }
            catch (OperationCanceledException) { BotStatuses[index] = $"{room.Title} · 중지됨"; }
            catch (Exception error) { BotStatuses[index] = $"{room.Title} · 실패로 중지: {error.Message}"; }
            finally { roomTimer.Stop(); activeBots--; Changed(); }
        }
        try { await Task.WhenAll(selected.Select(RunRoom)); }
        finally { bots = null; if (!Busy) Status = "자동 답장 종료 · 방별 결과를 확인하세요."; Changed(); }
    }
    public void Stop() { saveTimer.Stop(); if (settingsLoaded) Save(); operation?.Cancel(); bots?.Cancel(); PersonaImages.Stop(); }
    private void StopBots_Click(object sender, RoutedEventArgs e) => bots?.Cancel();
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
}
