using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KakaoRelay.Core;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Encoder = PromptJson.Encoder);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.Limits.MaxRequestBodySize = 20_000_000; });
var app = builder.Build();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var expected = Encoding.UTF8.GetBytes("Bearer " + token);
var reader = new ImportedChatReader();
var settings = new AiSettingsStore(AiSettingsStore.DefaultPath);
using var aiRunner = new CliAiRunner();
var ai = new AiService(reader, settings, aiRunner);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    // Reject DNS rebinding and requests from other websites; auth token lives only in the URL fragment.
    if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        var origin = context.Request.Headers.Origin.ToString();
        if ((origin.Length > 0 && origin != "http://" + context.Request.Host) || supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        { context.Response.StatusCode = 401; return; }
    }
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (ApiFailure e) { context.Response.StatusCode = e.StatusCode; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) when (e is ArgumentException or JsonException or BadHttpRequestException or InvalidDataException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "요청을 처리하지 못했습니다. CLI 설정과 로그인 상태를 확인하세요." }); }
});
foreach (var (route, file, mime) in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/app.js", "app.js", "text/javascript; charset=utf-8"), ("/style.css", "style.css", "text/css; charset=utf-8") })
{
    var name = file; var type = mime;
    app.MapGet(route, () => Results.Stream(typeof(Program).Assembly.GetManifestResourceStream("KakaoRelay.Portable.web." + name)!, type));
}
app.MapGet("/api/settings", () => settings.Load());
app.MapPut("/api/settings", (AiSettings s) => { settings.Save(s); return Results.Ok(); });
app.MapGet("/api/providers", () => CliAiRunner.Status(settings.Load()));
app.MapGet("/api/models", () => settings.Load().Providers.ToDictionary(p => p.Id, p => ModelCatalog.Choices(p.Id, p.Model)));
app.MapPost("/api/import", (ImportRequest r) => reader.Import(r.Name, r.Content));
app.MapGet("/api/rooms", async (CancellationToken ct) => await reader.RoomsAsync(ct));
app.MapGet("/api/messages", async (string roomId, int? limit, CancellationToken ct) => await reader.ReadAsync("import", roomId, limit ?? 100, ct));
app.MapPost("/api/generate", async (AiCommand command, CancellationToken ct) => await ai.GenerateAsync(command, ct));
await app.StartAsync();
var url = app.Urls.Single() + "/#" + token;
Console.WriteLine("KakaoRelay Portable · macOS / Linux / Windows");
Console.WriteLine("로컬 브라우저 UI. 종료: Ctrl+C. 가져온 대화와 결과는 앱 메모리에만 유지됩니다.");
// Printed for headless hosts. The URL grants access to this instance; do not share it.
Console.WriteLine(url);
if (!args.Contains("--no-browser"))
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else { var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false }; start.ArgumentList.Add(url); Process.Start(start); }
    }
    catch { Console.WriteLine("위 로컬 주소를 브라우저에서 여세요."); }
}
await app.WaitForShutdownAsync();
public sealed record ImportRequest(string Name, string Content);
