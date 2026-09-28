using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

// Portable input adapter. No account credentials or platform-specific DB assumptions.
public sealed class ImportedChatReader : IChatReader
{
    private readonly object sync = new();
    private readonly Dictionary<string, ChatContext> contexts = [];
    public LocalRoom Import(string filename, string content)
    {
        ArgumentNullException.ThrowIfNull(filename); ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 10_000_000) throw new ArgumentException("파일은 10MB 이하로 가져오세요.");
        if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var groups = document.RootElement.EnumerateArray().GroupBy(r => r.TryGetProperty("roomId", out var rid) ? rid.ToString() : "").ToList();
                if (groups.Count > 1)
                {
                    LocalRoom? first = null;
                    foreach (var group in groups)
                    {
                        var name = group.First().TryGetProperty("room", out var titleValue) ? titleValue.GetString() : group.Key;
                        var imported = Import((name ?? "대화") + ".json", JsonSerializer.Serialize(group.ToArray())); first ??= imported;
                    }
                    return first!;
                }
            }
        }
        var title = Path.GetFileNameWithoutExtension(filename);
        if (string.IsNullOrWhiteSpace(title)) title = "가져온 대화";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..24];
        var room = new LocalRoom("import", id, title, true);
        var messages = filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ParseJson(content) : ParseText(content);
        if (messages.Count == 0) throw new ArgumentException("메시지를 인식하지 못했습니다. 카카오톡 한국어 TXT 내보내기 또는 KakaoRelay JSON을 사용하세요.");
        lock (sync) contexts[id] = new(room, messages, DateTimeOffset.Now);
        return room;
    }
    public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default)
    { cancellation.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult(contexts.Values.Select(c => c.Room).ToList()); }
    public Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (limit is < 1 or > 500) throw new ArgumentException("메시지 수는 1~500입니다.");
        lock (sync)
        {
            if (profile != "import" || !contexts.TryGetValue(roomId, out var context)) throw new ApiFailure(404, "room_missing", "대화를 먼저 가져오세요.");
            return Task.FromResult(context with { Messages = context.Messages.TakeLast(limit).ToList(), ReadAt = DateTimeOffset.Now });
        }
    }
    private static List<LocalMessage> ParseJson(string content)
    {
        using var doc = JsonDocument.Parse(content);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("messages");
        var result = new List<LocalMessage>();
        foreach (var row in rows.EnumerateArray())
        {
            string Get(params string[] keys) { foreach (var key in keys) if (row.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null) return v.ToString(); return ""; }
            if (!DateTimeOffset.TryParse(Get("timeKST", "time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new ArgumentException("JSON 메시지 시각이 올바르지 않습니다.");
            var id = Get("messageId", "id");
            var deleted = Get("deleted");
            result.Add(new(id.Length == 0 ? (result.Count + 1).ToString(CultureInfo.InvariantCulture) : id, Get("authorId"), Get("author"), time, int.TryParse(Get("type"), out var type) ? type : 1, Get("message", "text"), deleted is "1" or "true" or "True"));
        }
        return result;
    }
    private static List<LocalMessage> ParseText(string content)
    {
        var messages = new List<LocalMessage>();
        var datePattern = new Regex(@"(?<y>\d{4})년\s*(?<m>\d{1,2})월\s*(?<d>\d{1,2})일", RegexOptions.CultureInvariant);
        var windows = new Regex(@"^\[(?<name>.+?)\]\s*\[(?<ampm>오전|오후)\s*(?<h>\d{1,2}):(?<min>\d{2})\]\s?(?<text>.*)$", RegexOptions.CultureInvariant);
        var mobile = new Regex(@"^\d{4}년\s*\d{1,2}월\s*\d{1,2}일\s*(?<ampm>오전|오후)\s*(?<h>\d{1,2}):(?<min>\d{2}),\s*(?<name>.+?)\s*:\s?(?<text>.*)$", RegexOptions.CultureInvariant);
        DateTime? date = null;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var match = windows.Match(line); var mobileMatch = mobile.Match(line);
            if (mobileMatch.Success) match = mobileMatch;
            var day = datePattern.Match(line);
            // Date changes only on a date heading or a recognized timestamp, never inside message text.
            if (day.Success && (mobileMatch.Success || (line.StartsWith("---", StringComparison.Ordinal) && line.EndsWith("---", StringComparison.Ordinal)) || line == day.Value))
            {
                date = new DateTime(int.Parse(day.Groups["y"].Value), int.Parse(day.Groups["m"].Value), int.Parse(day.Groups["d"].Value));
                if (!match.Success) continue;
            }
            if (match.Success && date.HasValue)
            {
                var hour = int.Parse(match.Groups["h"].Value); var minute = int.Parse(match.Groups["min"].Value);
                if (hour is < 1 or > 12 || minute > 59) throw new ArgumentException("TXT 시각을 인식할 수 없습니다.");
                hour = hour % 12 + (match.Groups["ampm"].Value == "오후" ? 12 : 0);
                var author = match.Groups["name"].Value;
                var at = new DateTimeOffset(date.Value.AddHours(hour).AddMinutes(minute), TimeSpan.FromHours(9));
                messages.Add(new((messages.Count + 1).ToString(CultureInfo.InvariantCulture), author, author, at, 1, match.Groups["text"].Value, false));
            }
            else if (messages.Count > 0) messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n" + line };
        }
        return messages;
    }
}
