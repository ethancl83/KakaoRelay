using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KakaoRelay.Core;

// One pipe and one serialized turn stream per room/provider/settings identity.
internal sealed class ResidentCliPool : IDisposable
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public ResidentCliSession? Session;
        public DateTime LastUsed = DateTime.UtcNow;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;
    public async Task<(string Text, string SessionId)> RunAsync(AiProviderSettings provider, string prompt, int seconds, CancellationToken cancellation, string folder, string? sessionId)
    {
        var key = Path.GetFullPath(folder) + "|" + JsonSerializer.Serialize(provider);
        Entry entry;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var pair in entries.ToArray())
                if (DateTime.UtcNow - pair.Value.LastUsed > TimeSpan.FromMinutes(10) && pair.Value.Gate.Wait(0))
                {
                    try { pair.Value.Session?.Dispose(); pair.Value.Session = null; }
                    finally { pair.Value.Gate.Release(); }
                }
            if (!entries.TryGetValue(key, out entry!)) entries[key] = entry = new();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        await entry.Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (entry.Session is { } current && (!current.Alive || current.SessionId != sessionId))
            { current.Dispose(); entry.Session = null; }
            if (entry.Session is null)
            {
                Directory.CreateDirectory(folder);
                var started = new ResidentCliSession(provider, folder, sessionId);
                // Publish before initialization so shutdown can also stop a starting process.
                lock (sync)
                {
                    if (disposed) { started.Dispose(); throw new OperationCanceledException(shutdown.Token); }
                    entry.Session = started;
                }
                await started.InitializeAsync(timeout.Token).ConfigureAwait(false);
            }
            return await entry.Session.RunAsync(prompt, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            entry.Session?.Dispose(); entry.Session = null;
            throw new OperationCanceledException(timeout.Token);
        }
        catch
        {
            entry.Session?.Dispose(); entry.Session = null;
            // Never replay a possibly accepted turn after failure or cancellation.
            throw;
        }
        finally { entry.LastUsed = DateTime.UtcNow; entry.Gate.Release(); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; shutdown.Cancel();
            foreach (var entry in entries.Values) entry.Session?.Dispose();
            entries.Clear();
        }
    }
}

internal sealed class ResidentCliSession : IDisposable
{
    private readonly AiProviderSettings provider;
    private readonly string folder;
    private readonly CliJsonPipe pipe;
    private readonly string? resumeId;
    public string SessionId { get; private set; }
    public bool Alive => pipe.Alive;
    public ResidentCliSession(AiProviderSettings provider, string folder, string? sessionId)
    {
        this.provider = provider; this.folder = Path.GetFullPath(folder); resumeId = sessionId;
        SessionId = sessionId ?? Guid.NewGuid().ToString();
        var exe = CliAiRunner.Resolve(provider) ?? throw new InvalidOperationException("CLI 실행 파일 없음");
        pipe = new(exe, this.folder, CliAiRunner.ResidentArguments(provider, sessionId, SessionId));
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (provider.Id == "claude") return; // The first user envelope starts the stream.
        await pipe.CallAsync("initialize", new { clientInfo = new { name = "kakaorelay", version = "1.0.0" } }, ct).ConfigureAwait(false);
        await pipe.SendAsync(new { method = "initialized", @params = new { } }, ct).ConfigureAwait(false);
        var configuration = await pipe.CallAsync("config/read", new { includeLayers = false }, ct).ConfigureAwait(false);
        var overrides = new Dictionary<string, object> { ["features.shell_tool"] = false, ["web_search"] = "disabled" };
        if (provider.Effort != "default") overrides["model_reasoning_effort"] = provider.Effort;
        // Disable inherited integrations for this chatbot thread, without editing user config.
        if (configuration.TryGetProperty("config", out var config))
            foreach (var section in new[] { "mcp_servers", "plugins" })
                if (config.TryGetProperty(section, out var values) && values.ValueKind == JsonValueKind.Object)
                {
                    foreach (var item in values.EnumerateObject())
                    {
                        if (item.Name.Contains('.')) throw new InvalidOperationException("Codex 통합 이름에 점이 있어 도구 비활성화 설정을 적용할 수 없습니다.");
                        overrides[$"{section}.{item.Name}.enabled"] = false;
                    }
                }
        var parameters = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(provider.Model) ? null : provider.Model,
            ["cwd"] = folder, ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["config"] = overrides
        };
        if (resumeId is not null) parameters["threadId"] = resumeId;
        var response = await pipe.CallAsync(resumeId is null ? "thread/start" : "thread/resume", parameters, ct).ConfigureAwait(false);
        var actual = response.GetProperty("thread").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(actual) || (resumeId is not null && actual != resumeId)) throw new InvalidOperationException("Codex 세션 ID가 일치하지 않습니다.");
        SessionId = actual;
    }
    public async Task<(string Text, string SessionId)> RunAsync(string prompt, CancellationToken ct)
    {
        if (provider.Id == "claude")
        {
            await pipe.SendAsync(new { type = "user", session_id = SessionId, message = new { role = "user", content = prompt }, parent_tool_use_id = (string?)null }, ct).ConfigureAwait(false);
            while (true)
            {
                var evt = await pipe.ReadAsync(ct).ConfigureAwait(false);
                if (evt.TryGetProperty("type", out var type) && type.GetString() == "result")
                {
                    if (!evt.TryGetProperty("session_id", out var id) || id.GetString() != SessionId) throw new InvalidOperationException("Claude 세션 ID가 일치하지 않습니다.");
                    if (evt.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    {
                        var error = new InvalidOperationException("Claude 응답 실패 · 로그인·모델·사용량을 확인하세요.");
                        error.Data["CliError"] = evt.GetRawText(); throw error;
                    }
                    if (evt.TryGetProperty("subtype", out var subtype) && subtype.GetString() != "success") throw new InvalidOperationException("Claude 응답 실패 · 로그인·모델·사용량을 확인하세요.");
                    return (CliAiRunner.ParseJsonOutput(evt.GetRawText()), SessionId);
                }
                if (type.ValueKind == JsonValueKind.String && type.GetString() == "control_request")
                    await pipe.SendAsync(new { type = "control_response", response = new { subtype = "error", request_id = evt.GetProperty("request_id").GetString(), error = "Tools are disabled for chatbot replies." } }, ct).ConfigureAwait(false);
            }
        }
        var text = new StringBuilder();
        bool complete = false;
        string? failure = null;
        void Observe(JsonElement evt)
        {
            if (!evt.TryGetProperty("method", out var method) || !evt.TryGetProperty("params", out var p)) return;
            if (p.TryGetProperty("threadId", out var thread) && thread.GetString() != SessionId) return;
            if (method.GetString() == "item/completed" && p.GetProperty("item").GetProperty("type").GetString() == "agentMessage")
            {
                var item = p.GetProperty("item");
                // Commentary is progress, not the final chat answer.
                if (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer")
                { text.Append(item.GetProperty("text").GetString()); if (text.Length > 2_000_000) throw new IOException("CLI 응답이 너무 큽니다."); }
            }
            if (method.GetString() == "turn/completed")
            {
                complete = true;
                if (p.GetProperty("turn").GetProperty("status").GetString() != "completed") failure = "Codex 응답 실패 · 로그인·모델·사용량을 확인하세요.";
            }
        }
        await pipe.CallAsync("turn/start", new { threadId = SessionId, model = string.IsNullOrWhiteSpace(provider.Model) ? null : provider.Model,
            effort = provider.Effort == "default" ? null : provider.Effort, input = new[] { new { type = "text", text = prompt } } }, ct, Observe).ConfigureAwait(false);
        while (!complete) Observe(await pipe.ReadAsync(ct).ConfigureAwait(false));
        if (failure is not null) throw new InvalidOperationException(failure);
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidOperationException("Codex 최종 응답 없음");
        return (text.ToString(), SessionId);
    }
    public void Dispose() => pipe.Dispose();
}

internal sealed class CliJsonPipe : IDisposable
{
    private readonly Process process;
    private readonly Task errors;
    private long sequence;
    private int disposed;
    public bool Alive => Volatile.Read(ref disposed) == 0 && !process.HasExited;
    public CliJsonPipe(string exe, string cwd, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        foreach (var key in new[] { "CLAUDECODE", "CODEX_THREAD_ID", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE" }) info.Environment.Remove(key);
        process = Process.Start(info) ?? throw new InvalidOperationException("CLI 시작 실패");
        errors = DrainErrorsAsync();
    }
    private async Task DrainErrorsAsync()
    {
        try { var buffer = new char[4096]; while (await process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }
    public async Task SendAsync(object value, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(PromptJson.Serialize(value).AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }
    public async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new IOException("CLI 연결이 종료되었습니다. 로그인·모델·CLI 버전을 확인하세요.");
            if (line.Length > 2_000_000) throw new IOException("CLI 이벤트가 너무 큽니다.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement evt;
            try { using var document = JsonDocument.Parse(line); evt = document.RootElement.Clone(); }
            catch (JsonException e) { throw new IOException("CLI 응답 형식을 확인하세요.", e); }
            if (evt.TryGetProperty("id", out var id) && evt.TryGetProperty("method", out _))
            {
                await SendAsync(new { id, error = new { code = -32601, message = "Tools and approvals are disabled for chatbot replies." } }, ct).ConfigureAwait(false);
                continue;
            }
            return evt;
        }
    }
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct, Action<JsonElement>? observe = null)
    {
        var id = ++sequence;
        await SendAsync(new { id, method, @params = parameters }, ct).ConfigureAwait(false);
        while (true)
        {
            var evt = await ReadAsync(ct).ConfigureAwait(false);
            if (evt.TryGetProperty("id", out var replyId) && replyId.ValueKind == JsonValueKind.Number && replyId.GetInt64() == id)
            {
                if (evt.TryGetProperty("error", out var error))
                {
                    var exception = new InvalidOperationException($"CLI {method} 실패 · 로그인·모델·세션을 확인하세요.");
                    exception.Data["CliError"] = error.GetRawText();
                    throw exception;
                }
                return evt.GetProperty("result");
            }
            observe?.Invoke(evt);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        try { errors.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        process.Dispose();
    }
}
