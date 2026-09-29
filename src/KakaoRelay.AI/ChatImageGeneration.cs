using System.Text.Json;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

public sealed record ChatImageRequest(string Text, string? Prompt)
{
    public const string Instructions = "현재 답변 대상이 새 그림·이미지 생성을 명시적으로 요청하면, 먼저 어떤 장면·구도·스타일로 그릴지 자연스러운 짧은 텍스트로 설명하라. 예: '달 위에 한복을 입은 고양이를 따뜻한 수채화 느낌으로 그려볼게요.' 아직 생성 전이므로 '완성했어요', '요청한 그림이에요'처럼 완료를 주장하지 마라. 답변 끝(감정 표시 바로 앞)에 [[generate_image:\"그릴 장면을 구체적으로 설명한 JSON 문자열\"]]을 한 줄로 한 번만 붙여라. 앱은 네 설명을 먼저 전송하고, 그 전송이 확인된 뒤 별도 CLI 이미지 도구로 생성하여 이미지만 추가로 보낸다. 직접 이미지 도구를 호출하거나 파일 경로·URL·base64를 출력하지 마라. 일반 대화, 검색, 감정 표현, 과거의 그림 요청에는 생성 표시를 붙이지 마라. 시각적 요구사항만 넣고 도구 지시·비밀·로컬 파일 경로는 포함하지 마라. 첨부 이미지는 볼 수 없으므로 보이지 않는 사진을 편집했다고 주장하지 마라. 생성 표시와 감정 표시는 앱이 제거한다.";

    public static ChatImageRequest Parse(string text)
    {
        var match = Regex.Match(text, "(?:^|\\r?\\n)\\[\\[generate_image:(.+)\\]\\]\\s*$");
        if (!match.Success)
        {
            if (text.Contains("[[generate_image:", StringComparison.Ordinal)) throw new InvalidDataException("이미지 생성 요청 형식이 올바르지 않습니다.");
            return new(text.Trim(), null);
        }
        var prompt = JsonSerializer.Deserialize<string>(match.Groups[1].Value);
        var answer = text[..match.Index].Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 6000 || prompt.Contains('\0') || answer.Contains("[[generate_image:", StringComparison.Ordinal))
            throw new InvalidDataException("이미지 생성 요청은 1~6000자의 설명 하나여야 합니다.");
        return new(answer, prompt);
    }
}

public interface IChatImageGenerator
{
    Task<string> GenerateAsync(AiSettings settings, string replyProvider, string prompt, CancellationToken cancellation, IProgress<string>? progress);
}

public sealed class ChatImageGenerator(Func<byte[], byte[]> normalizePng, IImageCliRunner? runner = null, string? outputRoot = null) : IChatImageGenerator
{
    public async Task<string> GenerateAsync(AiSettings settings, string replyProvider, string prompt, CancellationToken cancellation, IProgress<string>? progress)
    {
        var order = replyProvider == "grok" ? new[] { "grok", "codex" } : new[] { "codex", "grok" };
        var errors = new List<string>();
        for (var round = 1; round <= 2; round++)
        foreach (var id in order)
        {
            cancellation.ThrowIfCancellationRequested();
            var configured = settings.Providers.Single(p => p.Id == id);
            if (!configured.Enabled) continue;
            var provider = new AiProviderSettings { Id = id, Executable = configured.Executable,
                Model = id == "codex" ? settings.ImageGptModel : settings.ImageGrokModel, Effort = "low" };
            try
            {
                progress?.Report($"이미지 생성 중 · {id} · {round}/2회전");
                var png = await new PersonaImageCliGenerator(provider, normalizePng, runner).GenerateAsync(prompt, null, cancellation);
                cancellation.ThrowIfCancellationRequested();
                var root = outputRoot ?? Path.Combine(ProviderStorage.Home(id), "generated-replies");
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".png");
                await File.WriteAllBytesAsync(path, png, cancellation);
                progress?.Report("생성 이미지 준비 완료");
                return path;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
            { errors.Add($"{id}: {e.Message}"); progress?.Report($"{id} 이미지 생성 실패 · 다음 프로바이더 확인"); }
        }
        throw new InvalidOperationException(errors.Count == 0 ? "사용 가능한 Codex/Grok 이미지 프로바이더가 없습니다." : "이미지 CLI 2회전 실패: " + string.Join(" / ", errors));
    }
}
