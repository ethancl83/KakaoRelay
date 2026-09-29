using System.Text.Json;
using KakaoRelay.Core;

internal static class ChatToolChecks
{
    private const string Directive = "[[generate_image:\"달 위의 한복 고양이\"]]";
    private sealed class Runner : IAiRunner
    {
        public string Reply = "요청한 그림이에요.\n" + Directive + "\n[[emotion:happy]]";
        public string Prompt = "";
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        { Prompt = prompt; return Task.FromResult(Reply); }
    }
    private sealed class Images : IChatImageGenerator
    {
        public int Calls;
        public string Prompt = "";
        public bool Fail;
        public Task<string> GenerateAsync(AiSettings settings, string replyProvider, string prompt, CancellationToken cancellation, IProgress<string>? progress)
        { Calls++; Prompt = prompt; if (Fail) throw new InvalidDataException("fixture failure"); return Task.FromResult(Path.GetFullPath("generated.png")); }
    }
    private sealed class ImageCli : IImageCliRunner
    {
        public List<string> Calls = [];
        public bool FailAll;
        public async Task<string> RunAsync(AiProviderSettings provider, string folder, string prompt, string? referencePath, CancellationToken cancellation)
        {
            Calls.Add(provider.Id);
            if (provider.Id == "codex" || FailAll) throw new InvalidDataException("fixture unavailable");
            var path = Path.Combine(folder, "generated.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII="), cancellation);
            return JsonSerializer.Serialize(new { image_path = path });
        }
    }
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var reader = new ImportedChatReader();
        var room = reader.Import("image-request.txt", "--------------- 2026년 9월 29일 ---------------\n[테스트] [오후 1:00] 달 위의 한복 고양이 그려줘");
        var store = new AiSettingsStore(Path.Combine(root, "chat-image-settings.json"));
        var runner = new Runner(); var images = new Images();
        var service = new AiService(reader, store, runner, images);
        var result = await service.GenerateAsync(new(room.Profile, room.Id, "reply") { TargetMessageId = "123" });
        check(result.ImagePath is not null && result.Attachment is null && result.Emotion == "neutral" && !result.Text.Contains("[[") && images.Prompt == "달 위의 한복 고양이",
            "Reply image directive invokes generation and returns a generated attachment without exposing control markers");
        check(runner.Prompt.Contains("웹검색") && runner.Prompt.Contains("출처 URL") && runner.Prompt.Contains("123"),
            "Chat instructions permit sourced web answers and identify the current target message");
        runner.Reply = "평범한 답변\n[[emotion:neutral]]";
        var ordinary = await service.GenerateAsync(new(room.Profile, room.Id, "reply"));
        check(images.Calls == 1 && ordinary.ImagePath is null, "Ordinary replies never invoke the image generator");
        runner.Reply = "만들었어요.\n" + Directive + "\n[[emotion:neutral]]"; images.Fail = true;
        var failed = await service.GenerateAsync(new(room.Profile, room.Id, "reply"));
        check(failed.ImagePath is null && failed.Attachment is null && failed.ImageError is not null && failed.Text.Contains("생성하지 못"),
            "Failed generation replaces success claims with a truthful failure reply and no emotion image");
        foreach (var malformed in new[] { "[[generate_image:/secret.png]]", Directive + "\n" + Directive, "[[generate_image:\"\"]]", Directive + "\nextra" })
        {
            try { ChatImageRequest.Parse(malformed); check(false, "Malformed directive accepted"); }
            catch (Exception e) when (e is JsonException or InvalidDataException) { check(true, "Malformed image directives cannot become file attachments"); }
        }
        var settings = store.Load(); var cli = new ImageCli();
        var generator = new ChatImageGenerator(png => png, cli, Path.Combine(root, "generated-replies"));
        var imagePath = await generator.GenerateAsync(settings, "claude", "고양이", default, null);
        check(cli.Calls.SequenceEqual(new[] { "codex", "grok" }) && File.Exists(ImageSender.ValidateLocalImage(imagePath)),
            "Claude image requests use Codex then Grok fallback and retain validated output after temporary job cleanup");
        cli.Calls.Clear(); cli.FailAll = true;
        try { await generator.GenerateAsync(settings, "grok", "고양이", default, null); check(false, "Image fallback exhausted"); }
        catch (InvalidOperationException) { check(cli.Calls.SequenceEqual(new[] { "grok", "codex", "grok", "codex" }), "Image fallback stops after two rounds"); }
        var sent = new List<ApiSendCommand>();
        var session = new AutoReplySession(service, c => { sent.Add(c); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); }, new(Path.Combine(root, "image-send-days")));
        var candidate = new LocalMessage("901", "other", "tester", DateTimeOffset.Now, 1, "그려줘", false);
        await session.SendReplyAsync(room, candidate, ordinary, default);
        sent.Clear();
        await session.SendReplyAsync(room, candidate with { Id = "902" }, result with { ImagePath = imagePath }, default);
        check(sent.Count == 2 && sent[0].ImagePath == imagePath && sent[0].Attachment is null && sent[1].Message == result.Text,
            "Requested generated image precedes text even after today's first reply and without persona images enabled");
        foreach (var provider in new[] { "codex", "grok", "claude" })
        {
            var args = CliAiRunner.Arguments(new() { Id = provider }, root);
            check(provider == "codex" ? args.Contains("web_search=\"live\"") : provider == "grok"
                ? args.Contains("web_search,web_fetch") && !args.Contains("--disable-web-search") && args[args.IndexOf("--max-turns") + 1] == "8"
                : args.Contains("WebSearch,WebFetch") && args.Contains("--allowedTools"), $"{provider} CLI enables web tools");
        }
    }
}
