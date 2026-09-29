using System.Text.Json;
using KakaoRelay.Core;

internal static class AutoKnowledgeChecks
{
    private sealed class Reader : IChatReader
    {
        public LocalRoom Room = new("account", "room", "지식 테스트", true);
        public List<LocalMessage> Messages = [];
        public bool RequireDiscovery, Discovered;
        public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default) { Discovered = true; return Task.FromResult(new List<LocalRoom> { Room }); }
        public Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default)
        {
            if (RequireDiscovery && !Discovered) throw new ApiFailure(404, "room_not_loaded", "먼저 방을 불러오세요.");
            return Task.FromResult(new ChatContext(Room, Messages.TakeLast(limit).ToList(), DateTimeOffset.UtcNow));
        }
    }
    private sealed class Runner : IAiRunner
    {
        public int Calls;
        public string Prompt = "";
        public Func<string> Reply = () => "{\"items\":[]}";
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        { Calls++; Prompt = prompt; cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Reply()); }
    }
    private static LocalMessage Message(int id, string text, string author = "person") => new(id.ToString(), author, author, DateTimeOffset.UtcNow, 1, text, false);
    private static string Digest(params KnowledgeClaim[] claims) => JsonSerializer.Serialize(new KnowledgeDigest(claims.ToList()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static KnowledgeClaim Claim(string topic, string text, int id, string quote, string status = "confirmed") => new(topic, text, "knowledge", status, [new(id.ToString(), quote)]);
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var area = Path.Combine(root, "auto-knowledge"); var vault = Path.Combine(area, "vault"); Directory.CreateDirectory(vault);
        var state = Path.Combine(area, "state"); var reader = new Reader { RequireDiscovery = true }; var runner = new Runner();
        var settings = new AiSettings { SelfId = "self" }; settings.EnsurePersonas();
        settings.Personas[0].Knowledge = new() { Enabled = true, VaultPath = vault };
        settings.AutoKnowledge = new() { Enabled = true, Rooms = [new("account", "room", "지식 테스트", Guid.NewGuid())] };
        var store = new AiSettingsStore(Path.Combine(area, "settings.json")); store.Save(settings);
        check(store.Load().AutoKnowledge.Rooms[0].Generation == settings.AutoKnowledge.Rooms[0].Generation && store.Load().AutoKnowledge.IntervalMinutes == 30, "Automatic knowledge settings and subscription generation persist");
        var service = new AutoKnowledgeService(reader, runner, state); var status = ""; service.StatusChanged += s => status = s;
        reader.Messages.Add(Message(1, "과거 대화"));
        await service.TickAsync(settings, true);
        check(reader.Discovered, "Startup automation discovers local rooms without requiring a manual refresh");
        check(runner.Calls == 0 && !Directory.Exists(Path.Combine(vault, "자동정리")), "Enabling knowledge automation establishes a baseline without summarizing history");
        reader.Messages.AddRange([Message(2, "휴가는 사흘 전에 신청합니다."), Message(3, "BOT-SECRET", "self"), Message(4, "회의는 금요일일지도 몰라요.")]);
        runner.Reply = () => Digest(Claim("휴가 신청", "휴가는 사흘 전에 신청합니다.", 2, "휴가는 사흘 전에 신청합니다."), Claim("회의 일정", "회의는 금요일일 가능성이 있습니다.", 4, "금요일일지도 몰라요.", "review"));
        await service.TickAsync(settings);
        check(runner.Calls == 0, "A scheduled check waits for the configured interval");
        await service.TickAsync(settings, true);
        var accepted = Directory.GetFiles(Path.Combine(vault, "자동정리"), "*.md");
        var reviews = Directory.GetFiles(Path.Combine(vault, "검토필요"), "*.md");
        check(accepted.Length == 1 && reviews.Length == 1 && File.ReadAllText(accepted[0]).Contains("메시지 2") && !runner.Prompt.Contains("BOT-SECRET") && !runner.Prompt.Contains("과거 대화"), "Only new human text is summarized; accepted notes preserve evidence while uncertain claims go to review");
        check((await ObsidianKnowledge.SearchAsync(settings.Personas[0].Knowledge, "회의 금요일")).Excerpts.Count == 0
            && (await KnowledgeDocuments.ListAsync(vault)).Notes.Count == 1, "Review notes are excluded from bot search and the trusted library");
        var restarted = new AutoKnowledgeService(reader, runner, state);
        await restarted.TickAsync(settings, true);
        check(runner.Calls == 1, "Restart resumes the persisted cursor without repeated summaries");
        reader.Messages.Add(Message(5, "휴가는 사흘 전에 신청합니다."));
        runner.Reply = () => Digest(Claim("휴가 신청", "휴가는 사흘 전에 신청합니다.", 5, "휴가는 사흘 전에 신청합니다."));
        await service.TickAsync(settings, true);
        check(Directory.GetFiles(Path.Combine(vault, "자동정리"), "*.md").Length == 1 && status.Contains("중복 1건"), "Repeated knowledge is deduplicated across batches");
        reader.Messages.Add(Message(6, "휴가는 닷새 전에 신청합니다."));
        runner.Reply = () => Digest(Claim("휴가 신청", "휴가는 닷새 전에 신청합니다.", 6, "휴가는 닷새 전에 신청합니다."));
        await service.TickAsync(settings, true);
        check(Directory.GetFiles(Path.Combine(vault, "자동정리"), "*.md").Length == 1 && Directory.GetFiles(Path.Combine(vault, "검토필요"), "*.md").Length == 2,
            "Conflicting content under an existing topic is held for review instead of replacing knowledge");
        reader.Messages.Add(Message(7, "배포는 금요일입니다."));
        runner.Reply = () => Digest(Claim("배포 일정", "배포는 금요일입니다.", 999, "존재하지 않는 근거"));
        await service.TickAsync(settings, true); var failedCalls = runner.Calls;
        check(status.Contains("실패") && Directory.GetFiles(Path.Combine(vault, "자동정리"), "*.md").Length == 1, "Invalid source evidence fails closed and does not publish a note");
        await service.TickAsync(settings);
        check(runner.Calls == failedCalls, "Failed summaries wait until the next interval rather than retrying every minute");
        runner.Reply = () => Digest(Claim("배포 일정", "배포는 금요일입니다.", 7, "배포는 금요일입니다."));
        settings.AutoKnowledge.AutoSave = false;
        await service.TickAsync(settings, true);
        check(Directory.GetFiles(Path.Combine(vault, "검토필요"), "*.md").Length == 3, "Failure preserves the cursor for retry, and review-only mode never activates new facts");
        settings.AutoKnowledge.Enabled = false; reader.Messages.Add(Message(8, "중지 이후")); var before = runner.Calls;
        await service.TickAsync(settings, true);
        check(runner.Calls == before, "Disabled automation never reads or summarizes new content");
        settings.AutoKnowledge.Enabled = true;
        reader.Messages = Enumerable.Range(20, 500).Select(i => Message(i, "many")).ToList();
        await service.TickAsync(settings, true);
        check(status.Contains("500") && runner.Calls == before, "A gap beyond the reader window is reported without silently skipping messages");
        await CheckRecovery(area, vault, check);
    }
    private static async Task CheckRecovery(string area, string vault, Action<bool, string> check)
    {
        var state = Path.Combine(area, "recover"); Directory.CreateDirectory(state);
        var reader = new Reader(); var runner = new Runner(); var settings = new AiSettings { SelfId = "self" }; settings.EnsurePersonas();
        settings.Personas[0].Knowledge = new() { VaultPath = vault, Enabled = true };
        var room = new KnowledgeRoomSubscription("account", "room", "회복", Guid.NewGuid());
        settings.AutoKnowledge = new() { Enabled = true, Rooms = [room] };
        var id = AiConversation.Hash(PromptJson.Serialize(new { room.Profile, room.RoomId, room.Generation, vault, settings.SelfId }));
        var checkpoint = new AutoKnowledgeService.Checkpoint { Cursor = "9", LastRun = DateTimeOffset.UtcNow };
        var pending = new AutoKnowledgeService.Pending(vault, "abcdef-12345", "# recovered\n복구된 지식", "", checkpoint);
        var pendingPath = Path.Combine(state, id + ".pending.json");
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var service = new AutoKnowledgeService(reader, runner, state);
        await service.TickAsync(settings);
        check(File.Exists(Path.Combine(vault, "자동정리", "abcdef-12345.md")) && !File.Exists(pendingPath) && runner.Calls == 0,
            "Interrupted publication recovers the exact saved summary without calling AI again");
    }
}
