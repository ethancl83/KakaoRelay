using KakaoRelay.Core;
using System.Text.Json;

internal static class ImageCliChecks
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII=");
    private sealed class Runner : IImageCliRunner
    {
        public byte[]? Reference;
        public string Prompt = "", Folder = "";
        public bool ReturnReference, Fail;
        public async Task<string> RunAsync(AiProviderSettings p, string folder, string prompt, string? reference, CancellationToken ct)
        {
            Prompt = prompt; Folder = folder;
            Reference = reference is null ? null : await File.ReadAllBytesAsync(reference, ct);
            if (Fail) return "{\"error\":\"no image tool\"}";
            var output = Path.Combine(folder, "new.png"); await File.WriteAllBytesAsync(output, Png, ct);
            return JsonSerializer.Serialize(new { image_path = ReturnReference ? reference : output });
        }
    }
    public static async Task RunAsync(string root, Action<bool,string> check)
    {
        var settings = new AiSettings { ImageGptModel = "custom-gpt", ImageGrokModel = "custom-grok" };
        var store = new AiSettingsStore(Path.Combine(root, "image-cli-settings.json")); store.Save(settings);
        check(store.Load().ImageGptModel == "custom-gpt" && store.Load().ImageGrokModel == "custom-grok", "Image CLI model settings survive restart");
        check(NativeImageCliRunner.ExtractImageResult("I'll edit the image.\n{\"image_path\":\"/generated/result.png\"}\nDone.").Contains("/generated/result.png"), "Image CLI extracts the final result despite progress commentary");
        foreach (var id in new[] { "codex", "grok" })
        {
            var provider = new AiProviderSettings { Id = id, Model = id == "codex" ? PersonaImageCliGenerator.GptModel : PersonaImageCliGenerator.GrokModel, Executable = Environment.ProcessPath! };
            var runner = new Runner(); var generator = new PersonaImageCliGenerator(provider, b => b, runner);
            var output = await generator.GenerateAsync("한국어 style & $(literal)", Png, default);
            check(output.SequenceEqual(Png) && runner.Reference!.SequenceEqual(Png) && !Directory.Exists(runner.Folder), id + " image edit uses exact reference bytes and cleans the request directory");
            check(runner.Prompt.Contains("Python/API fallback") && runner.Prompt.Contains("Do not use API keys") && runner.Prompt.Contains(id == "codex" ? "imagegen skill" : "image_gen / image_edit"), id + " image job requires the native image tool and forbids API fallback");
            var info = NativeImageCliRunner.StartInfo(provider, root, Path.Combine(root, "reference.png"));
            check(!info.UseShellExecute && info.CreateNoWindow && !info.Environment.ContainsKey("OPENAI_API_KEY") && !info.Environment.ContainsKey("XAI_API_KEY") && !info.Environment.ContainsKey("GROK_API_KEY"), id + " image CLI hides its window and excludes API-key environment variables");
            check(id == "codex" ? info.ArgumentList.Contains("--image") && info.ArgumentList.Contains("features.shell_tool=false") && info.ArgumentList.Contains("image_generation") : info.ArgumentList.Contains("image_gen,image_edit") && info.ArgumentList.Contains("dontAsk"), id + " native tool and reference options are separate CLI arguments");
            runner.ReturnReference = true;
            try { await generator.GenerateAsync("edit", Png, default); check(false,"reference masquerading as output"); } catch(InvalidDataException) { check(true,id + " cannot reuse the unchanged reference as a generated image"); }
            runner.ReturnReference = false; runner.Fail = true;
            try { await generator.GenerateAsync("generate", null, default); check(false,"missing image"); } catch(InvalidDataException) { check(true,id + " missing native tool result fails without API fallback"); }
        }
        var outside = Path.Combine(root,"outside.png"); await File.WriteAllBytesAsync(outside,Png);
        try { PersonaImageCliGenerator.ValidateOutput(outside, Path.Combine(root,"job"),null,DateTime.UtcNow); check(false,"outside output"); } catch(InvalidDataException) { check(true,"CLI cannot import arbitrary files outside generated-image roots"); }
        var native = new NativeImageCliRunner();
        var fixture = new AiProviderSettings { Id="codex", Model="fixture", Executable=Environment.ProcessPath! };
        const string literal = "{\"image_path\":\"한글 & $(literal)\"}";
        var reply = await native.RunAsync(fixture,root,literal,null,default);
        check(reply==literal,"Native image CLI sends Unicode and metacharacters through stdin literally");
        using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await native.RunAsync(fixture,root,"HANG",null,stop.Token); check(false,"hung image CLI"); } catch(OperationCanceledException) { check(true,"Cancelling image generation terminates its CLI process"); }
    }
}
