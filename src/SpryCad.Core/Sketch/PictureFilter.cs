using System.Globalization;

namespace SpryCad.Core.Sketching;

/// <summary>
/// A sketch picture's image as it is shown (<see cref="PictureLook"/>): its background made transparent (the pixels
/// near a color, or only those reached from the border), its edges found (Canny: smoothed, gradient, thinned to a pixel,
/// strong edges followed through weaker ones) and drawn over it or alone. Images are straight-alpha RGBA rows.
/// </summary>
public static class PictureFilter
{
    /// <summary>Edges drawn over the picture, and alone (on nothing).</summary>
    static readonly byte[] OverColor = { 230, 70, 20 }, OnlyColor = { 25, 35, 60 };
    /// <summary>Colors this much past the tolerance fade from removed to kept (a soft edge, not a jagged one).</summary>
    const double Feather = 0.04;

    public static byte[] Apply(byte[] rgba, int w, int h, PictureLook look)
    {
        var o = (byte[])rgba.Clone();
        if (look.Background != null && TryParseColor(look.Background, out var key))
            RemoveBackground(o, w, h, key, look.Tolerance, look.Connected);
        if (look.Edges != PictureEdges.None)
        {
            var edges = Edges(rgba, w, h, look.Sensitivity);
            // a large image is shown smaller than it is: its one-pixel edges drawn two wide, so they do not vanish
            if (Math.Max(w, h) > 1200) edges = Dilate(edges, w, h);
            var c = look.Edges == PictureEdges.Over ? OverColor : OnlyColor;
            if (look.Edges == PictureEdges.Only) Array.Clear(o);
            for (int i = 0; i < edges.Length; i++)
                if (edges[i]) { o[i * 4] = c[0]; o[i * 4 + 1] = c[1]; o[i * 4 + 2] = c[2]; o[i * 4 + 3] = 255; }
        }
        return o;
    }

    // ---- background

    /// <summary>
    /// Makes the pixels within <paramref name="tolerance"/> of <paramref name="key"/> transparent (fading out just past
    /// it); with <paramref name="connected"/>, only those joined to the image's border through such pixels.
    /// </summary>
    public static void RemoveBackground(byte[] rgba, int w, int h, (byte r, byte g, byte b) key, double tolerance, bool connected)
    {
        int n = w * h;
        var keep = new float[n];
        for (int i = 0; i < n; i++)
        {
            var d = Distance(rgba, i, key);
            keep[i] = d <= tolerance ? 0 : d >= tolerance + Feather ? 1 : (float)((d - tolerance) / Feather);
        }
        bool[]? reached = null;
        if (connected)
        {
            reached = new bool[n];
            var queue = new Queue<int>();
            void Seed(int i) { if (!reached[i] && keep[i] < 1) { reached[i] = true; queue.Enqueue(i); } }
            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                int x = i % w, y = i / w;
                if (x > 0) Seed(i - 1);
                if (x < w - 1) Seed(i + 1);
                if (y > 0) Seed(i - w);
                if (y < h - 1) Seed(i + w);
            }
        }
        for (int i = 0; i < n; i++)
            if (keep[i] < 1 && (reached == null || reached[i])) rgba[i * 4 + 3] = (byte)Math.Round(rgba[i * 4 + 3] * keep[i]);
    }

    /// <summary>A color's distance from another, 0 (the same) to 1 (black from white).</summary>
    static double Distance(byte[] rgba, int i, (byte r, byte g, byte b) k)
    {
        double dr = rgba[i * 4] - k.r, dg = rgba[i * 4 + 1] - k.g, db = rgba[i * 4 + 2] - k.b;
        return Math.Sqrt(dr * dr + dg * dg + db * db) / (255 * Math.Sqrt(3));
    }

    /// <summary>The color most of the image's border is (a scan's paper, a photo's backdrop): what to remove first.</summary>
    public static (byte r, byte g, byte b) BorderColor(byte[] rgba, int w, int h)
    {
        // border pixels counted by color, to 32 levels a channel; the most common averaged
        var counts = new Dictionary<int, (int n, long r, long g, long b)>();
        void Add(int i)
        {
            int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            var k = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
            var c = counts.GetValueOrDefault(k);
            counts[k] = (c.n + 1, c.r + r, c.g + g, c.b + b);
        }
        for (int x = 0; x < w; x++) { Add(x); Add((h - 1) * w + x); }
        for (int y = 1; y < h - 1; y++) { Add(y * w); Add(y * w + w - 1); }
        var best = counts.Values.MaxBy(c => c.n);
        return ((byte)(best.r / best.n), (byte)(best.g / best.n), (byte)(best.b / best.n));
    }

    public static string Hex((byte r, byte g, byte b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

    public static bool TryParseColor(string? s, out (byte r, byte g, byte b) c)
    {
        c = default;
        if (s is not { Length: 7 } || s[0] != '#' || !int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        c = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    // ---- edges (Canny)

    /// <summary>
    /// The image's edges, a pixel wide: where its brightness changes most steeply, the strongest found and followed
    /// through weaker ones that continue them. <paramref name="sensitivity"/> 0 keeps the strongest ridges only, 1 most.
    /// </summary>
    public static bool[] Edges(byte[] rgba, int w, int h, double sensitivity)
    {
        int n = w * h;
        var edges = new bool[n];
        if (w < 3 || h < 3) return edges;
        var gray = new float[n];
        for (int i = 0; i < n; i++) gray[i] = 0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2];
        gray = Blur(gray, w, h);
        // gradient (Sobel), and its direction to the nearest 45 degrees
        var mag = new float[n];
        var dir = new byte[n];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float gx = gray[i - w + 1] + 2 * gray[i + 1] + gray[i + w + 1] - gray[i - w - 1] - 2 * gray[i - 1] - gray[i + w - 1];
                float gy = gray[i + w - 1] + 2 * gray[i + w] + gray[i + w + 1] - gray[i - w - 1] - 2 * gray[i - w] - gray[i - w + 1];
                mag[i] = MathF.Sqrt(gx * gx + gy * gy);
                var a = MathF.Atan2(gy, gx) * 180 / MathF.PI;
                if (a < 0) a += 180;
                dir[i] = (byte)(a < 22.5f || a >= 157.5f ? 0 : a < 67.5f ? 1 : a < 112.5f ? 2 : 3);
            }
        // thinned: only where the gradient peaks across the edge
        var ridge = new float[n];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                var m = mag[i];
                if (m <= 0) continue;
                int step = dir[i] switch { 0 => 1, 1 => w + 1, 2 => w, _ => w - 1 };
                if (m >= mag[i - step] && m > mag[i + step]) ridge[i] = m;
            }
        // thresholds from the ridges' own strengths: the strongest share of them are edges for sure, those half as strong
        // are edges where they continue one
        var strengths = new List<float>();
        int stride = Math.Max(1, n / 400_000);
        for (int i = 0; i < n; i += stride) if (ridge[i] > 0) strengths.Add(ridge[i]);
        if (strengths.Count == 0) return edges;
        strengths.Sort();
        var share = 0.05 + 0.45 * Math.Clamp(sensitivity, 0, 1);
        // (a smooth image has no edges: never below a clear step in brightness)
        float high = Math.Max(strengths[(int)Math.Min(strengths.Count - 1, (1 - share) * strengths.Count)], 40f), low = high * 0.5f;
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++) if (ridge[i] >= high) { edges[i] = true; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    var j = yy * w + xx;
                    if (!edges[j] && ridge[j] >= low) { edges[j] = true; queue.Enqueue(j); }
                }
        }
        return edges;
    }

    /// <summary>Gaussian smoothing (sigma 1.4), across then down, so noise and paper grain make no edges.</summary>
    static float[] Blur(float[] src, int w, int h)
    {
        const int r = 3; const float sigma = 1.4f;
        var k = new float[2 * r + 1]; float sum = 0;
        for (int i = -r; i <= r; i++) sum += k[i + r] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new float[src.Length]; var dst = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * src[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = s;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                dst[y * w + x] = s;
            }
        return dst;
    }

    static bool[] Dilate(bool[] e, int w, int h)
    {
        var o = new bool[e.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (e[y * w + x])
                    for (int dy = 0; dy <= 1 && y + dy < h; dy++)
                        for (int dx = 0; dx <= 1 && x + dx < w; dx++) o[(y + dy) * w + x + dx] = true;
        return o;
    }
}
