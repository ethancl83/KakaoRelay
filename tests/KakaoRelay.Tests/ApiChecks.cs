using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KakaoRelay.Core;

internal static class ApiChecks
{
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
        var service = new ApiSendService(ledger, () => rooms, () => fake);
        var path = Path.Combine(root, "connection.json");
        await using (var api = await RelayApi.StartAsync(path, () => rooms, () => TestSender.ReadHistory(ledger), service.SendAsync))
        {
            using var client = new HttpClient { BaseAddress = new Uri(api.Connection.BaseUrl) };
            check((await client.GetAsync("/v1/rooms")).StatusCode == HttpStatusCode.Unauthorized, "API rejects unauthenticated requests");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Connection.Token);
            client.DefaultRequestHeaders.Add("Origin", "https://example.org");
            check((await client.GetAsync("/v1/rooms")).StatusCode == HttpStatusCode.Unauthorized, "API rejects browser-origin requests even with a token");
            client.DefaultRequestHeaders.Remove("Origin");
            var found = await client.GetFromJsonAsync<List<ConversationTarget>>("/v1/rooms");
            check(found?.Single().Title == "시험방", "API returns available rooms as JSON");
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
    }
}
