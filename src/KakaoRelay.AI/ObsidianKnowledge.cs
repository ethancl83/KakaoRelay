using System.Text;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

public sealed class KnowledgeSettings
{
    public bool Enabled { get; set; }
    public string VaultPath { get; set; } = "";
    public void Validate()
    {
        if (VaultPath is null || VaultPath.Length > 4096 || VaultPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || (!string.IsNullOrWhiteSpace(VaultPath) && !Path.IsPathFullyQualified(VaultPath))
            || (Enabled && string.IsNullOrWhiteSpace(VaultPath)))
            throw new ArgumentException("지식베이스 보관함의 전체 폴더 경로를 입력하세요.");
    }
}

public sealed record KnowledgeExcerpt(string Source, int Part, string Text);
public sealed record KnowledgeSearch(List<KnowledgeExcerpt> Excerpts, int Notes, int Skipped, bool Limited);

// Reads ordinary Markdown directly; Obsidian need not be running. No plugins or index files.
public static class ObsidianKnowledge
{
    public const int MaxFiles = 2000, MaxFileBytes = 256 * 1024, MaxCharacters = 12000;
    private const int ChunkSize = 2000;

    public static string Query(ChatContext context, AiCommand command)
    {
        if (command.TargetMessageId is { } id)
            return context.Messages.LastOrDefault(m => m.Id == id && !m.Deleted)?.Text ?? "";
        if (!string.IsNullOrWhiteSpace(command.Instruction)) return command.Instruction;
        return string.Join("\n", context.Messages.Where(m => !m.Deleted).TakeLast(3).Select(m => m.Text));
    }

    public static Task<KnowledgeSearch> SearchAsync(KnowledgeSettings settings, string query, CancellationToken cancellation = default)
    {
        settings.Validate();
        cancellation.ThrowIfCancellationRequested();
        return settings.Enabled ? Task.Run(() => Search(settings, query, cancellation), cancellation)
            : Task.FromResult(new KnowledgeSearch([], 0, 0, false));
    }

    private static KnowledgeSearch Search(KnowledgeSettings settings, string query, CancellationToken cancellation)
    {
        settings.Validate();
        if (!settings.Enabled) return new([], 0, 0, false);
        var root = Path.GetFullPath(settings.VaultPath);
        CheckDirectory(root);
        var terms = Terms(query.Length > 8000 ? query[..8000] : query);
        var best = new List<(KnowledgeExcerpt Excerpt, int Score)>();
        var directories = new Stack<string>(); directories.Push(root);
        int notes = 0, skipped = 0, entries = 0, characters = 0;
        bool limited = false;
        while (directories.Count > 0 && !limited)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++entries > 10000) { limited = true; break; }
                    if (Path.GetFileName(path).StartsWith('.')) continue;
                    if (directory == root && Path.GetFileName(path) == "검토필요") continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                    if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (notes >= MaxFiles || characters >= 16_000_000) { limited = true; break; }
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (stream.Length > MaxFileBytes) { skipped++; continue; }
                        using var reader = new StreamReader(stream, Encoding.UTF8, true);
                        var buffer = new char[MaxFileBytes + 1];
                        var length = reader.ReadBlock(buffer, 0, buffer.Length);
                        if (length > MaxFileBytes) { skipped++; continue; }
                        var text = new string(buffer, 0, length);
                        notes++; characters += length;
                        var source = Path.GetRelativePath(root, path).Replace('\\', '/');
                        var titleTerms = Terms(source);
                        for (int offset = 0, part = 1; offset < text.Length; offset += ChunkSize, part++)
                        {
                            var chunk = text.Substring(offset, Math.Min(ChunkSize, text.Length - offset));
                            var bodyTerms = Terms(chunk);
                            var score = terms.Sum(t => (titleTerms.Contains(t) ? 3 : 0) + (bodyTerms.Contains(t) ? 1 : 0));
                            if (score == 0) continue;
                            best.Add((new(source, part, chunk), score));
                            best = best.OrderByDescending(x => x.Score).ThenBy(x => x.Excerpt.Source, StringComparer.Ordinal)
                                .ThenBy(x => x.Excerpt.Part).Take(6).ToList();
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (directory == root) throw new IOException("지식베이스 보관함을 읽을 수 없습니다.", e);
                skipped++;
            }
        }
        return new(best.Select(x => x.Excerpt).ToList(), notes, skipped, limited);
    }

    private static HashSet<string> Terms(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+"))
        {
            var word = match.Value;
            if (word.Length > 1) result.Add(word);
            // Korean particles otherwise prevent matching 질문: '휴가는' to note: '휴가'.
            if (word.Any(c => c is >= '\uAC00' and <= '\uD7A3'))
                for (var i = 0; i < word.Length - 1; i++) result.Add(word.Substring(i, 2));
        }
        return result;
    }

    public static async Task<string> SaveNoteAsync(KnowledgeSettings settings, string title, string body, LocalRoom? room = null, CancellationToken cancellation = default)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.VaultPath)) throw new ArgumentException("먼저 옵시디언 보관함을 선택하세요.");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || string.IsNullOrWhiteSpace(body) || body.Length > 100000)
            throw new ArgumentException("노트 제목(200자 이하)과 내용(10만 자 이하)을 확인하세요.");
        var root = Path.GetFullPath(settings.VaultPath);
        CheckDirectory(root);
        var folder = Path.Combine(root, "KakaoRelay");
        Directory.CreateDirectory(folder);
        CheckDirectory(folder);
        var path = Path.Combine(folder, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md");
        var heading = title.Replace('\r', ' ').Replace('\n', ' ');
        var metadata = room is null ? "사용자 작성 노트" : $"대화 분석 결과 · 방: {room.Title.Replace('\r', ' ').Replace('\n', ' ')}";
        var text = $"# {heading}\n\n> {metadata}\n> 저장: {DateTimeOffset.Now:O}\n\n{body.Trim()}\n";
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new ArgumentException("노트가 256KB를 넘습니다. 요약을 나누어 저장하세요.");
        cancellation.ThrowIfCancellationRequested();
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync(text.AsMemory(), cancellation);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void CheckDirectory(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("지식베이스 보관함을 찾을 수 없습니다. 폴더 경로를 확인하세요.");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("지식베이스에는 바로 가기나 심볼릭 링크가 아닌 실제 폴더를 선택하세요.");
    }
}
