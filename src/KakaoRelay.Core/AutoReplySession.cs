using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace KakaoRelay.Core;

public sealed class AutoReplySession(AiService ai, Func<ApiSendCommand, Task<TestSendReceipt>> send, DailyReplyHistory? history = null)
{
    private readonly DailyReplyHistory dailyReplies = history ?? new(DailyReplyHistory.DefaultRoot);
    public event Action<string>? StatusChanged;
    public static LocalMessage? Candidate(IEnumerable<LocalMessage> messages, string watermark, string selfId, string trigger) =>
        Candidates(messages, watermark, selfId, trigger).FirstOrDefault();
    public static List<LocalMessage> Candidates(IEnumerable<LocalMessage> messages, string watermark, string selfId, string trigger) =>
        messages.Where(m => BigInteger.Parse(m.Id) > BigInteger.Parse(watermark) && m.AuthorId != selfId && !m.Deleted && m.Type == 1 && MatchesTrigger(m.Text, trigger)).OrderBy(m => BigInteger.Parse(m.Id)).ToList();
    private static bool MatchesTrigger(string text, string trigger)
    {
        if (trigger.Length == 0) return true; // Immediate/context modes do not require a mention.
        text = text.TrimStart();
        return text.StartsWith(trigger, StringComparison.OrdinalIgnoreCase) && (text.Length == trigger.Length
            || char.IsWhiteSpace(text[trigger.Length]) || ".,!?;:~，。！？".Contains(text[trigger.Length]));
    }
    private sealed class SessionProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private static string Identity(LocalRoom room, LocalMessage candidate) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{room.Profile}:{room.Id}:{candidate.Id}")))[..40];

    public async Task SendReplyAsync(LocalRoom room, LocalMessage candidate, AiResult result, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 8000)
            throw new InvalidOperationException("자동 답변 길이가 유효하지 않아 중지했습니다.");
        var identity = Identity(room, candidate);
        var day = dailyReplies.Today;
        var sendImage = result.ImagePath is not null || (result.Attachment is not null
            && (result.Emotion != "neutral" || dailyReplies.IsFirstReply(room, day, identity)));
        if (sendImage)
        {
            StatusChanged?.Invoke($"{room.Title} · {(result.ImagePath is not null ? "생성한" : PersonaExpression.Find(result.Emotion).Name)} 이미지 먼저 전송 중");
            var imageReceipt = await send(new("bot-image-" + identity, room.Title, "")
            { ImagePath = result.ImagePath, Attachment = result.ImagePath is null ? result.Attachment : null });
            if (!imageReceipt.EnterPosted || imageReceipt.InputCleared != true)
                throw new InvalidOperationException("이미지 발송 결과가 불확실해 텍스트 답변을 보내지 않고 중지했습니다. 발송 이력과 대화를 확인하세요.");
            dailyReplies.RecordSent(room, day, identity);
            cancellation.ThrowIfCancellationRequested();
        }
        StatusChanged?.Invoke($"{room.Title} · 답변 텍스트 전송 중");
        var receipt = await send(new("bot-" + identity, room.Title, result.Text));
        if (!receipt.EnterPosted || receipt.InputCleared != true)
            throw new InvalidOperationException("답변 발송 결과 확인이 필요해 자동 답장을 중지했습니다. 발송 이력과 대화를 확인하세요.");
        dailyReplies.RecordSent(room, day, identity);
    }

    public async Task RunAsync(LocalRoom room, string selfId, string trigger, CancellationToken cancellation)
    {
        if (!BigInteger.TryParse(selfId, out var self) || self <= 0) throw new ArgumentException("내 사용자 ID가 필요합니다.");
        var settings = ai.Settings.Load(); var mode = settings.ReplyModeForRoom(room.Profile, room.Id);
        var baseline = await ai.Chats.ReadAsync(room.Profile, room.Id, 1, cancellation);
        var watermark = baseline.Messages.LastOrDefault()?.Id ?? "0";
        StatusChanged?.Invoke($"{room.Title} 대기 중 · {mode} · {settings.PollSeconds}초 폴링 · 시작 이후 새 메시지만 답장");
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), cancellation);
            var context = await ai.Chats.ReadAsync(room.Profile, room.Id, 500, cancellation);
            if (context.Messages.Count == 500 && BigInteger.Parse(context.Messages[0].Id) > BigInteger.Parse(watermark))
                throw new InvalidOperationException("새 메시지가 읽기 범위를 초과해 중지했습니다. 누락된 호출을 확인하세요.");
            settings = ai.Settings.Load();
            mode = settings.ReplyModeForRoom(room.Profile, room.Id);
            trigger = settings.TriggerForRoom(room.Profile, room.Id);
            var pending = Candidates(context.Messages, watermark, selfId, mode == "trigger" ? trigger : "");
            if (mode == "context")
            {
                var approved = new List<LocalMessage>();
                foreach (var candidate in pending)
                {
                    var recent = context.Messages.Where(m => BigInteger.Parse(m.Id) <= BigInteger.Parse(candidate.Id)).TakeLast(20);
                    var needed = await ai.ReplyJudge.ShouldReplyAsync(settings, settings.ResolvePersona(room.Profile, room.Id).Persona, recent, cancellation,
                        new SessionProgress(s => StatusChanged?.Invoke($"{room.Title} · {s}")));
                    StatusChanged?.Invoke($"{room.Title} · 메시지 {candidate.Id} · {(needed ? "답변 필요" : "맥락상 답변 생략")}");
                    if (needed) approved.Add(candidate);
                }
                pending = approved;
            }
            foreach (var candidate in pending)
            {
            // Consume the trigger before generation; failures are not retried as new sends.
            StatusChanged?.Invoke($"{room.Title} 답변 생성 중 · 이번 묶음 {pending.Count}개 호출");
            var result = await ai.GenerateAsync(new(room.Profile, room.Id, "reply", $"메시지 ID {candidate.Id}에만 답하라. 다른 호출은 별도로 처리된다. 호출 내용: {PromptJson.Serialize(candidate.Text)}") { TargetMessageId = candidate.Id }, cancellation, new SessionProgress(s => StatusChanged?.Invoke($"{room.Title} · {s}")));
            await SendReplyAsync(room, candidate, result, cancellation);
            StatusChanged?.Invoke($"{room.Title} · {result.Provider} 답변 전송 요청 처리 · 다음 호출 대기");

            }
            watermark = context.Messages.LastOrDefault()?.Id ?? watermark;
            StatusChanged?.Invoke($"{room.Title} 대기 중 · 마지막 확인 {DateTime.Now:HH:mm:ss} · {mode} · {settings.PollSeconds}초 폴링");
        }
    }
}
