using KakaoRelay.Core;

internal static class AutoReplyChecks
{
    private sealed class Reader : IChatReader
    {
        public LocalRoom Room = new("test-profile", "1", "테스트 방", true);
        public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default) => Task.FromResult(new List<LocalRoom> { Room });
        public Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default) => Task.FromResult(new ChatContext(Room,
            limit == 1 ? [Message("1", "old")] : [Message("1", "old"), Message("2", "@bot first"), Message("3", "@bot second")], DateTimeOffset.Now));
        public static LocalMessage Message(string id, string text) => new(id, "2", "tester", DateTimeOffset.Now, 1, text, false);
    }
    private sealed class Runner(Action run) : IAiRunner
    {
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        { run(); cancellation.ThrowIfCancellationRequested(); return Task.FromResult("reply"); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var reader = new Reader(); var sent = new List<ApiSendCommand>();
        var history = new DailyReplyHistory(Path.Combine(root, "auto-reply-days"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new AiService(reader, new AiSettingsStore(Path.Combine(root, "auto-reply-settings.json")), new Runner(() =>
        {
            check(sent.Count == 0, "Detected calls begin model generation without sending a preparation notice");
            stop.Cancel();
        }));
        var session = new AutoReplySession(service, command => { sent.Add(command); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); }, history);
        var settings = service.Settings.Load(); settings.Persona.Name = "bot"; settings.PollSeconds = 1; service.Settings.Save(settings);
        try { await session.RunAsync(reader.Room, "1", "ignored-custom-trigger", stop.Token); } catch (OperationCanceledException) { }
        check(sent.Count == 0, "Cancelling during generation sends no message to the room");
        using (var stopAfterReplies = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var generated = 0;
            var completeService = new AiService(reader, service.Settings, new Runner(() => generated++));
            var completeSession = new AutoReplySession(completeService, command =>
            {
                sent.Add(command); if (sent.Count == 2) stopAfterReplies.Cancel();
                return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true });
            }, history);
            try { await completeSession.RunAsync(reader.Room, "1", "", stopAfterReplies.Token); } catch (OperationCanceledException) { }
            check(generated == 2 && sent.Count == 2 && sent.All(c => c.Message == "reply") && sent.Select(c => c.RequestId).Distinct().Count() == 2,
                "Multiple triggers each send only their generated answer with distinct IDs and no preparation notices");
        }
        var candidate = Reader.Message("6", "@bot congratulations");
        var result = new AiResult("축하해요!", "codex", "fixture", "low", "reply", reader.Room, 1, [])
        { Emotion = "happy", Attachment = new("default", "astra", Guid.NewGuid().ToString("N")) };
        var imageCompleted = new TaskCompletionSource<TestSendReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordered = new List<ApiSendCommand>();
        var orderedSession = new AutoReplySession(service, command =>
        {
            ordered.Add(command);
            return command.IsImage ? imageCompleted.Task : Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true });
        }, history);
        var sending = orderedSession.SendReplyAsync(reader.Room, candidate, result, default);
        check(ordered.Count == 1 && ordered[0].Attachment == result.Attachment && ordered[0].Message == "" && !sending.IsCompleted, "Emotion image is dispatched first and text waits for its receipt");
        imageCompleted.SetResult(new TestSendReceipt { EnterPosted = true, InputCleared = true });
        await sending;
        check(ordered.Count == 2 && !ordered[1].IsImage && ordered[1].Message == result.Text && ordered[0].RequestId.StartsWith("bot-image-") && ordered[1].RequestId.StartsWith("bot-"), "Confirmed image is followed by the reply with separate stable request IDs");
        var ids = ordered.Select(c => c.RequestId).ToArray();
        ordered.Clear(); await orderedSession.SendReplyAsync(reader.Room, candidate, result, default);
        check(ordered.Select(c => c.RequestId).SequenceEqual(ids), "Repeating a reply retains both idempotency IDs after changing send order");
        foreach (var failedReceipt in new[] { new TestSendReceipt { EnterPosted = false, InputCleared = true }, new TestSendReceipt { EnterPosted = true, InputCleared = false } })
        {
            ordered.Clear();
            var failedImage = new AutoReplySession(service, command => { ordered.Add(command); return Task.FromResult(failedReceipt); }, history);
            try { await failedImage.SendReplyAsync(reader.Room, candidate, result, default); check(false, "uncertain image reply"); }
            catch (InvalidOperationException) { check(ordered.Count == 1 && ordered[0].IsImage, "Failed or uncertain image stops before text is sent"); }
        }
        using var cancelAfterImage = new CancellationTokenSource();
        ordered.Clear();
        var canceledImage = new AutoReplySession(service, command => { ordered.Add(command); cancelAfterImage.Cancel(); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); }, history);
        try { await canceledImage.SendReplyAsync(reader.Room, candidate, result, cancelAfterImage.Token); check(false, "cancel after image"); }
        catch (OperationCanceledException) { check(ordered.Count == 1 && ordered[0].IsImage, "Cancellation after image prevents the following text"); }
        ordered.Clear(); await orderedSession.SendReplyAsync(reader.Room, candidate, result with { Attachment = null }, default);
        check(ordered.Count == 1 && ordered[0].Message == result.Text && !ordered[0].IsImage, "No configured image keeps text-only replies working");
        var clock = new Clock(); var dailyRoot = Path.Combine(root, "daily-image-policy");
        AutoReplySession FreshSession() => new(service, command =>
        {
            ordered.Add(command);
            return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true });
        }, new DailyReplyHistory(dailyRoot, clock));
        var neutral = result with { Text = "확인했어요.", Emotion = "neutral" };
        ordered.Clear(); await FreshSession().SendReplyAsync(reader.Room, candidate, neutral, default);
        check(ordered.Count == 2 && ordered[0].IsImage, "First neutral reply of the day sends a default image before text");
        ordered.Clear(); await FreshSession().SendReplyAsync(reader.Room, candidate with { Id = "7" }, neutral, default);
        check(ordered.Count == 1 && !ordered[0].IsImage, "Later neutral reply stays text-only after session restart");
        ordered.Clear(); await FreshSession().SendReplyAsync(reader.Room, candidate, neutral, default);
        check(ordered.Count == 2 && ordered[0].IsImage, "Retry of the original greeting preserves its attachment and stable request identity");
        ordered.Clear(); await FreshSession().SendReplyAsync(reader.Room, candidate with { Id = "8" }, result, default);
        check(ordered.Count == 2 && ordered[0].IsImage, "Expressive replies can still send their matching emotion image later that day");
        var otherRoom = reader.Room with { Id = "other" };
        ordered.Clear(); await FreshSession().SendReplyAsync(otherRoom, candidate, result, default);
        await FreshSession().SendReplyAsync(otherRoom, candidate with { Id = "9" }, neutral, default);
        check(ordered.Count == 3 && ordered.Count(c => c.IsImage) == 1, "An emotional first reply consumes the day's first conversation; later neutral does not send a default");
        var textRoom = reader.Room with { Profile = "other-profile" };
        ordered.Clear(); await FreshSession().SendReplyAsync(textRoom, candidate, neutral with { Attachment = null }, default);
        await FreshSession().SendReplyAsync(textRoom, candidate with { Id = "10" }, neutral, default);
        check(ordered.Count == 2 && ordered.All(c => !c.IsImage), "A text-only first reply also prevents later default images");
        var newRoom = reader.Room with { Id = "new-room" };
        ordered.Clear(); await FreshSession().SendReplyAsync(newRoom, candidate, neutral, default);
        check(ordered.Count == 2, "A different room gets its own first daily greeting");
        clock.Now = clock.Now.AddDays(1);
        ordered.Clear(); await FreshSession().SendReplyAsync(reader.Room, candidate with { Id = "11" }, neutral, default);
        check(ordered.Count == 2 && ordered[0].IsImage, "A new local calendar day permits a new default greeting");
    }
}
