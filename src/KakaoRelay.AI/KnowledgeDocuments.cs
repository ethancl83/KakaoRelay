using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace KakaoRelay.Core;

public sealed record KnowledgeNote(string Path, string Source, long Bytes);
public sealed record KnowledgeLibrary(List<KnowledgeNote> Notes, int Skipped, bool Limited);

public static class KnowledgeDocuments
{
    public const int MaxUploadBytes = 20 * 1024 * 1024, MaxTextCharacters = 2_000_000;
    public static readonly string[] Extensions = [".md", ".txt", ".pdf", ".docx"];

    public static Task<KnowledgeLibrary> ListAsync(string vault, CancellationToken cancellation = default) => Task.Run(() =>
    {
        var root = Path.GetFullPath(vault);
        ObsidianKnowledge.CheckDirectory(root);
        var notes = new List<KnowledgeNote>();
        var pending = new Stack<string>(); pending.Push(root);
        int entries = 0, skipped = 0;
        bool limited = false;
        while (pending.Count > 0 && !limited)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++entries > 10000) { limited = true; break; }
                    if (Path.GetFileName(path).StartsWith('.')) continue;
                    if (directory == root && Path.GetFileName(path) == "검토필요") continue;
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped++; continue; }
                    if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(path); continue; }
                    if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (notes.Count == ObsidianKnowledge.MaxFiles) { limited = true; break; }
                    var bytes = new FileInfo(path).Length;
                    if (bytes > ObsidianKnowledge.MaxFileBytes) { skipped++; continue; }
                    notes.Add(new(path, Path.GetRelativePath(root, path).Replace('\\', '/'), bytes));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { if (directory == root) throw; skipped++; }
        }
        return new KnowledgeLibrary(notes.OrderBy(n => n.Source, StringComparer.OrdinalIgnoreCase).ToList(), skipped, limited);
    }, cancellation);

    // Stage a complete document in a hidden directory, then publish all parts together.
    // Source files stay untouched and repeated imports get unique directories.
    public static Task<IReadOnlyList<string>> ImportAsync(string vault, string source, CancellationToken cancellation = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(vault);
        ObsidianKnowledge.CheckDirectory(root);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new ArgumentException("MD, TXT, PDF, DOCX 파일을 선택하세요.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxUploadBytes) throw new ArgumentException("파일당 20MB 이하만 추가할 수 있습니다.");
        string body;
        if (extension == ".pdf")
        {
            using var document = PdfDocument.Open(input);
            if (document.NumberOfPages > 200) throw new ArgumentException("PDF는 200쪽 이하로 나누어 추가하세요.");
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellation.ThrowIfCancellationRequested();
                var content = ContentOrderTextExtractor.GetText(page);
                if (string.IsNullOrWhiteSpace(content)) continue;
                text.AppendLine($"\n## {page.Number}쪽\n").AppendLine(content);
                CheckTextLength(text.Length);
            }
            body = text.ToString();
        }
        else if (extension == ".docx")
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = archive.GetEntry("word/document.xml") ?? throw new ArgumentException("Word 문서 내용을 찾을 수 없습니다.");
            if (entry.Length > MaxUploadBytes) throw new ArgumentException("Word 문서의 압축 해제 내용이 20MB를 넘습니다.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxUploadBytes });
            var xml = XDocument.Load(reader);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var text = new StringBuilder();
            foreach (var paragraph in xml.Descendants(w + "p"))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var node in paragraph.Descendants())
                    if (node.Name == w + "t") text.Append(node.Value);
                    else if (node.Name == w + "tab") text.Append('\t');
                    else if (node.Name == w + "br") text.AppendLine();
                text.AppendLine(); CheckTextLength(text.Length);
            }
            body = text.ToString();
        }
        else
        {
            using var reader = new StreamReader(input, new UTF8Encoding(false, true), true);
            var buffer = new char[MaxTextCharacters + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            CheckTextLength(count);
            body = new string(buffer, 0, count);
            if (body.Contains('\0')) throw new ArgumentException("텍스트 파일이 아닙니다. UTF-8 텍스트로 저장해서 추가하세요.");
        }
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("추출할 텍스트가 없습니다. 스캔 PDF·이미지는 먼저 문자 인식(OCR)이 필요합니다.");
        cancellation.ThrowIfCancellationRequested();
        var imports = Path.Combine(root, "자료");
        Directory.CreateDirectory(imports); ObsidianKnowledge.CheckDirectory(imports);
        var title = Path.GetFileNameWithoutExtension(source);
        var safeTitle = string.Concat(title.Take(60).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var id = $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var stage = Path.Combine(imports, ".import-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(imports, id);
        Directory.CreateDirectory(stage);
        try
        {
            var names = new List<string>();
            for (int start = 0, part = 1; start < body.Length; part++)
            {
                cancellation.ThrowIfCancellationRequested();
                int length = Math.Min(50000, body.Length - start);
                if (length > 1 && start + length < body.Length && char.IsHighSurrogate(body[start + length - 1])) length--;
                var name = $"{part:D3}.md";
                var text = $"# {title.Replace('\r', ' ').Replace('\n', ' ')} · {part}\n\n> 원본: {Path.GetFileName(source)}\n\n" + body.Substring(start, length);
                File.WriteAllText(Path.Combine(stage, name), text, new UTF8Encoding(false));
                names.Add(name); start += length;
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return names.Select(n => Path.Combine(destination, n)).ToArray();
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }, cancellation);

    private static void CheckTextLength(int count)
    {
        if (count > MaxTextCharacters) throw new ArgumentException("추출한 내용이 200만 자를 넘습니다. 문서를 나누어 추가하세요.");
    }
}
