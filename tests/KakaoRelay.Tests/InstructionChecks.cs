using System.Text.Json;
using KakaoRelay.Core;

internal static class InstructionChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class Reader : IChatReader
    {
        public readonly LocalRoom Room = new("fixture", Guid.NewGuid().ToString("N"), "Fixture", true);
        public readonly List<LocalMessage> Messages = [new("1", "user", "fixture", DateTimeOffset.UtcNow, 1, "first-context", false)];
        public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default) => Task.FromResult(new List<LocalRoom> { Room });
        public Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default)
            => Task.FromResult(new ChatContext(Room, Messages.ToList(), DateTimeOffset.UtcNow));
    }
    private static string Prompt(AiResult result) => JsonDocument.Parse(result.Text).RootElement.GetProperty("prompt").GetString()!;
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (var id in new[] { "codex", "claude" })
        {
            var reader = new Reader(); var clock = new Clock();
            var store = new AiSettingsStore(Path.Combine(root, "daily-instructions-" + id + ".json"));
            var settings = new AiSettings { Provider = id };
            settings.Persona.Name = "instruction-fixture"; settings.Persona.Instructions = "unique-persona-rule";
            foreach (var provider in settings.Providers) { provider.Enabled = provider.Id == id; provider.Executable = Environment.ProcessPath!; }
            store.Save(settings);
            var command = new AiCommand(reader.Room.Profile, reader.Room.Id, "reply", "first-request") { TargetMessageId = "1" };
            var runner = new CliAiRunner();
            try
            {
                var service = new AiService(reader, store, runner, clock: clock);
                var first = Prompt(await service.GenerateAsync(command));
                reader.Messages.Add(new("2", "user", "fixture", DateTimeOffset.UtcNow, 1, "new-context", false));
                command = command with { Instruction = "second-request", TargetMessageId = "2" };
                var second = Prompt(await service.GenerateAsync(command));
                check(first.Contains("unique-persona-rule") && first.Contains("[[emotion:") && !second.Contains("unique-persona-rule")
                    && !second.Contains("[[emotion:") && second.Contains("second-request") && second.Contains("new-context") && !second.Contains("first-context"),
                    $"{id} sends persona and fixed instructions once, then only the current request and changed context");
                runner.Dispose(); runner = new CliAiRunner();
                service = new AiService(reader, store, runner, clock: clock);
                check(!Prompt(await service.GenerateAsync(command)).Contains("unique-persona-rule"), $"{id} same-day restart resumes without reinjecting persona");
                clock.Now = clock.Now.AddDays(1);
                check(Prompt(await service.GenerateAsync(command)).Contains("unique-persona-rule")
                    && !Prompt(await service.GenerateAsync(command)).Contains("unique-persona-rule"), $"{id} refreshes instructions once on the next local day");
                settings.Persona.Instructions = "changed-persona-rule"; store.Save(settings);
                check(Prompt(await service.GenerateAsync(command)).Contains("changed-persona-rule"), $"{id} persona edits initialize a fresh session immediately");
                var analyze = command with { Mode = "analyze" };
                check(Prompt(await service.GenerateAsync(analyze)).Contains("핵심 요약")
                    && !Prompt(await service.GenerateAsync(analyze)).Contains("changed-persona-rule"), $"{id} task-mode changes refresh their rules once");
                var folder = AiConversation.Folder(settings.Providers.Single(p => p.Id == id), settings.Persona, command);
                var state = AiConversation.Load(folder);
                check(state.InstructionsDate == DateOnly.FromDateTime(clock.Now.DateTime) && state.InstructionsRevision is not null,
                    $"{id} persists instruction date and revision with session metadata");
                new AiConversation().Save(folder);
                check(Prompt(await service.GenerateAsync(command)).Contains("changed-persona-rule"), $"{id} interrupted or lost sessions receive full instructions again");
            }
            finally { runner.Dispose(); }
        }
        var legacy = new AiConversation { SessionId = "legacy" };
        check(legacy.NeedsInstructions(new(2026, 9, 29), "v1"), "Older session metadata gets one initial instruction refresh");
    }
}
