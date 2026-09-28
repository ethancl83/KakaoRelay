using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KakaoRelay.Core;

// Standard SQLCipher 4 pages. Keys are validated by HMAC before any plaintext is used.
public static class ChatCipher
{
    public const int PageSize = 4096;
    private static byte[] MacKey(ReadOnlySpan<byte> first, byte[] key)
    {
        var salt = first[..16].ToArray();
        for (var i = 0; i < salt.Length; i++) salt[i] ^= 0x3a;
        return Rfc2898DeriveBytes.Pbkdf2(key, salt, 2, HashAlgorithmName.SHA512, 32);
    }
    private static bool Verify(ReadOnlySpan<byte> page, uint number, byte[] macKey)
    {
        if (page.Length != PageSize || number == 0) return false;
        var start = number == 1 ? 16 : 0;
        var input = new byte[4032 - start + 4];
        page[start..4032].CopyTo(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(input.Length - 4), number);
        return CryptographicOperations.FixedTimeEquals(HMACSHA512.HashData(macKey, input), page[4032..4096]);
    }
    public static bool VerifyKey(byte[] first, byte[] key) => first.Length >= PageSize && key.Length == 32 && Verify(first.AsSpan(0, PageSize), 1, MacKey(first, key));

    public static byte[] Decrypt(byte[] database, byte[] wal, byte[] key)
    {
        if (database.Length < PageSize || database.Length % PageSize != 0 || !VerifyKey(database, key))
            throw new InvalidDataException("지원하지 않는 DB 형식이거나 키 검증에 실패했습니다.");
        var macKey = MacKey(database, key);
        var merged = database.ToArray();
        if (wal.Length > 32)
        {
            uint Be(int offset) => BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset, 4));
            var magic = Be(0);
            if (magic is not (0x377f0682 or 0x377f0683) || Be(8) != PageSize || Be(4) != 3007000)
                throw new InvalidDataException("지원하지 않는 WAL 형식입니다.");
            uint s0 = 0, s1 = 0;
            void Checksum(ReadOnlySpan<byte> bytes)
            {
                unchecked
                {
                    for (var i = 0; i < bytes.Length; i += 8)
                    {
                        var a = magic == 0x377f0682 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i, 4)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4));
                        var b = magic == 0x377f0682 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 4, 4)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 4, 4));
                        s0 += a + s1; s1 += b + s0;
                    }
                }
            }
            Checksum(wal.AsSpan(0, 24));
            if (s0 != Be(24) || s1 != Be(28)) throw new InvalidDataException("WAL 헤더 검증 실패");
            var frames = new List<(uint Page, int Offset)>();
            var committed = 0; uint finalSize = 0;
            for (var offset = 32; offset + 4120 <= wal.Length; offset += 4120)
            {
                if (!wal.AsSpan(offset + 8, 8).SequenceEqual(wal.AsSpan(16, 8))) break;
                Checksum(wal.AsSpan(offset, 8)); Checksum(wal.AsSpan(offset + 24, PageSize));
                if (s0 != Be(offset + 16) || s1 != Be(offset + 20)) break; // Uncommitted/torn tail.
                var page = Be(offset);
                if (page == 0 || page > 131072 || !Verify(wal.AsSpan(offset + 24, PageSize), page, macKey))
                    throw new InvalidDataException("WAL 페이지 인증 실패");
                frames.Add((page, offset + 24));
                if (Be(offset + 4) != 0) { committed = frames.Count; finalSize = Be(offset + 4); }
            }
            if (committed > 0)
            {
                if (finalSize > 131072) throw new InvalidDataException("DB 크기 제한 초과");
                Array.Resize(ref merged, checked((int)finalSize * PageSize));
                foreach (var (page, offset) in frames.Take(committed))
                    if (page <= finalSize) wal.AsSpan(offset, PageSize).CopyTo(merged.AsSpan(((int)page - 1) * PageSize));
            }
        }
        using var aes = Aes.Create(); aes.Key = key;
        var plain = new byte[merged.Length];
        for (var offset = 0; offset < merged.Length; offset += PageSize)
        {
            var page = merged.AsSpan(offset, PageSize);
            if (!Verify(page, (uint)(offset / PageSize + 1), macKey)) throw new InvalidDataException("DB 페이지 인증 실패");
            var start = offset == 0 ? 16 : 0;
            aes.DecryptCbc(page[start..4016], page[4016..4032], plain.AsSpan(offset + start), PaddingMode.None);
        }
        "SQLite format 3\0"u8.CopyTo(plain);
        // The committed WAL is already folded in; an in-memory snapshot must not try to open a WAL file.
        plain[18] = plain[19] = 1;
        // Keep the codec's 80 reserved bytes: b-tree cells are laid out with this usable page size.
        if (plain[20] != 80) throw new InvalidDataException("예상하지 못한 SQLite 페이지 형식");
        CryptographicOperations.ZeroMemory(macKey);
        return plain;
    }
}

// Windows' SQLite engine operates on a private in-memory database; plaintext is never written to disk.
internal sealed class MemoryChatDb : IDisposable
{
    private nint db;
    public MemoryChatDb(byte[] bytes)
    {
        if (sqlite3_open_v2(":memory:", out db, 6, null) != 0) throw new InvalidDataException("SQLite 열기 실패");
        var buffer = sqlite3_malloc64((ulong)bytes.Length);
        if (buffer == 0) { Dispose(); throw new OutOfMemoryException(); }
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        if (sqlite3_deserialize(db, "main", buffer, bytes.Length, bytes.Length, 5) != 0)
        { sqlite3_free(buffer); Dispose(); throw new InvalidDataException("SQLite 읽기 실패"); }
        CryptographicOperations.ZeroMemory(bytes);
        try { if (Query("PRAGMA quick_check").Single()[0] != "ok") throw new InvalidDataException("DB 무결성 검사 실패"); }
        catch { Dispose(); throw; }
    }
    public List<string[]> Query(string sql)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var statement, 0) != 0) throw new InvalidDataException("카카오톡 DB 스키마가 지원되지 않습니다.");
        try
        {
            var result = new List<string[]>(); int status;
            while ((status = sqlite3_step(statement)) == 100)
            {
                var row = new string[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "";
                result.Add(row);
            }
            if (status != 101) throw new InvalidDataException("SQLite 조회 실패");
            return result;
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Dispose() { if (db != 0) { sqlite3_close(db); db = 0; } }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_deserialize(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, nint data, long size, long capacity, uint flags);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_malloc64(ulong size);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint pointer);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out nint statement, nint tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_text(nint statement, int column);
}

public sealed class LocalChatReader : IChatReader, IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, byte[]> keys = [];
    private List<LocalRoom> rooms = [];
    private readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kakao", "KakaoTalk", "users");
    public Task<List<LocalRoom>> RoomsAsync(CancellationToken cancellation = default) => Locked(() =>
    {
        Discover(cancellation); var found = new List<LocalRoom>();
        foreach (var path in Directory.Exists(root) ? Directory.GetDirectories(root) : [])
        {
            var profile = Path.GetFileName(path);
            if (!Regex.IsMatch(profile, "\\A[a-f0-9]{40}\\z")) continue;
            var list = Path.Combine(path, "chat_data", "chatListInfo.edb");
            if (!keys.ContainsKey(list)) continue;
            using var db = Open(list);
            foreach (var row in db.Query("SELECT chatId,chatRoomTitle FROM chatRoomList"))
            {
                if (!Regex.IsMatch(row[0], "\\A[0-9]+\\z")) continue;
                var log = Path.Combine(path, "chat_data", $"chatLogs_{row[0]}.edb");
                if (File.Exists(log)) found.Add(new(profile, row[0], string.IsNullOrWhiteSpace(row[1]) ? $"방 {row[0]}" : row[1], keys.ContainsKey(log)));
            }
        }
        rooms = found.OrderByDescending(r => r.Readable).ThenBy(r => r.Title).ToList();
        return rooms.ToList();
    }, cancellation);
    public Task<ChatContext> ReadAsync(string profile, string roomId, int limit, CancellationToken cancellation = default) => Locked(() =>
    {
        if (limit is < 1 or > 500) throw new ApiFailure(400, "invalid_limit", "메시지 수는 1~500입니다.");
        var room = rooms.SingleOrDefault(r => r.Profile == profile && r.Id == roomId)
            ?? throw new ApiFailure(404, "room_not_loaded", "로컬 대화방을 먼저 새로고침하세요.");
        var folder = Path.Combine(root, profile);
        var log = Path.Combine(folder, "chat_data", $"chatLogs_{roomId}.edb");
        if (!keys.ContainsKey(log)) Discover(cancellation);
        var names = new Dictionary<string, string>();
        var users = Path.Combine(folder, "TalkUserDB.edb");
        if (keys.ContainsKey(users))
        {
            using var people = Open(users);
            foreach (var r in people.Query("SELECT userId,coalesce(nullif(friendNickName,''),nickName,'') FROM talkUser")) names[r[0]] = r[1];
        }
        using var db = Open(log);
        var messages = db.Query($"SELECT logId,authorId,sendAt,type,message,coalesce(deleted,0) FROM chatLogs ORDER BY logId DESC LIMIT {limit}")
            .Select(r => new LocalMessage(r[0], r[1], names.GetValueOrDefault(r[1], r[1]), DateTimeOffset.FromUnixTimeSeconds(long.Parse(r[2])), int.Parse(r[3]), r[4], r[5] != "0"))
            .Reverse().ToList();
        return new ChatContext(room with { Readable = true }, messages, DateTimeOffset.Now);
    }, cancellation);
    private async Task<T> Locked<T>(Func<T> action, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try { return await Task.Run(action, cancellation); } finally { gate.Release(); }
    }
    private MemoryChatDb Open(string path)
    {
        if (!keys.TryGetValue(path, out var key)) throw new ApiFailure(409, "key_unavailable", "카카오톡에서 해당 방을 연 뒤 로컬 대화방을 새로고침하세요.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var database = SharedRead(path); var walPath = path + "-wal";
            var wal = File.Exists(walPath) ? SharedRead(walPath) : [];
            if (!database.AsSpan().SequenceEqual(SharedRead(path)) || !wal.AsSpan().SequenceEqual(File.Exists(walPath) ? SharedRead(walPath) : [])) continue;
            return new MemoryChatDb(ChatCipher.Decrypt(database, wal, key));
        }
        throw new ApiFailure(409, "database_busy", "대화 데이터가 변경 중입니다. 잠시 후 다시 읽어주세요.");
    }
    private static byte[] SharedRead(string path, int maximum = 512 * 1024 * 1024)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new InvalidDataException("지원하는 DB 크기를 초과했습니다.");
        var bytes = new byte[file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private void Discover(CancellationToken cancellation)
    {
        foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
        keys.Clear();
        if (!Directory.Exists(root)) throw new ApiFailure(404, "kakao_data_missing", "카카오톡 로컬 데이터가 없습니다.");
        var pages = new Dictionary<string, byte[]>();
        foreach (var profile in Directory.GetDirectories(root).Where(p => Regex.IsMatch(Path.GetFileName(p), "\\A[a-f0-9]{40}\\z")))
        {
            var chat = Path.Combine(profile, "chat_data");
            var files = Directory.Exists(chat) ? Directory.GetFiles(chat, "chatLogs_*.edb").Append(Path.Combine(chat, "chatListInfo.edb")).Append(Path.Combine(profile, "TalkUserDB.edb")) : [];
            foreach (var path in files.Where(File.Exists))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length < 4096) continue;
                var page = new byte[4096]; file.ReadExactly(page); pages[path] = page;
            }
        }
        var candidates = KakaoMemoryKeys.Read(cancellation);
        try
        {
            foreach (var candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var (path, page) in pages)
                    if (!keys.ContainsKey(path) && ChatCipher.VerifyKey(page, candidate)) keys[path] = candidate.ToArray();
            }
        }
        finally { foreach (var candidate in candidates) CryptographicOperations.ZeroMemory(candidate); }
    }
    public void Dispose() { foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key); keys.Clear(); gate.Dispose(); }
}

internal static class KakaoMemoryKeys
{
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo
    { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nuint RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32")] private static extern nuint VirtualQueryEx(nint handle, nint address, out MemoryInfo info, nuint size);
    [DllImport("kernel32")] private static extern bool ReadProcessMemory(nint handle, nint address, byte[] buffer, nuint size, out nuint read);
    public static List<byte[]> Read(CancellationToken cancellation)
    {
        var keys = new List<byte[]>(); var processes = Process.GetProcessesByName("KakaoTalk");
        if (processes.Length == 0) throw new ApiFailure(409, "kakao_not_running", "카카오톡을 실행하고 대화방을 열어주세요.");
        var readAny = false; long total = 0;
        foreach (var process in processes)
        {
            using (process)
            {
                var handle = OpenProcess(0x410, false, process.Id);
                if (handle == 0) continue;
                try
                {
                    nuint address = 0;
                    while (VirtualQueryEx(handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (info.RegionSize == 0 || (nuint)info.BaseAddress + info.RegionSize <= address) break;
                        address = (nuint)info.BaseAddress + info.RegionSize;
                        if (info.State != 0x1000 || info.Type != 0x20000 || (info.Protect & 0x101) != 0) continue;
                        for (nuint offset = 0; offset < info.RegionSize; offset += 1048476)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var buffer = new byte[(int)Math.Min((nuint)1048576, info.RegionSize - offset)];
                            total += buffer.Length;
                            if (total > 512L * 1024 * 1024) return keys;
                            if (!ReadProcessMemory(handle, info.BaseAddress + (nint)offset, buffer, (nuint)buffer.Length, out var count)) continue;
                            readAny = true;
                            try
                            {
                                for (var i = 0; i + 67 <= (int)count; i++)
                                {
                                    if (buffer[i] is not (120 or 88) || buffer[i + 1] != 39) continue;
                                    var length = i + 99 <= (int)count && buffer[i + 98] == 39 ? 96 : buffer[i + 66] == 39 ? 64 : 0;
                                    if (length == 0) continue;
                                    var hex = buffer.AsSpan(i + 2, length);
                                    if (hex.ContainsAnyExcept("0123456789abcdefABCDEF"u8)) continue;
                                    var key = Convert.FromHexString(Encoding.ASCII.GetString(hex[..64]));
                                    if (!keys.Any(k => k.AsSpan().SequenceEqual(key))) keys.Add(key);
                                    else CryptographicOperations.ZeroMemory(key);
                                }
                            }
                            finally { CryptographicOperations.ZeroMemory(buffer); }
                        }
                    }
                }
                finally { CloseHandle(handle); }
            }
        }
        if (!readAny) throw new ApiFailure(409, "memory_unavailable", "카카오톡 메모리를 읽을 수 없습니다. 앱과 카카오톡의 실행 권한을 확인하세요.");
        return keys;
    }
}
