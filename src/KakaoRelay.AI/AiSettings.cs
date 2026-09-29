using System.Text.Json;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

public sealed class BotPersona : System.ComponentModel.INotifyPropertyChanged
{
    private string name = "그쫀쿠";
    public string Name { get => name; set { if (name == value) return; name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Role { get; set; } = "대화 맥락을 이해하고 필요한 답변과 업무 정리를 돕는 AI 챗봇";
    public string Tone { get; set; } = "친절하고 차분한 존댓말";
    public string Style { get; set; } = "핵심부터 짧게 답하고, 불확실한 내용은 확인 질문을 한다. 과장하지 않는다.";
    public string Instructions { get; set; } = "대화에 없는 사실을 만들지 않는다. 민감한 정보는 답변에 불필요하게 반복하지 않는다.";
}
public sealed class AiProviderSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ModelChoices => ModelCatalog.Choices(Id, Model);
    public string Id { get; set; } = "codex";
    public bool Enabled { get; set; } = true;
    public string Executable { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "default";
}
public sealed class PersonaProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "새 페르소나";
    public BotPersona Persona { get; set; } = new();
    public bool AttachImages { get; set; }
    public KnowledgeSettings Knowledge { get; set; } = new();
}
public sealed class AiSettings
{
    public string SelfId { get; set; } = "";
    // Legacy field remains readable; clients cannot override the persona-derived mention.
    public string Trigger { get => "@" + (Persona?.Name?.Trim() ?? ""); set { } }
    public int PollSeconds { get; set; } = 5;
    public string ImageGptModel { get; set; } = PersonaImageCliGenerator.GptModel;
    public string ImageGrokModel { get; set; } = PersonaImageCliGenerator.GrokModel;
    public string ReplyMode { get; set; } = "trigger";
    public Dictionary<string, string> RoomReplyModes { get; set; } = [];
    public string ReplyModeForRoom(string profile, string roomId) => RoomReplyModes.GetValueOrDefault(RoomKey(profile, roomId), ReplyMode);
    public List<string> SelectedBotRooms { get; set; } = [];
    public AutoKnowledgeSettings AutoKnowledge { get; set; } = new();
    public string? SelectedChatRoom { get; set; }
    public string Provider { get; set; } = "auto";
    public int ContextMessages { get; set; } = 100;
    public int TimeoutSeconds { get; set; } = 180;
    private BotPersona legacyPersona = new();
    // Keep old API/portable clients editing the first slot after migration as well.
    public BotPersona Persona { get => Personas is { Count: > 0 } ? Personas[0].Persona : legacyPersona; set { legacyPersona = value; if (Personas is { Count: > 0 }) Personas[0].Persona = value; } }
    public List<PersonaProfile> Personas { get; set; } = [];
    public Dictionary<string, string> RoomPersonas { get; set; } = [];
    public static string RoomKey(string profile, string roomId) => JsonSerializer.Serialize(new[] { profile, roomId });
    public void EnsurePersonas()
    {
        if (Personas.Count != 0) return;
        Personas = [new() { Id = "default", Label = "기본", Persona = Persona },
            new() { Id = "work", Label = "업무", Persona = new() { Name = "업무 도우미", Tone = "정중하고 간결한 존댓말", Style = "할 일과 결론부터 명확하게 정리한다." } },
            new() { Id = "friend", Label = "친구", Persona = new() { Name = "친구", Tone = "따뜻하고 편안한 말투", Style = "상대방의 감정에 공감하고 자연스럽게 대화한다." } }];
    }
    public PersonaProfile ResolvePersona(string profile, string roomId)
    {
        EnsurePersonas();
        return RoomPersonas.TryGetValue(RoomKey(profile, roomId), out var id) ? Personas.Single(p => p.Id == id) : Personas[0];
    }
    public string TriggerForRoom(string profile, string roomId) => "@" + ResolvePersona(profile, roomId).Persona.Name.Trim();
    public static string ImageRoot(string personaId)
    {
        if (!Regex.IsMatch(personaId, "\\A[a-zA-Z0-9_-]{1,80}\\z")) throw new ArgumentException("페르소나 ID를 확인하세요.");
        return Path.Combine(PersonaImageStore.DefaultRoot, personaId);
    }
    public List<AiProviderSettings> Providers { get; set; } = [new() { Id = "codex" }, new() { Id = "grok" }, new() { Id = "claude" }];
    public static readonly string[] Order = ["codex", "grok", "claude"];
    public void Validate()
    {
        if (AutoKnowledge is null) throw new ArgumentException("대화 자동 정리 설정을 확인하세요.");
        AutoKnowledge.Validate();
        if (new[] { ImageGptModel, ImageGrokModel }.Any(m => m is null || !Regex.IsMatch(m, "\\A[a-zA-Z0-9._:/-]{1,150}\\z"))) throw new ArgumentException("이미지 CLI 모델 이름을 확인하세요.");
        if (PollSeconds is < 1 or > 300 || ReplyMode is not ("immediate" or "trigger" or "context")) throw new ArgumentException("폴링은 1~300초이며 답변 방식을 선택해야 합니다.");
        if (RoomReplyModes is null || RoomReplyModes.Values.Any(mode => mode is not ("immediate" or "trigger" or "context"))) throw new ArgumentException("채팅방별 답변 방식을 확인하세요.");
        if (SelectedBotRooms is null || SelfId is null || Trigger is null) throw new ArgumentException("자동 답장 설정을 확인하세요.");
        if (Provider != "auto" && !Order.Contains(Provider)) throw new ArgumentException("프로바이더를 확인하세요.");
        if (ContextMessages is < 1 or > 500 || TimeoutSeconds is < 10 or > 900) throw new ArgumentException("문맥은 1~500개, 제한 시간은 10~900초입니다.");
        if (Persona is null || Providers is null || Providers.Count != 3 || Providers.Any(p => p is null) || !Providers.Select(p => p.Id).Order().SequenceEqual(Order.Order())) throw new ArgumentException("AI 설정 형식이 올바르지 않습니다.");
        if (string.IsNullOrWhiteSpace(Persona.Name) || new[] { Persona.Name, Persona.Role, Persona.Tone, Persona.Style, Persona.Instructions }.Any(t => t is null || t.Length > 10000)) throw new ArgumentException("페르소나 이름과 지침을 확인하세요.");
        if (Personas is null || RoomPersonas is null) throw new ArgumentException("페르소나 목록을 확인하세요.");
        EnsurePersonas();
        if (Personas.Count != 3 || Personas.Any(p => p is null) || Personas.Select(p => p.Id).Distinct().Count() != 3) throw new ArgumentException("페르소나 3개의 ID가 서로 달라야 합니다.");
        foreach (var p in Personas)
        {
            if (p.Knowledge is null) throw new ArgumentException("지식베이스 설정을 확인하세요.");
            p.Knowledge.Validate();
            ImageRoot(p.Id);
            if (string.IsNullOrWhiteSpace(p.Label) || p.Persona is null || string.IsNullOrWhiteSpace(p.Persona.Name)
                || new[] { p.Label, p.Persona.Name, p.Persona.Role, p.Persona.Tone, p.Persona.Style, p.Persona.Instructions }.Any(t => t is null || t.Length > 10000))
                throw new ArgumentException("페르소나 이름과 지침을 확인하세요.");
        }
        if (RoomPersonas.Any(binding => !Personas.Any(p => p.Id == binding.Value))) throw new ArgumentException("채팅방에 연결된 페르소나가 없습니다.");
        foreach (var p in Providers)
        {
            if (p.Model is null || p.Executable is null || !Regex.IsMatch(p.Model, "\\A[a-zA-Z0-9._:/-]{0,150}\\z") || !Regex.IsMatch(p.Effort ?? "", "\\A(default|none|minimal|low|medium|high|xhigh|max|ultra|ultracode)\\z")) throw new ArgumentException("모델 또는 effort 값을 확인하세요.");
        }
    }
    public AiSettings Copy() => JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(this))!;
}
public sealed class AiSettingsStore(string path)
{
    private readonly object sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = PromptJson.Encoder };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "ai-settings.json");
    public AiSettings Load()
    {
        lock (sync)
        {
            if (!File.Exists(path)) { var initial = new AiSettings(); initial.EnsurePersonas(); return initial; }
            var settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("AI 설정을 읽을 수 없습니다.");
            settings.Validate(); return settings;
        }
    }
    public void Save(AiSettings settings)
    {
        settings.Validate();
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
    }
}
