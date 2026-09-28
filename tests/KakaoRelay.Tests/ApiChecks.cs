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
        public void Validate(TestSendRequest target) { }
        public void Attach(string path) { if (!File.Exists(path)) throw new Exception("Missing staged file"); Attachments++; }
        public bool ConfirmPreview() => true;
    }
    private sealed class FakeAiRunner : IAiRunner
    {
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation) => Task.FromResult("회의는 금요일입니다.");
    }
    private sealed class FakeTransport : ITestSendTransport
    {
        public int Posts;
        private string draft = "메시지 입력";
        public string ForegroundWindow => "0x1";
        public void ValidateTarget(TestSendRequest request) { }
        public string ReadDraft() => draft;
        public void WriteDraft(string value) => draft = value;
        public void PostEnter() { Posts++; draft = ""; }
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
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
            var command = new ApiSendCommand("long-message", "시험방", new string('가', 12000) + "\r\n끝");
            var response = await client.PostAsJsonAsync("/v1/send", command);
            var receipt = await response.Content.ReadFromJsonAsync<TestSendReceipt>();
            check(response.IsSuccessStatusCode && receipt?.EnterPosted == true && fake.Posts == 1, "API accepts long Korean messages and empty cue without a checkbox");
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
}
