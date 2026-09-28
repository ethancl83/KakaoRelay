namespace KakaoRelay.Core;

public static class MagentaTransparency
{
    public static bool IsTransparentCopy(ReadOnlySpan<byte> source, ReadOnlySpan<byte> candidate)
    {
        if (source.Length != candidate.Length || source.Length % 4 != 0) return false;
        var minimum = 0;
        var maximum = 100;
        var changed = false;
        for (var i = 0; i < source.Length; i += 4)
        {
            var original = source.Slice(i, 4);
            var result = candidate.Slice(i, 4);
            if (original[3] == 0) { if (!original.SequenceEqual(result)) return false; continue; }
            var distance = Math.Max(Math.Max(255 - original[0], original[1]), 255 - original[2]);
            if (original.SequenceEqual(result)) maximum = Math.Min(maximum, distance - 1);
            else if (result[0] == 0 && result[1] == 0 && result[2] == 0 && result[3] == 0)
            { minimum = Math.Max(minimum, distance); changed = true; }
            else return false;
            if (minimum > maximum) return false;
        }
        return changed;
    }
    // Input is straight-alpha BGRA, not premultiplied. Other colors and alpha values stay intact.
    public static int Apply(Span<byte> bgra, int tolerance, CancellationToken cancellation = default)
    {
        if (tolerance is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (bgra.Length % 4 != 0) throw new ArgumentException("BGRA 픽셀 데이터가 올바르지 않습니다.", nameof(bgra));
        var changed = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
            if (bgra[i + 3] != 0 && 255 - bgra[i] <= tolerance && bgra[i + 1] <= tolerance && 255 - bgra[i + 2] <= tolerance)
            {
                bgra.Slice(i, 4).Clear();
                changed++;
            }
        }
        return changed;
    }
}
