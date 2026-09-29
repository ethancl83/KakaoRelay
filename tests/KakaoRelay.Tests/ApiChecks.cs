using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KakaoRelay.Core;

internal static class ApiChecks
{
    private sealed class FakeImageTransport : IImageSendTransport
    {
        public int Attachments;
        public Action? OnAttach;
        public void Validate(TestSendRequest target) { }
        public void Attach(string path) { if (!File.Exists(path)) throw new Exception("Missing staged file"); Attachments++; OnAttach?.Invoke(); }
        public bool ConfirmPreview() => true;
    }
    private sealed class FakeAiRunner : IAiRunner
    {
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation) => Task.FromResult("회의는 금요일입니다.");
    }
    private sealed class FakeTransport : ITestSendTransport
    {
        public int Posts;
        public Action? BeforeWrite;
        public Action<string>? OnPost;
        private string draft = "메시지 입력";
        public string ForegroundWindow => "0x1";
        public void ValidateTarget(TestSendRequest request) { }
        public string ReadDraft() => draft;
        public void WriteDraft(string value) { BeforeWrite?.Invoke(); draft = value; }
        public void PostEnter() { Posts++; OnPost?.Invoke(draft); draft = ""; }
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        await CheckDelayedTextAsync(root, check);
        await CheckQueueAsync(root, check);
        var ledger = Path.Combine(root, "api-ledger");
        var fake = new FakeTransport();
        var room = new ConversationTarget("시험방", 1, "0x1", false);
        var rooms = new List<ConversationTarget> { room };
        var imageTransport = new FakeImageTransport();
        var service = new ApiSendService(ledger, () => rooms, () => fake, () => imageTransport, Path.Combine(root, "api-image-transfers"));
        var path = Path.Combine(root, "connection.json");
        await using (var api = await RelayApi.StartAsync(path, () => rooms, () => TestSender.ReadHistory(ledger), service.SendAsync))
        {
            using var client = new HttpClient { BaseAddress = new Uri(api.Connection.BaseUrl) };
            check((await client.GetAsync("/v1/rooms")).StatusCode == HttpStatusCode.Unauthorized, "API rejects unauthenticated requests");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Connection.Token);
            client.DefaultRequestHeaders.Add("Origin", "https://example.org");
            check((await client.GetAsync("/v1/rooms")).StatusCode == HttpStatusCode.Unauthorized, "API rejects browser-origin requests even with a token");
            client.DefaultRequestHeaders.Remove("Origin");
            var imagePath = Path.Combine(root, "api-image.png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII="));
            var imageCommand = new ApiImageCommand("api-image-once", "시험방", imagePath);
            var imageResponse = await client.PostAsJsonAsync("/v1/send-image", imageCommand);
            var imageReceipt = await imageResponse.Content.ReadFromJsonAsync<TestSendReceipt>();
            check(imageResponse.IsSuccessStatusCode && imageReceipt is { Kind: "image", AttachmentQueued: true, EnterPosted: true } && imageTransport.Attachments == 1 && fake.Posts == 0, "Image HTTP endpoint routes a local file through the image adapter only");
            File.Delete(imagePath);
            var duplicateImage = await client.PostAsJsonAsync("/v1/send-image", imageCommand);
            check(duplicateImage.IsSuccessStatusCode && imageTransport.Attachments == 1, "Image API retry returns receipt even after the source file is gone");
            var wrongImage = await client.PostAsJsonAsync("/v1/send-image", imageCommand with { ImagePath = Path.Combine(root, "different.png") });
            check(wrongImage.StatusCode == HttpStatusCode.Conflict && imageTransport.Attachments == 1, "Image request ID cannot be reused for another file");
            var missingImage = await client.PostAsJsonAsync("/v1/send-image", new ApiImageCommand("image-empty", "시험방"));
            var relativeImage = await client.PostAsJsonAsync("/v1/send-image", new ApiImageCommand("image-relative", "시험방", "relative.png"));
            check(missingImage.StatusCode == HttpStatusCode.BadRequest && relativeImage.StatusCode == HttpStatusCode.BadRequest && imageTransport.Attachments == 1, "Image API rejects missing or relative image sources before dispatch");
            var found = await client.GetFromJsonAsync<List<ConversationTarget>>("/v1/rooms");
            check(found?.Single().Title == "시험방", "API returns available rooms as JSON");
            check((await client.GetStringAsync("/v1/rooms")).Contains("시험방"), "HTTP JSON responses contain literal Korean instead of Unicode escapes");
            check(JsonSerializer.Serialize(new { text = "한글 발송 이력" }, ReportStore.JsonOptions).Contains("한글 발송 이력"), "Reports and send history share readable Korean JSON encoding");
            var malformed = await client.PostAsync("/v1/send", new StringContent("{bad", Encoding.UTF8, "application/json"));
            check(malformed.StatusCode == HttpStatusCode.BadRequest && fake.Posts == 0, "Malformed API JSON never dispatches");
            var invalid = await client.PostAsJsonAsync("/v1/send", new { requestId = "../bad", recipient = "시험방", message = "hi" });
            check(invalid.StatusCode == HttpStatusCode.BadRequest && fake.Posts == 0, "API validates IDs before filesystem or native access");
            foreach (var delay in new[] { -1, 30001 })
                check((await client.PostAsJsonAsync("/v1/send", new { requestId = "invalid-delay", recipient = "시험방", message = "hi", sendDelayMs = delay })).StatusCode == HttpStatusCode.BadRequest && fake.Posts == 0,
                    "HTTP rejects invalid send delays before touching the editor");
            var command = new ApiSendCommand("long-message", "시험방", new string('가', 12000) + "\r\n끝");
            var response = await client.PostAsJsonAsync("/v1/send", command);
            var receipt = await response.Content.ReadFromJsonAsync<TestSendReceipt>();
            check(response.IsSuccessStatusCode && receipt?.EnterPosted == true && fake.Posts == 1, "API accepts long Korean messages and empty cue without a checkbox");
            check(receipt?.SendDelayMs == 1000 && !receipt.InputResponseTimedOut, "API defaults to a one-second delay and records the effective delay");
            rooms.Clear();
            var replay = await client.PostAsJsonAsync("/v1/send", command);
            check(replay.IsSuccessStatusCode && fake.Posts == 1, "Duplicate API request returns stored result even after room closes");
            var conflict = await client.PostAsJsonAsync("/v1/send", command with { Message = "changed" });
            check(conflict.StatusCode == HttpStatusCode.Conflict && fake.Posts == 1, "Changed content cannot reuse an API request ID");
            var result = await client.GetFromJsonAsync<TestSendReceipt>("/v1/requests/long-message");
            check(result?.Status == "needs-review", "API distinguishes queued Enter from confirmed delivery");
            var files = Directory.GetFiles(ledger, "*.json", SearchOption.AllDirectories);
            check(files.All(f => !File.ReadAllText(f).Contains(new string('가', 50), StringComparison.Ordinal)), "API reservations and receipts do not persist message bodies");
            File.Delete(TestSender.ReceiptPath(ledger, "long-message"));
            var interrupted = await client.PostAsJsonAsync("/v1/send", command);
            check(interrupted.StatusCode == HttpStatusCode.Conflict && fake.Posts == 1, "Interrupted reserved request is never automatically replayed");
            var missing = await client.PostAsJsonAsync("/v1/send", new ApiSendCommand("missing", "시험방", "hello"));
            check(missing.StatusCode == HttpStatusCode.Conflict && fake.Posts == 1, "Missing recipient never dispatches");
            var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/capabilities");
            check(capabilities.GetProperty("maxMessageCharacters").ValueKind == JsonValueKind.Null && !capabilities.GetProperty("recentMessages").GetProperty("supported").GetBoolean(), "Capabilities declare no app character cap and unsupported recent-message reading");
        }
        check(!File.Exists(path), "API removes its discovery credentials on shutdown");
        var chatReader = new ImportedChatReader();
        var localRoom = chatReader.Import("api.txt", "--------------- 2026년 9월 28일 ---------------\n[테스트] [오후 1:00] 회의는 금요일입니다.");
        var ai = new AiService(chatReader, new AiSettingsStore(Path.Combine(root, "api-ai-settings.json")), new FakeAiRunner());
        await using (var api = await RelayApi.StartAsync(path, () => rooms, () => [], service.SendAsync, ai))
        {
            using var client = new HttpClient { BaseAddress = new Uri(api.Connection.BaseUrl) };
            check((await client.GetAsync("/v1/local/rooms")).StatusCode == HttpStatusCode.Unauthorized, "Local conversation API requires the same bearer protection");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Connection.Token);
            var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/capabilities");
            check(capabilities.GetProperty("recentMessages").GetProperty("supported").GetBoolean() && capabilities.GetProperty("aiChat").GetBoolean(), "Configured API advertises local reading and AI chat");
            var context = await client.GetFromJsonAsync<ChatContext>($"/v1/local/messages?profile=import&roomId={localRoom.Id}&limit=10");
            check(context?.Messages.Single().Text == "회의는 금요일입니다.", "Local messages endpoint returns actual context without dispatching");
            var invalid = await client.GetAsync($"/v1/local/messages?profile=import&roomId={localRoom.Id}&limit=501");
            check(invalid.StatusCode == HttpStatusCode.BadRequest, "Message limit validation is exposed through HTTP");
            var aiSettings = await client.GetFromJsonAsync<AiSettings>("/v1/ai/settings");
            aiSettings!.Persona.Name = "API 봇";
            check((await client.PutAsJsonAsync("/v1/ai/settings", aiSettings)).IsSuccessStatusCode && ai.Settings.Load().Persona.Name == "API 봇", "AI settings endpoint persists persona changes");
            var posts = fake.Posts;
            var response = await client.PostAsJsonAsync("/v1/ai/generate", new AiCommand(localRoom.Profile, localRoom.Id, "reply"));
            var answer = await response.Content.ReadFromJsonAsync<AiResult>();
            check(response.IsSuccessStatusCode && answer?.Provider == "codex" && fake.Posts == posts, "AI generation returns a draft without sending to KakaoTalk");
            aiSettings.Provider = "bogus";
            check((await client.PutAsJsonAsync("/v1/ai/settings", aiSettings)).StatusCode == HttpStatusCode.BadRequest, "Invalid provider cannot overwrite saved settings");
        }
    }
    private sealed class DelayedTransport : ITestSendTransport
    {
        public int Writes, Posts, EarlyReads;
        public int? ConfiguredDelay;
        public Exception? InputFailure;
        public bool ChangeTarget;
        public long WrittenAt;
        public double ElapsedAtPost;
        public string ForegroundWindow => "0x1";
        public void ValidateTarget(TestSendRequest request)
        {
            ConfiguredDelay = request.SendDelayMs;
            if (Writes > 0 && ChangeTarget) throw new InvalidOperationException("Target changed during delay");
        }
        public string ReadDraft()
        {
            if (Writes > 0 && Posts == 0) { EarlyReads++; throw new Exception("Inserted text must not be read before Enter"); }
            return "";
        }
        public void WriteDraft(string text)
        {
            Writes++; WrittenAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (InputFailure is not null) throw InputFailure;
        }
        public void PostEnter() { Posts++; ElapsedAtPost = System.Diagnostics.Stopwatch.GetElapsedTime(WrittenAt).TotalMilliseconds; }
    }
    private static async Task CheckDelayedTextAsync(string root, Action<bool, string> check)
    {
        var ledger = Path.Combine(root, "delayed-api-ledger");
        var fake = new DelayedTransport();
        var service = new ApiSendService(ledger, () => [new("시험방", 1, "0x1", false)], () => fake);
        var command = new ApiSendCommand("delayed-normal", "시험방", "fixture") { SendDelayMs = 70 };
        var receipt = await service.SendAsync(command);
        check(receipt.EnterPosted && fake.Posts == 1 && fake.EarlyReads == 0 && fake.ConfiguredDelay == 70 && fake.ElapsedAtPost >= 60,
            "API text waits for its configured delay then posts once without reading inserted text");
        await service.SendAsync(command with { SendDelayMs = 0 });
        check(fake.Posts == 1 && fake.Writes == 1, "Changing only the delay never replays an existing request ID");
        fake = new() { InputFailure = new TextInputTimeoutException(new System.ComponentModel.Win32Exception(1460)) };
        var timeoutCommand = command with { RequestId = "delayed-timeout" };
        receipt = await service.SendAsync(timeoutCommand);
        await service.SendAsync(timeoutCommand);
        check(receipt.EnterPosted && receipt.InputResponseTimedOut && fake.Posts == 1 && fake.Writes == 1 && fake.EarlyReads == 0 && fake.ElapsedAtPost >= 60,
            "Text insertion timeout waits then posts once in the same request without retyping or read-back");
        foreach (var failure in new Exception[] { new System.ComponentModel.Win32Exception(5), new TimeoutException("selection timeout"), new InvalidOperationException("editor closed") })
        {
            fake = new() { InputFailure = failure };
            receipt = await service.SendAsync(command with { RequestId = Guid.NewGuid().ToString("N"), SendDelayMs = 0 });
            check(!receipt.EnterPosted && !receipt.InputResponseTimedOut && fake.Posts == 0, "Access, selection timeout and editor failures still prevent Enter");
        }
        fake = new() { ChangeTarget = true };
        receipt = await service.SendAsync(command with { RequestId = "delayed-target-change" });
        check(!receipt.EnterPosted && fake.Posts == 0, "A target change during the delay prevents Enter");
        fake = new();
        receipt = await service.SendAsync(command with { RequestId = "delayed-zero", SendDelayMs = 0 });
        check(receipt.EnterPosted && fake.ConfiguredDelay == 0 && fake.EarlyReads == 0, "An explicit zero delay keeps API read-back disabled");
        fake = new() { InputFailure = new TextInputTimeoutException(new System.ComponentModel.Win32Exception(1460)) };
        receipt = TestSender.SendOnce(new("manual-timeout", "fixture", "text", 1, "0x1", DateTimeOffset.Now.AddMinutes(5)), fake, ledger);
        check(!receipt.EnterPosted && fake.Posts == 0, "Manual sends retain their previous stop-on-timeout behavior");
    }
    private static async Task CheckQueueAsync(string root, Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        var fake = new FakeTransport
        {
            BeforeWrite = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); },
            OnPost = text => order.Add(text)
        };
        var image = new FakeImageTransport { OnAttach = () => order.Add("image") };
        var service = new ApiSendService(Path.Combine(root, "queue-ledger"),
            () => [new("시험방", 1, "0x1", false)], () => fake, () => image, Path.Combine(root, "queue-images"));
        var imagePath = Path.Combine(root, "queue.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII="));
        var command = new ApiSendCommand("queue-first", "시험방", "first");
        var first = service.SendAsync(command);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = service.SendAsync(command);
            var conflict = service.SendAsync(command with { Message = "changed" });
            var attachment = service.SendAsync(new("queue-image", "시험방", "") { ImagePath = imagePath });
            var missing = service.SendAsync(new("queue-missing", "닫힌방", "missing"));
            var last = service.SendAsync(new("queue-last", "시험방", "last"));
            check(new Task[] { first, duplicate, conflict, attachment, missing, last }.All(t => !t.IsCompleted),
                "Concurrent sends wait for the active native send instead of returning busy");
            release.Set();
            await Task.WhenAll(first, duplicate, attachment, last).WaitAsync(TimeSpan.FromSeconds(10));
            async Task<bool> HasFailure(Task task, string code)
            {
                try { await task; return false; }
                catch (ApiFailure error) { return error.Code == code; }
            }
            check(await HasFailure(conflict, "request_id_conflict") && await HasFailure(missing, "room_unavailable"),
                "Queued conflicts and unavailable rooms preserve their errors");
            check(order.SequenceEqual(new[] { "first", "image", "last" }) && fake.Posts == 2 && image.Attachments == 1,
                "Text and images share FIFO order, duplicate requests send once, and failures do not block later sends");
        }
        finally { release.Set(); await first; }
    }
}
