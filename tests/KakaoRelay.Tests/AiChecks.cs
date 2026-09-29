using KakaoRelay.Core;
using System.Text.Json;

internal static class AiChecks
{
    private sealed class BlockingRunner : IAiRunner
    {
        public int Entered;
        public readonly TaskCompletionSource ThreeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        { if (Interlocked.Increment(ref Entered) == 3) ThreeEntered.TrySetResult(); await Release.Task.WaitAsync(cancellation); return "ok"; }
    }
    private sealed class FakeRunner : IAiRunner
    {
        public List<string> Calls = [];
        public bool Cancel;
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        {
            Calls.Add(provider.Id);
            if (Cancel) throw new OperationCanceledException(cancellation);
            if (provider.Id == "codex") throw new InvalidOperationException("offline");
            return Task.FromResult("금요일까지 확인할게요.");
        }
    }
    private sealed class SequenceRunner(Func<int, string> answer) : IAiRunner
    {
        public List<string> Calls = [];
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        {
            Calls.Add(provider.Id);
            return Task.FromResult(answer(Calls.Count));
        }
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var reader = new ImportedChatReader();
        var room = reader.Import("sample.txt", "대화 내보내기\n--------------- 2026년 9월 28일 월요일 ---------------\n[민수] [오전 12:03] 첫 줄\n두 번째 줄\n[영희] [오후 12:05] 금요일까지 확인해주세요.");
        var context = await reader.ReadAsync(room.Profile, room.Id, 100);
        check(context.Messages.Count == 2 && context.Messages[0].Text == "첫 줄\n두 번째 줄" && context.Messages[0].Time.Hour == 0 && context.Messages[1].Time.Hour == 12, "Portable TXT keeps multiline bodies and Korean AM/PM timestamps");
        var mobile = reader.Import("mobile.txt", "2026년 9월 28일 오후 1:12, 민수 : 안녕하세요\n다음 줄");
        check((await reader.ReadAsync(mobile.Profile, mobile.Id, 1)).Messages.Single().Text == "안녕하세요\n다음 줄", "Mobile Korean export format imports without inventing timestamps");
        var json = reader.Import("sample.json", "[{\"id\":\"9\",\"author\":\"A\",\"authorId\":\"1\",\"time\":\"2026-09-28T12:00:00+09:00\",\"text\":\"hello\",\"type\":1,\"deleted\":false}]");
        check((await reader.ReadAsync(json.Profile, json.Id, 1)).Messages.Single().Text == "hello", "Portable JSON adapter reads the API message schema");
        var store = new AiSettingsStore(Path.Combine(root, "ai-settings.json"));
        var settings = new AiSettings(); settings.Persona.Name = "테스트봇"; settings.Providers[1].Model = "grok-example"; store.Save(settings);
        check(store.Load().Persona.Name == "테스트봇" && store.Load().Providers[1].Model == "grok-example", "Persona and provider-specific models persist atomically");
        check(File.ReadAllText(Path.Combine(root, "ai-settings.json")).Contains("테스트봇"), "Settings JSON stores Korean names directly without Unicode escapes");
        settings.SelfId = "self-test"; settings.Trigger = "@그쫀쿠"; settings.SelectedBotRooms = [AiSettings.RoomKey("p", "r")]; settings.SelectedChatRoom = AiSettings.RoomKey("p", "r");
        store.Save(settings);
        var persisted = new AiSettingsStore(Path.Combine(root, "ai-settings.json")).Load();
        check(persisted.SelfId == "self-test" && persisted.Trigger == "@테스트봇" && persisted.SelectedBotRooms.SequenceEqual(settings.SelectedBotRooms) && persisted.SelectedChatRoom == settings.SelectedChatRoom, "Bot identity, persona-derived trigger and selected rooms survive a new settings store");
        check(File.Exists(Path.Combine(root, "ai-settings.json.bak")) && ModelCatalog.Choices("grok", "custom-model").Contains("custom-model"), "Settings backup and custom model choices are preserved");
        var runner = new FakeRunner(); var service = new AiService(reader, store, runner);
        var result = await service.GenerateAsync(new(room.Profile, room.Id));
        check(result.Provider == "grok" && runner.Calls.SequenceEqual(new[] { "codex", "grok" }) && result.Attempts.Count == 2, "Automatic provider fallback preserves Codex then Grok priority");
        settings.Provider = "codex"; store.Save(settings); runner.Calls.Clear();
        result = await service.GenerateAsync(new(room.Profile, room.Id));
        check(result.Provider == "grok" && runner.Calls.SequenceEqual(new[] { "codex", "grok" }), "Explicit provider is the first choice and still falls back on failure");
        settings.Provider = "claude"; store.Save(settings);
        var secondRound = new SequenceRunner(n => n < 5 ? throw new JsonException("Malformed provider response") : "recovered");
        result = await new AiService(reader, store, secondRound).GenerateAsync(new(room.Profile, room.Id));
        check(result.Provider == "codex" && secondRound.Calls.SequenceEqual(new[] { "claude", "codex", "grok", "claude", "codex" }),
            "Claude failure wraps to Codex and malformed responses can recover on the second round");
        foreach (var selected in new[] { "auto", "codex", "grok", "claude" })
        {
            settings.Provider = selected; store.Save(settings);
            var exhausted = new SequenceRunner(_ => throw new OperationCanceledException());
            var start = selected == "auto" ? 0 : Array.IndexOf(AiSettings.Order, selected);
            var expected = Enumerable.Range(0, 6).Select(i => AiSettings.Order[(start + i) % 3]);
            try { await new AiService(reader, store, exhausted).GenerateAsync(new(room.Profile, room.Id)); check(false, "two rounds exhausted"); }
            catch (ApiFailure e) { check(e.Code == "ai_unavailable" && exhausted.Calls.SequenceEqual(expected), $"{selected} stops after exactly two full rounds of provider timeouts"); }
        }
        settings.Provider = "claude"; settings.Providers.Single(p => p.Id == "grok").Enabled = false; store.Save(settings);
        var disabled = new SequenceRunner(_ => throw new InvalidOperationException("offline"));
        try { await new AiService(reader, store, disabled).GenerateAsync(new(room.Profile, room.Id)); check(false, "disabled provider"); }
        catch (ApiFailure) { check(disabled.Calls.SequenceEqual(new[] { "claude", "codex", "claude", "codex" }), "Disabled providers are skipped in both rounds"); }
        settings.Providers.Single(p => p.Id == "grok").Enabled = true; store.Save(settings);
        using (var stopDuringCall = new CancellationTokenSource())
        {
            var interrupted = new SequenceRunner(_ => { stopDuringCall.Cancel(); throw new OperationCanceledException(stopDuringCall.Token); });
            try { await new AiService(reader, store, interrupted).GenerateAsync(new(room.Profile, room.Id), stopDuringCall.Token); check(false, "cancel during fallback"); }
            catch (OperationCanceledException) { check(interrupted.Calls.SequenceEqual(new[] { "claude" }), "User stop during a call prevents all fallback and second-round attempts"); }
        }
        settings.Provider = "auto"; store.Save(settings); runner.Cancel = true; runner.Calls.Clear();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await service.GenerateAsync(new(room.Profile, room.Id), cts.Token); check(false, "cancelled generation"); } catch (OperationCanceledException) { check(runner.Calls.Count == 0, "Cancelled AI requests do not invoke a fallback provider"); }
        var deleted = context with { Messages = [context.Messages[0] with { Text = "do not disclose", Deleted = true }] };
        var prompt = AiService.BuildPrompt(settings.Persona, deleted, new(room.Profile, room.Id));
        check(!prompt.Contains("do not disclose") && prompt.Contains("conversation") && prompt.Contains("신뢰할 수 없는"), "Prompts redact deleted text and distinguish untrusted chat data from instructions");
        const string quotedKorean = "한글 그대로 \"따옴표\" \\경로\n다음 줄\t탭";
        var readableJson = PromptJson.Serialize(quotedKorean);
        check(readableJson.Contains("한글 그대로") && !readableJson.Contains("\\uD55C", StringComparison.OrdinalIgnoreCase)
            && JsonSerializer.Deserialize<string>(readableJson) == quotedKorean, "Prompt JSON preserves Korean while correctly quoting newlines, quotes and backslashes");
        var koreanContext = context with { Messages = [context.Messages[0] with { Text = quotedKorean }] };
        var koreanPrompt = AiService.BuildPrompt(settings.Persona, koreanContext, new(room.Profile, room.Id, "reply", "한글 지시"));
        var conversationJson = koreanPrompt[(koreanPrompt.IndexOf("conversation: ", StringComparison.Ordinal) + "conversation: ".Length)..];
        using var parsedConversation = JsonDocument.Parse(conversationJson);
        check(koreanPrompt.Contains("테스트봇") && koreanPrompt.Contains("한글 지시") && parsedConversation.RootElement[0].GetProperty("text").GetString() == quotedKorean,
            "Conversation prompts retain readable Korean persona, instructions and exact message structure");
        check(ContextReplyJudge.Prompt(settings.Persona, koreanContext.Messages).Contains("한글 그대로")
            && PersonaImageCliGenerator.BuildPrompt("codex", PersonaImageService.ReferencePrompt(settings.Persona, "마젠타 배경"), null).Contains("마젠타 배경"),
            "Reply judging and nested image generation prompts also preserve Korean");
        check(CliAiRunner.Arguments(new() { Id = "codex", Model = "test-model", Effort = "high" }, root).Contains("model_reasoning_effort=\"high\""), "Codex effort is a separate TOML config argument");
        check(CliAiRunner.Arguments(new() { Id = "claude" }, root).Contains("WebSearch,WebFetch") && CliAiRunner.Arguments(new() { Id = "grok" }, root).Contains("--prompt-file"), "Claude web tools are enabled and Grok prompts stay off command line");
        check(CliAiRunner.ParseJsonOutput("{\"text\":\"한글\"}") == "한글" && CliAiRunner.ParseJsonOutput("{\"result\":\"OK\",\"is_error\":false}") == "OK", "Grok and Claude final JSON responses parse");
        try { CliAiRunner.ParseJsonOutput("{\"result\":\"error\",\"is_error\":true}"); check(false, "provider error"); } catch (InvalidOperationException) { check(true, "Provider error payloads cannot become chatbot answers"); }
        var nativeRunner = new CliAiRunner();
        var fixture = new AiProviderSettings { Id = "codex", Executable = Environment.ProcessPath! };
        var text = await nativeRunner.RunAsync(fixture, "fixture 한국어 $(do-not-run) & quote \"", 10, CancellationToken.None);
        check(text == "fixture 한국어 $(do-not-run) & quote \"", "CLI subprocess passes Unicode and shell metacharacters through stdin literally");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await nativeRunner.RunAsync(fixture, "HANG", 10, stop.Token); check(false, "hung process"); } catch (OperationCanceledException) { check(true, "Cancellation terminates a hung CLI process"); }
        try { await nativeRunner.RunAsync(fixture, "HANG", 1, CancellationToken.None); check(false, "timeout process"); } catch (OperationCanceledException) { check(true, "Timeout terminates a hung CLI process"); }
        check(!File.ReadAllText(Path.Combine(root, "ai-settings.json")).Contains("첫 줄"), "Settings never persist conversation content");
        var blocking = new BlockingRunner(); var parallel = new AiService(reader, store, blocking);
        var a = parallel.GenerateAsync(new(room.Profile, room.Id));
        var same = parallel.GenerateAsync(new(room.Profile, room.Id));
        var b = parallel.GenerateAsync(new(mobile.Profile, mobile.Id));
        var c = parallel.GenerateAsync(new(json.Profile, json.Id));
        await blocking.ThreeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(blocking.Entered == 3 && !same.IsCompleted, "Three rooms run concurrently while a second call in the same room waits");
        using var queuedCancel = new CancellationTokenSource();
        var cancelledQueue = parallel.GenerateAsync(new(room.Profile, room.Id), queuedCancel.Token); queuedCancel.Cancel();
        try { await cancelledQueue; check(false, "cancel queued"); } catch (OperationCanceledException) { check(blocking.Entered == 3, "Cancelling a queued room request never launches its CLI"); }
        blocking.Release.SetResult(); await Task.WhenAll(a, same, b, c);
        check(blocking.Entered == 4, "Repeated room call runs after its predecessor completes");
        var sessionId = Guid.NewGuid().ToString();
        foreach (var id in AiSettings.Order)
        {
            var arguments = CliAiRunner.ConversationArguments(new() { Id = id }, root, sessionId, Guid.NewGuid().ToString());
            check(arguments.Contains(sessionId) && !arguments.Contains("--ephemeral") && !arguments.Contains("--no-session-persistence") && arguments.Contains(id == "codex" ? "resume" : "--resume"), $"{id} resumes an explicit persisted session, never the globally latest session");
        }
        var p = new AiProviderSettings(); var persona = new BotPersona();
        check(AiConversation.Folder(p, persona, new("profile", "room1")) != AiConversation.Folder(p, persona, new("profile", "room2")), "Session storage is isolated by room");
        var conversation = new AiConversation { SessionId = sessionId, Seen = context.Messages.Select(AiConversation.Fingerprint).ToHashSet() };
        var sessionFolder = Path.Combine(root, "session"); conversation.Save(sessionFolder);
        var restoredSession = AiConversation.Load(sessionFolder);
        check(restoredSession.SessionId == sessionId && context.Messages.All(m => restoredSession.Seen.Contains(AiConversation.Fingerprint(m))) && !restoredSession.Seen.Contains(AiConversation.Fingerprint(context.Messages[0] with { Text = "edited" })), "Resume metadata persists and detects changed messages without storing plaintext");
        await ResidentCliChecks.RunAsync(root, check);
    }
}
