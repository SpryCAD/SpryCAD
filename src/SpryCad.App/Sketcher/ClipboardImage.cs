using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace SpryCad.App.Sketcher;

// (the clipboard calls the rest of the app uses: Avalonia 11.3 marks them obsolete in favor of its data transfer API)
#pragma warning disable CS0618

/// <summary>
/// An image on the clipboard, as file bytes: PNG data (what browsers, Office and the Snipping Tool copy), an image
/// file copied in Explorer, or (Windows) a plain bitmap, made a BMP file. With a name for it.
/// </summary>
public static class ClipboardImage
{
    static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static async Task<(byte[] data, string name)?> Read(IClipboard clip)
    {
        string[] formats;
        try { formats = await clip.GetFormatsAsync(); }
        catch (Exception ex) { App.Log(ex, "Clipboard formats"); return null; }
        foreach (var f in new[] { "PNG", "image/png" })
            if (formats.Contains(f) && Bytes(await clip.GetDataAsync(f)) is { Length: > 0 } png) return (png, "Pasted picture.png");
        if (formats.Contains(DataFormats.Files) && await clip.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> items)
            foreach (var item in items)
                if (item.TryGetLocalPath() is { } path && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    try { return (File.ReadAllBytes(path), Path.GetFileName(path)); }
                    catch (Exception ex) { App.Log(ex, "Clipboard file " + path); }
                }
        if (OperatingSystem.IsWindows() && Dib() is { } bmp) return (bmp, "Pasted picture.bmp");
        return null;
    }

    static byte[]? Bytes(object? o) => o switch
    {
        byte[] b => b,
        Stream s => ReadAll(s),
        _ => null,
    };

    static byte[] ReadAll(Stream s) { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }

    // ---- Windows: CF_DIB (a bitmap's header and pixels, no file header): a BMP file made of it

    const uint CF_DIB = 8;
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);

    static byte[]? Dib()
    {
        if (!IsClipboardFormatAvailable(CF_DIB)) return null;
        // (another program may have it open a moment)
        bool open = false;
        for (int i = 0; i < 5 && !(open = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(20);
        if (!open) return null;
        try
        {
            var h = GetClipboardData(CF_DIB);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                var size = (int)GlobalSize(h);
                if (size < 40) return null;
                var dib = new byte[size];
                Marshal.Copy(p, dib, 0, size);
                return BmpOf(dib);
            }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    static byte[]? BmpOf(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        int bitCount = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
        // after the header: the three color masks of a BI_BITFIELDS bitmap with the short header, then the palette
        int masks = headerSize == 40 && compression == 3 ? 12 : 0;
        int colors = used > 0 ? used : bitCount <= 8 ? 1 << bitCount : 0;
        int offset = 14 + headerSize + masks + colors * 4;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
