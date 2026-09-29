using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace KakaoRelay.Core;

public sealed record ApiSendCommand(string RequestId, string Recipient, string Message)
{
    public int SendDelayMs { get; init; } = 1000;
    public PersonaAttachment? Attachment { get; init; }
    public string? ImagePath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsImage => Attachment is not null || ImagePath is not null;
    public void Validate()
    {
        if (SendDelayMs is < 0 or > 30000) throw new ApiFailure(400, "invalid_send_delay", "sendDelayMs는 0~30000 밀리초 정수입니다.");
        if (!ValidId(RequestId) || string.IsNullOrWhiteSpace(Recipient) || Message is null || (!IsImage && string.IsNullOrWhiteSpace(Message)) || Message.Contains('\0'))
            throw new ApiFailure(400, "invalid_request", "requestId, recipient, message를 확인하세요.");
        if (ImagePath is not null && (Attachment is not null || Message.Length != 0 || !Path.IsPathFullyQualified(ImagePath) || ImagePath.Contains('\0')))
            throw new ApiFailure(400, "invalid_request", "imagePath는 로컬 이미지의 절대 경로이며 attachment와 함께 사용할 수 없습니다.");
        if (Attachment is not null)
        {
            if (Message.Length != 0 || !ValidId(Attachment.PersonaId) || !PersonaImageCatalog.Providers.Contains(Attachment.Provider) || !Guid.TryParseExact(Attachment.ImageId, "N", out _))
                throw new ApiFailure(400, "invalid_request", "이미지 요청은 빈 message와 등록된 attachment 식별자를 사용하세요. 텍스트는 별도 요청으로 보냅니다.");
        }
    }
    public static bool ValidId(string? id) => Regex.IsMatch(id ?? "", @"\A[a-zA-Z0-9_-]{1,80}\z");
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ImagePath is not null
        ? JsonSerializer.Serialize(new { Recipient, ImagePath = Path.GetFullPath(ImagePath) }) : Attachment is null
        ? JsonSerializer.Serialize(new { Recipient, Message }) : JsonSerializer.Serialize(new { Recipient, Message, Attachment }))));
}

public sealed record ApiImageCommand(string RequestId, string Recipient, string? ImagePath = null, PersonaAttachment? Attachment = null)
{
    public ApiSendCommand ToSend()
    {
        var command = new ApiSendCommand(RequestId, Recipient, "") { ImagePath = ImagePath, Attachment = Attachment };
        command.Validate();
        return command;
    }
}

// Binds an AI request ID to its content before touching KakaoTalk. Receipts survive room closure.
public sealed class ApiSendService(string ledger, Func<List<ConversationTarget>> scan, Func<ITestSendTransport> transport, Func<IImageSendTransport>? imageTransport = null, string? imageTransferRoot = null)
{
    private readonly AsyncWorkQueue queue = new();
    private sealed record Reservation(string Fingerprint, string EngineFingerprint);
    public Task<TestSendReceipt> SendAsync(ApiSendCommand command)
    {
        command.Validate();
        return queue.Enqueue(async () =>
        {
            return await Task.Run(() =>
            {
                var folder = Path.Combine(ledger, "api");
                Directory.CreateDirectory(folder);
                var reservationPath = Path.Combine(folder, command.RequestId + ".json");
                var receiptPath = TestSender.ReceiptPath(ledger, command.RequestId);
                if (File.Exists(reservationPath))
                {
                    var previous = JsonSerializer.Deserialize<Reservation>(File.ReadAllText(reservationPath), ReportStore.JsonOptions)
                        ?? throw new ApiFailure(409, "uncertain", "기록을 확인할 수 없습니다. 재전송하지 마세요.");
                    if (previous.Fingerprint != command.Fingerprint()) throw new ApiFailure(409, "request_id_conflict", "같은 요청 ID에 다른 본문이나 대화방을 사용할 수 없습니다.");
                    if (!File.Exists(receiptPath)) throw new ApiFailure(409, "uncertain", "요청이 예약된 뒤 중단되었습니다. 대화를 확인하고 자동 재전송하지 마세요.");
                    var receipt = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(receiptPath), ReportStore.JsonOptions);
                    if (receipt is null || receipt.Fingerprint != previous.EngineFingerprint)
                        throw new ApiFailure(409, "uncertain", "발송 기록이 일치하지 않습니다. 대화를 확인하세요.");
                    return receipt;
                }
                if (File.Exists(receiptPath)) throw new ApiFailure(409, "request_id_conflict", "이미 사용 중인 요청 ID입니다.");
                var matches = scan().Where(r => r.Title == command.Recipient && r.Selectable).ToList();
                if (matches.Count != 1) throw new ApiFailure(409, "room_unavailable", "해당 이름의 열린 대화방을 하나로 확인할 수 없습니다.");
                var room = matches[0];
                if (command.IsImage && imageTransport is null) throw new ApiFailure(409, "image_unavailable", "이미지 전송 어댑터가 없습니다.");
                var imagePath = command.ImagePath is not null ? ImageSender.ValidateLocalImage(command.ImagePath) : command.Attachment is null ? null : ImageSender.ResolvePath(command.Attachment);
                var request = new TestSendRequest(command.RequestId, room.Title, imagePath is null ? command.Message : ImageSender.FileIdentity(imagePath), room.ProcessId, room.Handle, DateTimeOffset.Now.AddMinutes(5), true)
                { SendDelayMs = imagePath is null ? command.SendDelayMs : null };
                request.Validate();
                using (var file = new FileStream(reservationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(file, new Reservation(command.Fingerprint(), request.Fingerprint()), ReportStore.JsonOptions);
                    file.Flush(true);
                }
                return imagePath is null ? TestSender.SendOnce(request, transport(), ledger) : ImageSender.SendOnce(request, imagePath, imageTransport!(), ledger, imageTransferRoot);
            });
        });
    }
}

public sealed record ApiConnection(string BaseUrl, string Token, int ProcessId, string Version);

public sealed class RelayApi : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string discoveryPath;
    public ApiConnection Connection { get; }
    public static string DefaultConnectionPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "connection.json");
    private RelayApi(WebApplication app, string discoveryPath, ApiConnection connection)
    { this.app = app; this.discoveryPath = discoveryPath; Connection = connection; }

    public static async Task<RelayApi> StartAsync(string discoveryPath, Func<List<ConversationTarget>> rooms, Func<List<TestSendReceipt>> history, Func<ApiSendCommand, Task<TestSendReceipt>> send, AiService? ai = null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expectedAuth = Encoding.UTF8.GetBytes("Bearer " + token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Encoder = PromptJson.Encoder);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = null;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site")
                || supplied.Length != expectedAuth.Length || !CryptographicOperations.FixedTimeEquals(supplied, expectedAuth))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { code = "unauthorized" }); return; }
            try { await next(context); }
            catch (ApiFailure failure)
            { context.Response.StatusCode = failure.StatusCode; await context.Response.WriteAsJsonAsync(new { code = failure.Code, detail = failure.Message }); }
            catch (Exception error) when (error is JsonException or BadHttpRequestException or ArgumentException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request" }); }
            catch
            { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = "unknown_result", detail = "같은 requestId로 결과를 조회하세요. 새 ID로 자동 재전송하지 마세요." }); }
        });
        app.MapGet("/v1/health", () => Results.Json(new { status = "ready", version = "0.7.6", application = "KakaoRelay" }));
        app.MapGet("/v1/capabilities", () => Results.Json(new
        {
            send = true, idempotency = "requestId", requiresOpenRoom = true, maxMessageCharacters = (int?)null,
            textSend = new { defaultSendDelayMs = 1000, maxSendDelayMs = 30000, verifyInsertedText = false, continueAfterInputTimeout = true },
            imageSend = new { supported = true, endpoint = "/v1/send-image", transport = "WM_DROPFILES + HWND", computerUse = false },
            recentMessages = new { supported = ai is not null, reason = "SQLCipher 4 · 키가 메모리에 로드된 방 · DB/WAL 읽기 전용" },
            aiChat = ai is not null,
            deliveryVerification = "manual", endpoints = new[] { "GET /v1/rooms", "POST /v1/send", "POST /v1/send-image", "GET /v1/requests", "GET /v1/requests/{requestId}" }.Concat(ai is null ? [] : new[] { "GET /v1/local/rooms", "GET /v1/local/messages", "GET /v1/ai/settings", "PUT /v1/ai/settings", "GET /v1/ai/providers", "POST /v1/ai/generate", "GET /v1/personas/{personaId}/images" })
        }));
        app.MapGet("/v1/rooms", async () => Results.Json(await Task.Run(rooms)));
        app.MapPost("/v1/send-image", async (ApiImageCommand command) => Results.Json(await send(command.ToSend())));
        if (ai is not null)
        {
            app.MapGet("/v1/personas/{personaId}/images", (string personaId) =>
            {
                if (!ApiSendCommand.ValidId(personaId) || !ai.Settings.Load().Personas.Any(p => p.Id == personaId)) throw new ApiFailure(404, "persona_unavailable", "페르소나가 없습니다.");
                var catalog = new PersonaImageCatalog(AiSettings.ImageRoot(personaId));
                return Results.Json(new { personaId, appliedProvider = catalog.AppliedProvider(), versions = PersonaImageCatalog.Providers.ToDictionary(p => p, p => catalog.Store(p).Load()) });
            });
            app.MapGet("/v1/local/rooms", async (CancellationToken ct) => Results.Json(await ai.Chats.RoomsAsync(ct)));
            app.MapGet("/v1/local/messages", async (string profile, string roomId, int? limit, CancellationToken ct) => Results.Json(await ai.Chats.ReadAsync(profile, roomId, limit ?? 100, ct)));
            app.MapGet("/v1/ai/settings", () => Results.Json(ai.Settings.Load()));
            app.MapPut("/v1/ai/settings", (AiSettings settings) => { ai.Settings.Save(settings); return Results.Ok(); });
            app.MapGet("/v1/ai/providers", () => Results.Json(CliAiRunner.Status(ai.Settings.Load())));
            app.MapPost("/v1/ai/generate", async (AiCommand command, CancellationToken ct) => Results.Json(await ai.GenerateAsync(command, ct)));
        }
        app.MapGet("/v1/requests", async () => Results.Json(await Task.Run(history)));
        app.MapGet("/v1/requests/{id}", async (string id) =>
        {
            if (!ApiSendCommand.ValidId(id)) return Results.BadRequest(new { code = "invalid_request_id" });
            var receipt = (await Task.Run(history)).FirstOrDefault(r => r.RequestId == id);
            if (receipt is not null) return Results.Json(receipt);
            // A missing receipt is not proof that retrying with a new ID is safe.
            return Results.NotFound(new { code = "receipt_unavailable", detail = "같은 요청 ID를 유지하세요." });
        });
        app.MapPost("/v1/send", async (HttpRequest request) =>
        {
            var command = await request.ReadFromJsonAsync<ApiSendCommand>();
            if (command is null) throw new ApiFailure(400, "invalid_request", "JSON 본문이 필요합니다.");
            command.Validate();
            return Results.Json(await send(command));
        });
        try
        {
            await app.StartAsync();
            var connection = new ApiConnection(app.Urls.Single(), token, Environment.ProcessId, "0.7.6");
            Directory.CreateDirectory(Path.GetDirectoryName(discoveryPath)!);
            var temporary = discoveryPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(connection, ReportStore.JsonOptions));
            File.Move(temporary, discoveryPath, true);
            return new RelayApi(app, discoveryPath, connection);
        }
        catch { await app.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(discoveryPath) && JsonSerializer.Deserialize<ApiConnection>(File.ReadAllText(discoveryPath), ReportStore.JsonOptions)?.Token == Connection.Token)
                File.Delete(discoveryPath);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(stop.Token);
            await app.DisposeAsync();
        }
    }
}
