using SpryCad.Core.Sketching;

namespace SpryCad.App.Sketcher;

/// <summary>
/// Sketch pictures' images, decoded and processed (<see cref="PictureFilter"/>) off the UI thread, each result kept until
/// it is taken (the last few: those of a setting typed digit by digit, never asked for again, go). Edges are found on
/// the image at most <see cref="EdgeSide"/> on a side.
/// </summary>
public static class PictureProcessor
{
    public const int EdgeSide = 2048;

    // the last few images decoded, by file (sampling a color; the start of processing)
    static readonly List<(string file, PictureImage img)> _originals = new();
    static readonly List<((string, PictureLook) key, PictureImage? img)> _done = new();
    static readonly HashSet<(string, PictureLook)> _running = new();
    static readonly object _lock = new();

    /// <summary>The file's image as it is (decoded here, the first time), or null when it is not one.</summary>
    public static PictureImage? Original(string file, byte[] bytes)
    {
        lock (_lock)
            foreach (var (f, img) in _originals) if (f == file) return img;
        var decoded = PictureImage.Decode(bytes);
        if (decoded == null) return null;
        lock (_lock)
        {
            _originals.Insert(0, (file, decoded));
            if (_originals.Count > 3) _originals.RemoveAt(_originals.Count - 1);
        }
        return decoded;
    }

    /// <summary>
    /// The file's image processed as <paramref name="look"/> says, once made (taken: asked again, it is made again);
    /// null while it is being made, <paramref name="ready"/> called (on the UI thread) when it is.
    /// </summary>
    public static PictureImage? Processed(string file, byte[] bytes, PictureLook look, Action ready, out bool failed)
    {
        failed = false;
        lock (_lock)
        {
            var k = _done.FindIndex(d => d.key == (file, look));
            if (k >= 0)
            {
                var img = _done[k].img;
                _done.RemoveAt(k);
                failed = img == null;
                return img;
            }
            if (!_running.Add((file, look))) return null;
        }
        Task.Run(() =>
        {
            PictureImage? result = null;
            try
            {
                if (Original(file, bytes) is { } src)
                {
                    var img = look.Edges != PictureEdges.None ? src.Reduced(EdgeSide) : src;
                    result = img.With(PictureFilter.Apply(img.Rgba, img.Width, img.Height, look));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { App.Log(ex, "Process picture"); }
            lock (_lock)
            {
                _running.Remove((file, look));
                _done.Add(((file, look), result));
                if (_done.Count > 4) _done.RemoveAt(0);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(ready);
        });
        return null;
    }
}
