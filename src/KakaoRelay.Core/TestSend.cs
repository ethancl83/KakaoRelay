using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace KakaoRelay.Core;

public sealed record TestSendRequest(string RequestId, string Recipient, string Message, int ExpectedProcessId, string ExpectedWindowHandle, DateTimeOffset ExpiresAt, bool VerifiedEmptyPlaceholder = false, string? ContinuationOf = null)
{
    // API text sends wait instead of reading back the inserted message. Null retains the manual flow.
    public int? SendDelayMs { get; init; }
    public void Validate()
    {
        if (!Regex.IsMatch(RequestId ?? "", @"\A[a-zA-Z0-9_-]{1,80}\z")) throw new ArgumentException("Invalid request ID");
        if (string.IsNullOrWhiteSpace(Recipient) || string.IsNullOrWhiteSpace(Message) || Message.Contains('\0')) throw new ArgumentException("Invalid recipient or message");
        if (ExpectedProcessId <= 0 || !Regex.IsMatch(ExpectedWindowHandle ?? "", @"\A0x[0-9a-fA-F]{1,16}\z")) throw new ArgumentException("Missing observed window identity");
        if (ExpiresAt <= DateTimeOffset.Now || ExpiresAt > DateTimeOffset.Now.AddMinutes(15)) throw new ArgumentException("Test request must expire within 15 minutes");
        if (ContinuationOf is not null && (!Regex.IsMatch(ContinuationOf, @"\A[a-zA-Z0-9_-]{1,80}\z") || ContinuationOf == RequestId)) throw new ArgumentException("Invalid continuation ID");
        if (SendDelayMs is < 0 or > 30000) throw new ArgumentException("Send delay must be between 0 and 30000 milliseconds");
    }
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Recipient, Message, ExpectedProcessId, ExpectedWindowHandle, VerifiedEmptyPlaceholder }))));
    // Legacy opt-in field retained for receipt fingerprint compatibility.
    // The UI/API now opt in automatically per the user's configured empty-cue policy.
    public bool IsEmptyDraft(string value) => value.Length == 0 || (VerifiedEmptyPlaceholder && value == "메시지 입력");
    public bool AcceptsInitialDraft(string value) => ContinuationOf is null ? IsEmptyDraft(value) : SameText(value, Message);
    public static bool SameText(string first, string second) => Normalize(first) == Normalize(second);
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
}

public sealed class TestSendReceipt
{
    public string RequestId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "prepared";
    public string Detail { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public bool EnterPosted { get; set; }
    public bool InputResponseTimedOut { get; set; }
    public int? SendDelayMs { get; set; }
    public string Kind { get; set; } = "text";
    public bool AttachmentQueued { get; set; }
    public bool? InputCleared { get; set; }
    public string ForegroundBefore { get; set; } = "";
    public string ForegroundAfter { get; set; } = "";
    [JsonIgnore] public string DisplayTime => StartedAt.ToLocalTime().ToString("MM.dd HH:mm:ss");
    [JsonIgnore] public string DisplayStatus => Status switch
    {
        "observed-in-chat" => "대화 확인 완료", "needs-review" => "대화 확인 필요",
        "blocked-before-input" => "입력 전 중단", "continued-after-visual-review" => "후속 요청으로 처리",
        _ => "확인 필요 · 작업 기록 미완료"
    };
}

public interface ITestSendTransport
{
    string ForegroundWindow { get; }
    void ValidateTarget(TestSendRequest request);
    string ReadDraft();
    void WriteDraft(string message);
    void PostEnter();
}

// Only the actual text insertion timeout may continue; selection, identity and access failures still stop.
public sealed class TextInputTimeoutException(Exception inner) : TimeoutException("Text input response timed out", inner);

public static class TestSender
{
    public static string DefaultLedger => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "test-sends");
    public static string ReceiptPath(string ledger, string requestId) => Path.Combine(ledger, requestId + ".json");

    public static TestSendReceipt SendOnce(TestSendRequest request, ITestSendTransport transport, string ledger)
    {
        request.Validate();
        // This method is synchronous so ownership and release stay on the same thread.
        using var gate = new Mutex(false, @"Local\KakaoRelay.TestSend");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another test send is in progress");
        try
        {
            Directory.CreateDirectory(ledger);
            var path = ReceiptPath(ledger, request.RequestId);
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(path), ReportStore.JsonOptions) ?? throw new InvalidDataException();
                if (existing.Fingerprint != request.Fingerprint()) throw new InvalidOperationException("Request ID conflicts with an earlier request");
                // Never replay any previously recorded request, even after a crash before completion.
                return existing;
            }
            var receipt = new TestSendReceipt { RequestId = request.RequestId, Recipient = request.Recipient, Fingerprint = request.Fingerprint(), SendDelayMs = request.SendDelayMs };
            Save(path, receipt);
            bool inputAttempted = false;
            try
            {
                transport.ValidateTarget(request);
                receipt.ForegroundBefore = transport.ForegroundWindow;
                if (!request.AcceptsInitialDraft(transport.ReadDraft())) throw new InvalidOperationException("Input contains a different draft; nothing was overwritten");
                if (request.ContinuationOf is { } previousId)
                {
                    var previousPath = ReceiptPath(ledger, previousId);
                    var previous = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(previousPath), ReportStore.JsonOptions) ?? throw new InvalidDataException();
                    if (previous.Status != "needs-review" || previous.Fingerprint != receipt.Fingerprint || previous.InputCleared != false || previous.FinishedAt is null)
                        throw new InvalidOperationException("The previous request cannot be continued");
                    previous.Status = "continued-after-visual-review";
                    previous.Detail = $"Unsent identical draft and disabled send button were visually checked. Continued by {request.RequestId}.";
                    Save(previousPath, previous);
                }
                receipt.Status = "inputting";
                Save(path, receipt);
                inputAttempted = true;
                try { transport.WriteDraft(request.Message); }
                catch (TextInputTimeoutException) when (request.SendDelayMs.HasValue)
                {
                    receipt.InputResponseTimedOut = true;
                    Save(path, receipt);
                }
                if (request.SendDelayMs is { } delay) Thread.Sleep(delay);
                else if (!TestSendRequest.SameText(transport.ReadDraft(), request.Message)) throw new InvalidOperationException("Input read-back differs from the requested message");
                transport.ValidateTarget(request);
                receipt.Status = "dispatching";
                Save(path, receipt); // Persist BEFORE the irreversible Enter request.
                transport.PostEnter();
                receipt.EnterPosted = true;
                receipt.Status = "needs-review";
                receipt.Detail = "Enter was queued once. Verify the new outgoing bubble; do not resend.";
                Save(path, receipt);
                // An empty edit control is evidence of UI processing, not proof of delivery.
                for (var i = 0; i < 8; i++)
                {
                    Thread.Sleep(150);
                    if (request.IsEmptyDraft(transport.ReadDraft())) { receipt.InputCleared = true; break; }
                }
                receipt.InputCleared ??= false;
            }
            catch (Exception error)
            {
                receipt.Status = inputAttempted ? "needs-review" : "blocked-before-input";
                receipt.Detail = error.Message;
            }
            receipt.ForegroundAfter = transport.ForegroundWindow;
            receipt.FinishedAt = DateTimeOffset.Now;
            Save(path, receipt);
            return receipt;
        }
        finally { gate.ReleaseMutex(); }
    }

    public static void RecordObserved(TestSendRequest request, string ledger)
    {
        RecordObserved(request.RequestId, ledger, request.Fingerprint());
    }

    public static void RecordObserved(string requestId, string ledger, string? expectedFingerprint = null)
    {
        if (!Regex.IsMatch(requestId, @"\A[a-zA-Z0-9_-]{1,80}\z")) throw new ArgumentException("Invalid request ID");
        using var gate = new Mutex(false, @"Local\KakaoRelay.TestSend");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another operation is in progress");
        try
        {
            var path = ReceiptPath(ledger, requestId);
            var receipt = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(path), ReportStore.JsonOptions) ?? throw new InvalidDataException();
            if ((expectedFingerprint is not null && receipt.Fingerprint != expectedFingerprint) || receipt.Status != "needs-review" || !receipt.EnterPosted)
                throw new InvalidOperationException("No pending send to verify");
            receipt.Status = "observed-in-chat";
            receipt.Detail = "The new outgoing bubble was visually confirmed in the recipient conversation. Recipient receipt/read status is not inferred.";
            Save(path, receipt);
        }
        finally { gate.ReleaseMutex(); }
    }

    public static List<TestSendReceipt> ReadHistory(string ledger)
    {
        var result = new List<TestSendReceipt>();
        if (!Directory.Exists(ledger)) return result;
        foreach (var path in Directory.EnumerateFiles(ledger, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(path), ReportStore.JsonOptions);
                if (item is not null && item.RequestId == Path.GetFileNameWithoutExtension(path)) result.Add(item);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(r => r.StartedAt).ToList();
    }

    internal static void Save(string path, TestSendReceipt receipt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ReportStore.JsonOptions);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
