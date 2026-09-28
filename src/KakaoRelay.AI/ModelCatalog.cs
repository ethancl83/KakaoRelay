using System.Text.Json;
namespace KakaoRelay.Core;

public static class ModelCatalog
{
    public static IReadOnlyList<string> Choices(string provider, string current = "")
    {
        var models = new List<string> { "" };
        if (provider == "codex")
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            try
            {
                using var cache = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "models_cache.json")));
                foreach (var model in cache.RootElement.GetProperty("models").EnumerateArray())
                {
                    var slug = model.GetProperty("slug").GetString();
                    if (slug is not null && slug != "codex-auto-review") models.Add(slug);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        if (provider == "grok") models.AddRange(["grok-build", "grok-4.7"]);
        if (provider == "claude") models.AddRange(["sonnet", "opus", "haiku", "fable"]);
        if (!string.IsNullOrWhiteSpace(current)) models.Add(current);
        return models.Distinct(StringComparer.Ordinal).ToArray();
    }
}
