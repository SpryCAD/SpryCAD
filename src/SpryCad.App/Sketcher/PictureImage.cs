using SkiaSharp;

namespace SpryCad.App.Sketcher;

/// <summary>
/// Sketch pictures' images decoded (PNG, JPEG, BMP, GIF, WebP), as straight-alpha RGBA rows from the top, turned
/// upright as a camera's orientation tag says and at most <see cref="MaxSide"/> pixels on a side (a texture any
/// graphics card takes; a photo larger than that shows no more detail at sketch scale).
/// </summary>
public sealed class PictureImage
{
    public const int MaxSide = 4096;

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
    /// <summary>The image's own size in pixels, upright (before any reduction to <see cref="MaxSide"/>).</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    PictureImage(int w, int h, byte[] rgba, int sw, int sh) { Width = w; Height = h; Rgba = rgba; SourceWidth = sw; SourceHeight = sh; }

    /// <summary>The same image with other pixels (processed: the same size).</summary>
    public PictureImage With(byte[] rgba) => new(Width, Height, rgba, SourceWidth, SourceHeight);

    /// <summary>This image at most <paramref name="maxSide"/> pixels on a side (itself when it is already).</summary>
    public PictureImage Reduced(int maxSide)
    {
        var scale = (double)maxSide / Math.Max(Width, Height);
        if (scale >= 1) return this;
        int w = Math.Max(1, (int)Math.Round(Width * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
        using var b = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(Rgba, 0, b.GetPixels(), Rgba.Length);
        using var r = b.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
        return new(w, h, r.Bytes, SourceWidth, SourceHeight);
    }

    /// <summary>The color at a point of the image (0 to 1 across, 0 to 1 down from its top).</summary>
    public (byte r, byte g, byte b) At(double u, double v)
    {
        int x = Math.Clamp((int)(u * Width), 0, Width - 1), y = Math.Clamp((int)(v * Height), 0, Height - 1);
        int i = (y * Width + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2]);
    }

    /// <summary>The image the bytes hold, or null when they are not one this can read.</summary>
    public static PictureImage? Decode(byte[] data)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec == null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var raw = new SKBitmap(info);
            var r = codec.GetPixels(info, raw.GetPixels());
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            OpaqueIfClear(raw);
            using var upright = Orient(raw, codec.EncodedOrigin);
            int sw = upright.Width, sh = upright.Height;
            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(sw, sh));
            int w = Math.Max(1, (int)Math.Round(sw * scale)), h = Math.Max(1, (int)Math.Round(sh * scale));
            using var sized = w == sw && h == sh ? upright.Copy() : upright.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
            if (sized == null) return null;
            return new PictureImage(w, h, sized.Bytes, sw, sh);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.Log(ex, "Decode picture"); return null; }
    }

    // a 32-bit bitmap whose fourth byte is not used (every pixel's 0, as screenshots on the clipboard are): opaque, not invisible
    static void OpaqueIfClear(SKBitmap b)
    {
        var px = b.Bytes;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) return;
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(px, 0, b.GetPixels(), px.Length);
    }

    /// <summary>The image the bytes hold as a PNG file (upright, at most <see cref="MaxSide"/> on a side), or null when they are not one.</summary>
    public static byte[]? ToPng(byte[] data)
    {
        if (Decode(data) is not { } img) return null;
        using var b = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, b.GetPixels(), img.Rgba.Length);
        using var image = SKImage.FromBitmap(b);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png?.ToArray();
    }

    // a photo stored sideways with a tag saying how to turn it (as phones save them): each pixel moved to where it shows
    static SKBitmap Orient(SKBitmap b, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft) return b.Copy();
        int w = b.Width, h = b.Height;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? h : w, oh = swap ? w : h;
        var o = new SKBitmap(new SKImageInfo(ow, oh, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var src = b.Bytes; var dst = new byte[ow * oh * 4];
        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                var (dx, dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - sx, sy),
                    SKEncodedOrigin.BottomRight => (w - 1 - sx, h - 1 - sy),
                    SKEncodedOrigin.BottomLeft => (sx, h - 1 - sy),
                    SKEncodedOrigin.LeftTop => (sy, sx),
                    SKEncodedOrigin.RightTop => (h - 1 - sy, sx),
                    SKEncodedOrigin.RightBottom => (h - 1 - sy, w - 1 - sx),
                    _ => (sy, w - 1 - sx), // LeftBottom
                };
                Buffer.BlockCopy(src, (sy * w + sx) * 4, dst, (dy * ow + dx) * 4, 4);
            }
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, o.GetPixels(), dst.Length);
        return o;
    }
}
