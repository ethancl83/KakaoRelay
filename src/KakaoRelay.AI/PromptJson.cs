using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace KakaoRelay.Core;

// Prompts are UTF-8 text, not HTML. Keep Korean readable while retaining JSON quoting and structure.
public static class PromptJson
{
    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.Create(UnicodeRanges.All);
    private static readonly JsonSerializerOptions Options = new() { Encoder = Encoder };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
