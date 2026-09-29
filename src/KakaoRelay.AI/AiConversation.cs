using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace KakaoRelay.Core;
public sealed class AiConversation
{
    public string? SessionId { get; set; }
    public string? KnowledgeRevision { get; set; }
    public DateOnly? InstructionsDate { get; set; }
    public string? InstructionsRevision { get; set; }
    public bool NeedsInstructions(DateOnly day, string revision) => string.IsNullOrWhiteSpace(SessionId)
        || InstructionsDate != day || InstructionsRevision != revision;
    public HashSet<string> Seen { get; set; } = [];
    public static string Root => ProviderStorage.Root;
    public static string Fingerprint(LocalMessage message) => Hash(JsonSerializer.Serialize(message));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Folder(AiProviderSettings provider, BotPersona persona, AiCommand command) => ProviderStorage.ConversationFolder(provider.Id, Hash(JsonSerializer.Serialize(new { command.Profile, command.RoomId, provider, persona })));
    public static AiConversation Load(string folder, string? knowledgeRevision = null)
    {
        var path = Path.Combine(folder, "state.json");
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<AiConversation>(File.ReadAllText(path)) ?? new();
            return knowledgeRevision is not null && state.KnowledgeRevision != knowledgeRevision ? new() : state;
        }
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
