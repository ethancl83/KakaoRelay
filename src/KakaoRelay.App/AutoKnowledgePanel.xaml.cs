using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class AutoKnowledgePanel : UserControl, INotifyPropertyChanged
{
    public sealed class RoomChoice
    {
        public required string Profile { get; init; }
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Destination { get; init; }
        public bool Selected { get; set; }
    }
    private AiService? ai;
    private Func<AiSettings>? settings;
    private Func<bool>? save;
    private Func<string>? currentVault;
    private CancellationTokenSource? running, loading;
    private bool stopping;
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Queue<string> statuses = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RoomChoice> Rooms { get; } = [];
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public bool AutoSave { get; set; } = true;
    public bool Busy => running is not null || loading is not null;
    public bool CanConfigure => ai is not null && loading is null && !stopping;
    public bool CanRun => !Busy && !stopping && settings?.Invoke().AutoKnowledge.Enabled == true;
    public string Status { get; private set; } = "자동 정리가 꺼져 있습니다.";
    public AutoKnowledgePanel()
    {
        InitializeComponent(); DataContext = this;
        timer.Tick += async (_, _) => await Run(false);
    }
    public void Initialize(AiService service, Func<AiSettings> getSettings, Func<bool> saveSettings, Func<string> vault)
    {
        ai = service; settings = getSettings; save = saveSettings; currentVault = vault;
        var config = settings().AutoKnowledge;
        Enabled = config.Enabled; IntervalMinutes = config.IntervalMinutes; AutoSave = config.AutoSave;
        foreach (var room in config.Rooms) AddChoice(new(room.Profile, room.RoomId, room.Title, true), true);
        ai.AutomaticKnowledge.StatusChanged += message => Dispatcher.Invoke(() => Log(message));
        Status = Enabled ? "자동 정리 사용 중 · 저장된 처리 위치에서 재개합니다." : "자동 정리가 꺼져 있습니다. 방을 선택하고 설정을 적용하세요.";
        timer.Start(); Changed(); _ = Run(false);
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    private void Log(string message)
    {
        statuses.Enqueue(message); while (statuses.Count > 8) statuses.Dequeue();
        Status = string.Join("\n", statuses.Reverse()); Changed();
    }
    public void Stop() { stopping = true; timer.Stop(); running?.Cancel(); loading?.Cancel(); Changed(); }
    private void AddChoice(LocalRoom room, bool selected)
    {
        var persona = settings!().ResolvePersona(room.Profile, room.Id);
        Rooms.Add(new() { Profile = room.Profile, Id = room.Id, Title = room.Title, Selected = selected,
            Destination = persona.Persona.Name + " · " + (string.IsNullOrWhiteSpace(persona.Knowledge.VaultPath) ? "보관함 연결 필요" : Path.GetFileName(Path.TrimEndingDirectorySeparator(persona.Knowledge.VaultPath))) });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CanConfigure) return;
        using var ct = new CancellationTokenSource(); loading = ct; Changed();
        try
        {
            var previous = Rooms.Where(r => r.Selected).Select(r => AiSettings.RoomKey(r.Profile, r.Id)).ToHashSet();
            var saved = settings!().AutoKnowledge.Rooms;
            var list = await ai!.Chats.RoomsAsync(ct.Token);
            Rooms.Clear();
            foreach (var room in list.Where(r => r.Readable || saved.Any(s => s.Profile == r.Profile && s.RoomId == r.Id)))
                AddChoice(room, previous.Contains(AiSettings.RoomKey(room.Profile, room.Id)) || saved.Any(s => s.Profile == room.Profile && s.RoomId == room.Id));
            foreach (var missing in saved.Where(s => !Rooms.Any(r => r.Profile == s.Profile && r.Id == s.RoomId)))
                AddChoice(new(missing.Profile, missing.RoomId, missing.Title, false), true);
            Log("정리할 방을 체크한 뒤 설정 적용을 누르세요.");
        }
        catch (OperationCanceledException) { Log("방 읽기를 취소했습니다."); }
        catch (Exception error) { Log(error.Message); }
        finally { loading = null; Changed(); }
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!CanConfigure) return;
        try
        {
            var all = settings!(); var old = all.AutoKnowledge;
            var chosen = Rooms.Where(r => r.Selected).ToList();
            var next = new AutoKnowledgeSettings { Enabled = Enabled, AutoSave = AutoSave, IntervalMinutes = IntervalMinutes };
            foreach (var room in chosen)
            {
                if (Enabled && !Directory.Exists(all.ResolvePersona(room.Profile, room.Id).Knowledge.VaultPath)) throw new IOException(room.Title + " 봇의 보관함을 먼저 연결하세요.");
                var existing = old.Enabled ? old.Rooms.FirstOrDefault(r => r.Profile == room.Profile && r.RoomId == room.Id) : null;
                next.Rooms.Add(existing ?? new(room.Profile, room.Id, room.Title, Guid.NewGuid()));
            }
            next.Validate();
            if (Enabled && string.IsNullOrWhiteSpace(all.SelfId)) throw new ArgumentException("AI 챗봇 탭에서 내 카카오 ID를 설정하세요.");
            all.AutoKnowledge = next;
            if (save?.Invoke() != true) { all.AutoKnowledge = old; throw new IOException("설정을 저장하지 못했습니다."); }
            running?.Cancel();
            Log(Enabled ? $"자동 정리 사용 · {chosen.Count}개 방 · {IntervalMinutes}분 주기. 새로 선택한 방은 현재 대화를 시작 기준으로 저장합니다." : "자동 정리를 껐습니다.");
            _ = Run(false);
        }
        catch (Exception error) { Log(error.Message); }
    }
    private async Task Run(bool force)
    {
        if (Busy || stopping || ai is null) return;
        using var ct = new CancellationTokenSource(); running = ct; Changed();
        try { await ai.AutomaticKnowledge.TickAsync(ai.Settings.Load(), force, ct.Token); }
        catch (OperationCanceledException) { Log("자동 정리 작업을 중지했습니다."); }
        catch (Exception error) { Log(error.Message); }
        finally { running = null; Changed(); }
    }
    private async void Run_Click(object sender, RoutedEventArgs e) => await Run(true);
    private void Review_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var vault = currentVault?.Invoke();
            if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault)) throw new IOException("위에서 봇을 선택하고 보관함을 연결하세요.");
            var path = Path.Combine(vault, "검토필요"); Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { Log(error.Message); }
    }
}
