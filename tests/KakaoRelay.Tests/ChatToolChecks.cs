using System.Text.Json;
using KakaoRelay.Core;

internal static class ChatToolChecks
{
    private const string Directive = "[[generate_image:\"달 위의 한복 고양이\"]]";
    private sealed class Runner : IAiRunner
    {
        public string Reply = "달 위의 한복 고양이를 수채화로 그려볼게요.\n" + Directive + "\n[[emotion:happy]]";
        public string Prompt = "";
        public Task<string> RunAsync(AiProviderSettings provider, string prompt, int timeoutSeconds, CancellationToken cancellation)
        { Prompt = prompt; return Task.FromResult(Reply); }
    }
    private sealed class Images : IChatImageGenerator
    {
        public int Calls;
        public string Prompt = "";
        public bool Fail;
        public Func<Task>? BeforeGenerate;
        public async Task<string> GenerateAsync(AiSettings settings, string replyProvider, string prompt, CancellationToken cancellation, IProgress<string>? progress)
        { Calls++; Prompt = prompt; if (BeforeGenerate is not null) await BeforeGenerate(); if (Fail) throw new InvalidDataException("fixture failure"); return Path.GetFullPath("generated.png"); }
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
        check(sent.Count == 2 && sent[0].Message == result.Text && sent[1].ImagePath == imagePath && sent[1].Attachment is null,
            "Requested image follows the explanation even after today's first reply and without persona images enabled");
        images.Fail = false;
        var beforePlan = images.Calls;
        runner.Reply = "달 위의 한복 고양이를 수채화로 그려볼게요.\n" + Directive + "\n[[emotion:neutral]]";
        var plan = await service.GenerateAsync(new(room.Profile, room.Id, "reply"), deferImages: true);
        check(images.Calls == beforePlan && plan.ImagePath is null && plan.ImagePrompt == "달 위의 한복 고양이" && plan.Text.Contains("그려볼게요"),
            "Automatic reply preparation returns the drawing plan without starting image generation");
        var introDone = new TaskCompletionSource<TestSendReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        images.BeforeGenerate = () => { imageStarted.SetResult(); return imageDone.Task; };
        sent.Clear();
        var staged = new AutoReplySession(service, c =>
        {
            sent.Add(c);
            return c.IsImage ? Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }) : introDone.Task;
        }, new(Path.Combine(root, "staged-image-days")));
        var sending = staged.SendReplyAsync(room, candidate with { Id = "903" }, plan, default);
        check(sent.Count == 1 && sent[0].Message == plan.Text && images.Calls == beforePlan && !sending.IsCompleted,
            "Only the drawing explanation is sent while its delivery receipt is pending");
        introDone.SetResult(new TestSendReceipt { EnterPosted = true, InputCleared = true });
        await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(sent.Count == 1 && images.Calls == beforePlan + 1 && !sending.IsCompleted,
            "Image generation starts only after explanation delivery and sends nothing while generating");
        imageDone.SetResult(); await sending;
        check(sent.Count == 2 && sent[1].IsImage && sent[1].ImagePath is not null,
            "Completed drawing sends only the image, without repeating the explanation");
        images.BeforeGenerate = null;
        var beforeFailure = images.Calls;
        var uncertain = new AutoReplySession(service, _ => Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = false }));
        try { await uncertain.SendReplyAsync(room, candidate, plan, default); check(false, "Uncertain introduction"); }
        catch (InvalidOperationException) { check(images.Calls == beforeFailure, "Uncertain explanation delivery prevents image generation"); }
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = new AutoReplySession(service, _ => { cancel.Cancel(); return Task.FromResult(new TestSendReceipt { EnterPosted = true, InputCleared = true }); }, new(Path.Combine(root, "cancel-image-days")));
            try { await cancelled.SendReplyAsync(room, candidate, plan, cancel.Token); check(false, "Cancelled image"); }
            catch (OperationCanceledException) { check(images.Calls == beforeFailure, "Stop after the explanation prevents image generation"); }
        }
        images.Fail = true; sent.Clear();
        await session.SendReplyAsync(room, candidate with { Id = "904" }, plan, default);
        check(sent.Count == 2 && sent.All(c => !c.IsImage) && sent[0].Message == plan.Text
            && sent[1].Message.Contains("생성하지 못") && sent[1].RequestId.StartsWith("bot-image-error-"),
            "Generation failure follows the explanation with a distinct failure notice and no image");
        foreach (var provider in new[] { "codex", "grok", "claude" })
        {
            var args = CliAiRunner.Arguments(new() { Id = provider }, root);
            check(provider == "codex" ? args.Contains("web_search=\"live\"") : provider == "grok"
                ? args.Contains("web_search,web_fetch") && !args.Contains("--disable-web-search") && args[args.IndexOf("--max-turns") + 1] == "8"
                : args.Contains("WebSearch,WebFetch") && args.Contains("--allowedTools"), $"{provider} CLI enables web tools");
        }
    }
}
