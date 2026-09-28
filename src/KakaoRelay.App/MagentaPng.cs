using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public static class MagentaPng
{
    public static bool IsTransparentCopy(byte[] source, byte[] candidate)
    {
        static (int Width, int Height, byte[] Pixels) Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 32_000_000) throw new InvalidDataException("이미지 해상도가 너무 큽니다.");
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            return (bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }
        var original = Decode(source); var result = Decode(candidate);
        return original.Width == result.Width && original.Height == result.Height
            && MagentaTransparency.IsTransparentCopy(original.Pixels, result.Pixels);
    }
    public static (byte[] Png, int ChangedPixels) Convert(byte[] input, int tolerance, CancellationToken cancellation = default)
    {
        using var stream = new MemoryStream(input);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 32_000_000) throw new InvalidDataException("이미지 해상도가 너무 큽니다.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var changed = MagentaTransparency.Apply(pixels, tolerance, cancellation);
        if (changed == 0) return ([], 0);
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        using var output = new MemoryStream(); encoder.Save(output);
        cancellation.ThrowIfCancellationRequested();
        return (output.ToArray(), changed);
    }
}
