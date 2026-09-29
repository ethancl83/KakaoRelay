using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace KakaoRelay.Core;
public sealed class AiConversation
{
    public string? SessionId { get; set; }
    public HashSet<string> Seen { get; set; } = [];
    public static string Root => ProviderStorage.Root;
    public static string Fingerprint(LocalMessage message) => Hash(JsonSerializer.Serialize(message));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Folder(AiProviderSettings provider, BotPersona persona, AiCommand command, string? knowledgeRevision = null) => ProviderStorage.ConversationFolder(provider.Id, Hash(JsonSerializer.Serialize(new { command.Profile, command.RoomId, provider, persona, knowledgeRevision })));
    public static AiConversation Load(string folder)
    {
        var path = Path.Combine(folder, "state.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AiConversation>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); }
    }
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
}
