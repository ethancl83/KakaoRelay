using System.Security.Cryptography;

namespace KakaoRelay.Core;

public interface IImageSendTransport
{
    void Validate(TestSendRequest target);
    void Attach(string path);
    bool ConfirmPreview();
}

public static class ImageSender
{
    public static string TransferRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KakaoRelay", "attachments");
    public static string ValidateLocalImage(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !File.Exists(path)) throw new ApiFailure(400, "image_unavailable", "PC에 존재하는 로컬 이미지 절대 경로를 지정하세요.");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12]; var count = file.Read(header);
        var valid = extension switch
        {
            ".png" => count >= 8 && header[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
            ".jpg" or ".jpeg" => count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
            ".gif" => count >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)),
            ".bmp" => count >= 2 && header[0] == 66 && header[1] == 77,
            ".webp" => count >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid) throw new ApiFailure(400, "invalid_image", "이미지 확장자와 파일 내용을 확인하세요. PNG, JPEG, GIF, BMP, WebP를 지원합니다.");
        return Path.GetFullPath(path);
    }
    public static string ResolvePath(PersonaAttachment attachment)
    {
        var store = new PersonaImageCatalog(AiSettings.ImageRoot(attachment.PersonaId)).Store(attachment.Provider);
        if (!store.Load().Images.Any(i => i.Id == attachment.ImageId)) throw new ApiFailure(400, "image_unavailable", "등록된 페르소나 이미지만 보낼 수 있습니다.");
        var path = store.ImagePath(attachment.ImageId);
        if (!File.Exists(path)) throw new ApiFailure(400, "image_unavailable", "이미지 파일이 없습니다.");
        return path;
    }
    public static string FileIdentity(string path) => "image:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static TestSendReceipt SendOnce(TestSendRequest request, string imagePath, IImageSendTransport transport, string ledger, string? transferRoot = null)
    {
        request.Validate();
        using var gate = new Mutex(false, @"Local\KakaoRelay.TestSend");
        bool acquired; try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another send is in progress");
        try
        {
            Directory.CreateDirectory(ledger);
            var path = TestSender.ReceiptPath(ledger, request.RequestId);
            if (File.Exists(path))
            {
                var previous = System.Text.Json.JsonSerializer.Deserialize<TestSendReceipt>(File.ReadAllText(path), ReportStore.JsonOptions) ?? throw new InvalidDataException();
                if (previous.Fingerprint != request.Fingerprint()) throw new InvalidOperationException("Image request ID conflict");
                return previous;
            }
            var receipt = new TestSendReceipt { RequestId = request.RequestId, Recipient = request.Recipient, Fingerprint = request.Fingerprint(), Kind = "image" };
            TestSender.Save(path, receipt);
            var attempted = false;
            try
            {
                if (FileIdentity(imagePath) != request.Message) throw new InvalidDataException("이미지 파일이 변경되었습니다.");
                transport.Validate(request);
                // Give KakaoTalk a stable file in Documents; keep it after an uncertain/queued send.
                // The private persona library may be regenerated or edited while KakaoTalk uploads.
                var staging = transferRoot ?? TransferRoot;
                Directory.CreateDirectory(staging);
                var transferPath = Path.Combine(staging, Guid.NewGuid().ToString("N") + Path.GetExtension(imagePath).ToLowerInvariant());
                File.Copy(imagePath, transferPath, false);
                if (FileIdentity(transferPath) != request.Message) throw new InvalidDataException("전송용 이미지가 원본과 다릅니다.");
                receipt.Status = "dispatching"; TestSender.Save(path, receipt);
                attempted = true;
                transport.Attach(transferPath);
                receipt.AttachmentQueued = true; TestSender.Save(path, receipt);
                var closed = transport.ConfirmPreview();
                receipt.EnterPosted = true; receipt.InputCleared = closed;
                receipt.Status = "needs-review";
                receipt.Detail = closed ? "Image preview send was requested once and preview closed. Verify the outgoing image; do not resend." : "Image send was requested but preview closure is uncertain. Do not resend.";
            }
            catch (Exception e) { receipt.Status = attempted ? "needs-review" : "blocked-before-input"; receipt.Detail = e.Message; }
            receipt.FinishedAt = DateTimeOffset.Now; TestSender.Save(path, receipt); return receipt;
        }
        finally { gate.ReleaseMutex(); }
    }
}
