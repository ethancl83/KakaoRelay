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
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var reader = new Reader(); var sent = new List<ApiSendCommand>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new AiService(reader, new AiSettingsStore(Path.Combine(root, "auto-reply-settings.json")), new Runner(() =>
        {
            check(sent.Count == 0, "Detected calls begin model generation without sending a preparation notice");
            stop.Cancel();
        }));
        var session = new AutoReplySession(service, command => { sent.Add(command); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); });
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
            });
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
        });
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
            var failedImage = new AutoReplySession(service, command => { ordered.Add(command); return Task.FromResult(failedReceipt); });
            try { await failedImage.SendReplyAsync(reader.Room, candidate, result, default); check(false, "uncertain image reply"); }
            catch (InvalidOperationException) { check(ordered.Count == 1 && ordered[0].IsImage, "Failed or uncertain image stops before text is sent"); }
        }
        using var cancelAfterImage = new CancellationTokenSource();
        ordered.Clear();
        var canceledImage = new AutoReplySession(service, command => { ordered.Add(command); cancelAfterImage.Cancel(); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); });
        try { await canceledImage.SendReplyAsync(reader.Room, candidate, result, cancelAfterImage.Token); check(false, "cancel after image"); }
        catch (OperationCanceledException) { check(ordered.Count == 1 && ordered[0].IsImage, "Cancellation after image prevents the following text"); }
        ordered.Clear(); await orderedSession.SendReplyAsync(reader.Room, candidate, result with { Attachment = null }, default);
        check(ordered.Count == 1 && ordered[0].Message == result.Text && !ordered[0].IsImage, "No configured image keeps text-only replies working");
    }
}
