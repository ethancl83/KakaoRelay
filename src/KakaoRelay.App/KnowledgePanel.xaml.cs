using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public partial class KnowledgePanel : UserControl, INotifyPropertyChanged
{
    private Func<AiSettings>? settings;
    private Func<bool>? save;
    private PersonaProfile? profile;
    private KnowledgeNote? selectedNote;
    private CancellationTokenSource? operation;
    private bool stopping;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IEnumerable<PersonaProfile> Profiles => settings?.Invoke().Personas ?? [];
    public ObservableCollection<KnowledgeNote> Notes { get; } = [];
    public bool Busy => operation is not null;
    public bool Idle => settings is not null && !Busy && !stopping;
    public bool HasSelectedNote => selectedNote is not null;
    public string VaultPath { get; set; } = "";
    public bool Enabled { get; set; }
    public string Query { get; set; } = "";
    public string Preview { get; private set; } = "자료를 추가하거나 노트를 선택하세요.";
    public string Status { get; private set; } = "자료는 이 컴퓨터의 보관함에 저장됩니다.";
    public string LibraryHint { get; private set; } = "노트를 불러오세요";
    public string ConnectionHint => profile is null ? "봇을 선택하세요."
        : string.IsNullOrWhiteSpace(profile.Knowledge.VaultPath) ? "연결 설정에서 보관함을 선택하세요."
        : $"{(profile.Knowledge.Enabled ? "● 답변에 사용 중" : "○ 답변에 사용 안 함")} · {Path.GetFileName(Path.TrimEndingDirectorySeparator(profile.Knowledge.VaultPath))}";
    public PersonaProfile? Profile
    {
        get => profile;
        set
        {
            if (value is null || Busy) return;
            profile = value; VaultPath = value.Knowledge.VaultPath; Enabled = value.Knowledge.Enabled;
            Notes.Clear(); SelectedNote = null; Changed();
            if (IsLoaded) _ = ReloadAsync();
        }
    }
    public KnowledgeNote? SelectedNote
    {
        get => selectedNote;
        set
        {
            selectedNote = value; Preview = "노트를 선택하면 내용을 볼 수 있습니다.";
            if (value is not null)
            {
                try
                {
                    for (var dir = new DirectoryInfo(Path.GetDirectoryName(value.Path)!); dir is not null; dir = dir.Parent)
                        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("연결된 폴더는 읽을 수 없습니다.");
                    if (File.GetAttributes(value.Path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("연결된 파일은 읽을 수 없습니다.");
                    using var input = new FileStream(value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (input.Length > ObsidianKnowledge.MaxFileBytes) throw new IOException("노트가 256KB를 넘습니다.");
                    using var reader = new StreamReader(input, Encoding.UTF8, true);
                    var buffer = new char[20000];
                    var count = reader.ReadBlock(buffer, 0, buffer.Length);
                    Preview = new string(buffer, 0, count) + (reader.Peek() >= 0 ? "\n\n… 미리보기 생략. 전체 내용은 옵시디언에서 확인하세요." : "");
                }
                catch (Exception error) { Preview = "노트를 읽을 수 없습니다: " + error.Message; }
            }
            Changed();
        }
    }
    public KnowledgePanel()
    {
        InitializeComponent(); DataContext = this;
        Loaded += async (_, _) => { if (Idle) await ReloadAsync(); };
    }
    public void Initialize(Func<AiSettings> getSettings, Func<bool> saveSettings)
    { settings = getSettings; save = saveSettings; Profile = Profiles.FirstOrDefault(); Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    public void Stop() { stopping = true; operation?.Cancel(); Changed(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (!Idle) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; Changed();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "취소했습니다. 완료된 자료는 보관함에 남아 있습니다."; }
        catch (Exception error) { Status = error.Message; }
        finally { operation = null; Changed(); }
    }
    private string ConnectedVault()
    {
        var path = profile?.Knowledge.VaultPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new IOException("연결 설정에서 보관함을 선택하고 저장하세요.");
        return path;
    }
    private async Task LoadNotes(string vault, CancellationToken cancellation)
    {
        var result = await KnowledgeDocuments.ListAsync(vault, cancellation);
        Notes.Clear(); foreach (var note in result.Notes) Notes.Add(note);
        SelectedNote = null;
        LibraryHint = $"노트 {result.Notes.Count}개" + (result.Skipped > 0 ? $" · 제외 {result.Skipped}개" : "") + (result.Limited ? " · 한도 도달" : "");
        Changed();
    }
    private Task ReloadAsync() => Work(async ct => { await LoadNotes(ConnectedVault(), ct); Status = "노트 목록을 새로 불러왔습니다."; });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "봇이 참고할 옵시디언 보관함 선택" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { VaultPath = dialog.FolderName; Status = "연결 저장을 누르면 적용됩니다."; Changed(); }
    }
    private void CreateVault_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "새 보관함을 만들 상위 폴더 선택" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            VaultPath = Path.Combine(dialog.FolderName, "KakaoRelay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(VaultPath); Enabled = true;
            Status = "보관함을 만들었습니다. 연결 저장을 누르세요."; Changed();
        }
        catch (Exception error) { Status = error.Message; Changed(); }
    }
    private async Task SaveConnection(bool all)
    {
        if (!Idle || profile is null) return;
        await Work(async ct =>
        {
            var candidate = new KnowledgeSettings { VaultPath = VaultPath.Trim(), Enabled = Enabled };
            candidate.Validate();
            if (!string.IsNullOrWhiteSpace(candidate.VaultPath)) await KnowledgeDocuments.ListAsync(candidate.VaultPath, ct);
            var targets = all ? Profiles.ToArray() : [profile];
            var previous = targets.Select(p => p.Knowledge).ToArray();
            for (int i = 0; i < targets.Length; i++) targets[i].Knowledge = new() { VaultPath = candidate.VaultPath, Enabled = candidate.Enabled };
            if (save?.Invoke() != true)
            {
                for (int i = 0; i < targets.Length; i++) targets[i].Knowledge = previous[i];
                throw new IOException("연결을 저장하지 못했습니다. AI 챗봇 탭의 설정 오류를 확인하세요.");
            }
            if (!string.IsNullOrWhiteSpace(candidate.VaultPath)) await LoadNotes(candidate.VaultPath, ct);
            else { Notes.Clear(); SelectedNote = null; LibraryHint = "노트 0개"; }
            Status = all ? "모든 봇에 같은 보관함을 연결했습니다." : "이 봇의 연결 설정을 저장했습니다.";
        });
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveConnection(false);
    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await SaveConnection(true);
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "지식 자료 추가", Multiselect = true, Filter = "지식 자료|*.md;*.txt;*.pdf;*.docx" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await Import(dialog.FileNames);
    }
    private void Files_DragOver(object sender, DragEventArgs e)
    { e.Effects = Idle && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Import(paths);
    }
    private Task Import(string[] paths) => Work(async ct =>
    {
        if (paths.Length > 50) throw new ArgumentException("한 번에 50개 이하의 파일을 추가하세요.");
        var vault = ConnectedVault();
        var results = new List<string>(); int done = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested(); Status = $"자료 추가 중 · {done}/{paths.Length} · {Path.GetFileName(path)}"; Changed();
            try { var added = await KnowledgeDocuments.ImportAsync(vault, path, ct); done++; results.Add($"완료 · {Path.GetFileName(path)} → 노트 {added.Count}개"); }
            catch (Exception error) when (error is not OperationCanceledException) { results.Add($"실패 · {Path.GetFileName(path)}: {error.Message}"); }
        }
        await LoadNotes(vault, ct);
        Preview = string.Join("\n\n", results);
        Status = $"파일 {done}/{paths.Length}개 추가 완료. 원본 파일은 그대로 유지됩니다.";
    });
    private async void Search_Click(object sender, RoutedEventArgs e) => await Search();
    private async void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Search(); } }
    private Task Search() => Work(async ct =>
    {
        if (string.IsNullOrWhiteSpace(Query)) throw new ArgumentException("찾을 내용을 입력하세요.");
        var result = await ObsidianKnowledge.SearchAsync(new() { Enabled = true, VaultPath = ConnectedVault() }, Query, ct);
        SelectedNote = null;
        Preview = result.Excerpts.Count == 0 ? "관련 노트가 없습니다. 제목이나 다른 주제어로 검색해 보세요."
            : string.Join("\n\n", result.Excerpts.Select(x => $"[노트: {x.Source}] · 발췌 {x.Part}\n{x.Text}"));
        Status = $"관련 발췌 {result.Excerpts.Count}개 · 읽은 노트 {result.Notes}개 · 제외 {result.Skipped}개{(result.Limited ? " · 검색 한도 도달" : "")} · AI 호출 없음";
    });
    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle) return;
        try { ConnectedVault(); } catch (Exception error) { Status = error.Message; Changed(); return; }
        var title = new TextBox { Margin = new(0, 4, 0, 12), Padding = new(8), MaxLength = 200 };
        var body = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(8), MaxLength = 100000 };
        System.Windows.Automation.AutomationProperties.SetName(title, "새 지식 노트 제목");
        System.Windows.Automation.AutomationProperties.SetName(body, "새 지식 노트 내용");
        var hint = new TextBlock { Text = "제목과 내용을 적거나 붙여 넣으세요.", Margin = new(0, 10, 0, 10) };
        var button = new Button { Content = "노트 저장", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new(18) };
        var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "제목" }); heading.Children.Add(title);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var footer = new StackPanel(); footer.Children.Add(hint); footer.Children.Add(button);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(body);
        var dialog = new Window { Title = "지식 노트 직접 쓰기", Owner = Window.GetWindow(this), Width = 450, Height = 520, MinWidth = 360, MinHeight = 350, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        button.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(body.Text)) { hint.Text = "제목과 내용을 모두 입력하세요."; return; }
            if (Encoding.UTF8.GetByteCount(body.Text) > ObsidianKnowledge.MaxFileBytes - 2000) { hint.Text = "내용이 너무 깁니다. 여러 노트로 나누어 저장하세요."; return; }
            dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true) return;
        await Work(async ct =>
        {
            var vault = ConnectedVault();
            var path = await ObsidianKnowledge.SaveNoteAsync(new() { VaultPath = vault }, title.Text, body.Text, cancellation: ct);
            await LoadNotes(vault, ct); SelectedNote = Notes.FirstOrDefault(n => n.Path == path); Status = "노트를 저장했습니다. 다음 질문부터 참고합니다.";
        });
    }
    private void Open(Action action) { try { action(); } catch (Exception error) { Status = error.Message; } Changed(); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(() => Process.Start(new ProcessStartInfo(ConnectedVault()) { UseShellExecute = true }));
    private void OpenObsidian_Click(object sender, RoutedEventArgs e) => Open(() => Process.Start(new ProcessStartInfo("obsidian://open?path=" + Uri.EscapeDataString(ConnectedVault())) { UseShellExecute = true }));
    private void Edit_Click(object sender, RoutedEventArgs e) => Open(() =>
    {
        if (selectedNote is null || !File.Exists(selectedNote.Path)) throw new IOException("노트를 선택하세요.");
        Process.Start(new ProcessStartInfo("obsidian://open?path=" + Uri.EscapeDataString(selectedNote.Path)) { UseShellExecute = true });
    });
}
