using System.Buffers.Binary;
using System.Security.Cryptography;
using KakaoRelay.Core;

internal static class CipherChecks
{
    public static void Run(Action<bool, string> check)
    {
        var key = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(16);
        var macSalt = salt.Select(b => (byte)(b ^ 0x3a)).ToArray();
        var macKey = Rfc2898DeriveBytes.Pbkdf2(key, macSalt, 2, HashAlgorithmName.SHA512, 32);
        byte[] Page(byte marker)
        {
            var result = new byte[4096]; salt.CopyTo(result, 0);
            var plain = new byte[4000]; plain[0] = 0x10; plain[1] = 0; plain[2] = plain[3] = 2; plain[4] = 80; plain[50] = marker;
            var iv = RandomNumberGenerator.GetBytes(16); iv.CopyTo(result, 4016);
            using var aes = Aes.Create(); aes.Key = key;
            aes.EncryptCbc(plain, iv, result.AsSpan(16, 4000), PaddingMode.None);
            var authenticated = new byte[4020]; result.AsSpan(16, 4016).CopyTo(authenticated); BinaryPrimitives.WriteUInt32LittleEndian(authenticated.AsSpan(4016), 1);
            HMACSHA512.HashData(macKey, authenticated).CopyTo(result, 4032); return result;
        }
        var original = Page(10); var updated = Page(20); var unfinished = Page(30);
        var plain = ChatCipher.Decrypt(original, [], key);
        check(plain.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) && plain[20] == 80 && plain[66] == 10 && plain[18] == 1, "SQLCipher reader restores SQLite header and reserved-byte layout");
        var corrupted = original.ToArray(); corrupted[100] ^= 1;
        check(!ChatCipher.VerifyKey(corrupted, key) && !ChatCipher.VerifyKey(original, new byte[32]), "Cipher rejects tampered pages and wrong keys before parsing");
        var wal = new byte[32 + 2 * 4120];
        void Be(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(at, 4), value);
        Be(0, 0x377f0682); Be(4, 3007000); Be(8, 4096); Be(16, 123); Be(20, 456);
        uint s0 = 0, s1 = 0;
        void Sum(int at, int length)
        {
            unchecked { for (var i = at; i < at + length; i += 8) { s0 += BinaryPrimitives.ReadUInt32LittleEndian(wal.AsSpan(i, 4)) + s1; s1 += BinaryPrimitives.ReadUInt32LittleEndian(wal.AsSpan(i + 4, 4)) + s0; } }
        }
        Sum(0, 24); Be(24, s0); Be(28, s1);
        void Frame(int at, byte[] data, uint commit)
        {
            Be(at, 1); Be(at + 4, commit); Be(at + 8, 123); Be(at + 12, 456); data.CopyTo(wal, at + 24);
            Sum(at, 8); Sum(at + 24, 4096); Be(at + 16, s0); Be(at + 20, s1);
        }
        Frame(32, updated, 1); Frame(4152, unfinished, 0);
        check(ChatCipher.Decrypt(original, wal, key)[66] == 20, "WAL applies committed updates but excludes a later uncommitted frame");
        wal[4200] ^= 1;
        check(ChatCipher.Decrypt(original, wal, key)[66] == 20, "Torn WAL tail cannot replace the last committed snapshot");
        wal[24] ^= 1;
        try { ChatCipher.Decrypt(original, wal, key); check(false, "bad WAL"); } catch (InvalidDataException) { check(true, "Corrupt WAL header is rejected"); }
        LocalMessage Msg(string id, string author, string text, int type = 1, bool deleted = false) => new(id, author, author, DateTimeOffset.Now, type, text, deleted);
        var rows = new[] { Msg("8", "2", "@bot old"), Msg("10", "1", "@bot own"), Msg("11", "2", "@bot deleted", deleted: true), Msg("12", "2", "ordinary"), Msg("13", "2", "@bot attachment", type: 2), Msg("14", "2", "@bot question") };
        check(AutoReplySession.Candidate(rows, "9", "1", "@bot")?.Id == "14", "Auto reply excludes baseline, self, deleted, nontext and untriggered messages");
        check(AutoReplySession.Candidate(rows, "14", "1", "@bot") is null, "Auto reply watermark prevents repeated generation for the same trigger");
        var repeated = rows.Concat(new[] { Msg("16", "2", "@bot third"), Msg("15", "2", "@bot second") });
        check(AutoReplySession.Candidates(repeated, "9", "1", "@bot").Select(m => m.Id).SequenceEqual(new[] { "14", "15", "16" }), "All repeated triggers are queued in message order without collapsing to the last call");
    }
}
