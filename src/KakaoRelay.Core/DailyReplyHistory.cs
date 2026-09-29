using System.Text.Json;

namespace KakaoRelay.Core;

// Only the local date and first reply identity are stored, never chat content.
public sealed class DailyReplyHistory(string root, TimeProvider? clock = null)
{
    private sealed record Entry(DateOnly Day, string FirstReply);
    private static readonly object Sync = new();
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KakaoRelay", "daily-replies");
    public DateOnly Today => DateOnly.FromDateTime((clock ?? TimeProvider.System).GetLocalNow().DateTime);
    private string PathFor(LocalRoom room) => Path.Combine(root, AiConversation.Hash(AiSettings.RoomKey(room.Profile, room.Id)) + ".json");
    private Entry? Read(LocalRoom room)
    {
        var path = PathFor(room);
        return File.Exists(path) ? JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))
            ?? throw new InvalidDataException("오늘의 답장 기록을 읽을 수 없습니다.") : null;
    }
    public bool IsFirstReply(LocalRoom room, DateOnly day, string replyId)
    {
        lock (Sync)
        {
            var entry = Read(room);
            // Repeated delivery keeps the original attachment and its idempotency key.
            return entry is null || entry.Day != day || entry.FirstReply == replyId;
        }
    }
    public void RecordSent(LocalRoom room, DateOnly day, string replyId)
    {
        lock (Sync)
        {
            if (Read(room)?.Day == day) return;
            Directory.CreateDirectory(root);
            var path = PathFor(room);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Entry(day, replyId)));
            File.Move(path + ".tmp", path, true);
        }
    }
}
