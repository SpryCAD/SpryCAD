using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary>
/// <c>-- fileicons repoRoot</c>: draws the file type icons, a page with a symbol (a part: one block; an assembly:
/// two blocks put together) and, where there is room, the app's red feather badge. Writes the Windows icons
/// (src/FeatherCad.App/Assets/FeatherCADPart.ico, FeatherCADAssembly.ico, every size a PNG) and the Linux ones
/// (installer/linux/application-x-feathercad-part.png, -assembly.png).
/// </summary>
static class FileIcons
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    public static int Run(string root)
    {
        foreach (var (kind, name) in new[] { ("part", "Part"), ("assembly", "Assembly") })
        {
            var pngs = Sizes.Select(s => (s, Png.Encode(s, s, Draw(kind, s)))).ToList();
            var ico = Path.Combine(root, "src", "FeatherCad.App", "Assets", $"FeatherCAD{name}.ico");
            File.WriteAllBytes(ico, Ico(pngs));
            var png = Path.Combine(root, "installer", "linux", $"application-x-feathercad-{kind}.png");
            File.WriteAllBytes(png, pngs.Last().Item2);
            Console.WriteLine($"{ico}\n{png}");
        }
        return 0;
    }

    /// <summary>An ICO holding PNG images (Windows Vista and later read them at every size).</summary>
    static byte[] Ico(List<(int size, byte[] png)> images)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (s, png) in images)
        {
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        return ms.ToArray();
    }

    // a shape: what it covers (in units of the icon's width, y down) and its color
    record Shape(Func<double, double, bool> Covers, uint Rgb);

    static byte[] Draw(string kind, int size)
    {
        var shapes = new List<Shape>();
        // lines at least a pixel wide
        double line = Math.Max(1.0, size / 28.0) / size;
        // the page, its corner folded
        var page = new[] { (0.16, 0.03), (0.64, 0.03), (0.86, 0.25), (0.86, 0.97), (0.16, 0.97) };
        shapes.Add(new(Stroke(page, line), 0x6B7686));
        shapes.Add(new(Inset(page, line / 2), 0xFBFCFE));
        var fold = new[] { (0.64, 0.03), (0.64, 0.25), (0.86, 0.25) };
        shapes.Add(new(Stroke(fold, line), 0x6B7686));
        shapes.Add(new(Inset(fold, line / 2), 0xD9DFE8));
        if (kind == "part") Block(shapes, 0.51, 0.63, 0.29, line, (0x5FA3E0, 0x2F75B8, 0x1F5590));
        else
        {
            Block(shapes, 0.62, 0.50, 0.19, line, (0xF2B35C, 0xD9822B, 0xAD621B));
            Block(shapes, 0.42, 0.73, 0.21, line, (0x5FA3E0, 0x2F75B8, 0x1F5590));
        }
        // the app's badge where it can be made out
        if (size >= 32)
        {
            double bx = 0.06, by = 0.06, bs = 0.36, rr = 0.08;
            shapes.Add(new((x, y) => RoundRect(x, y, bx, by, bs, rr), 0xC0262D));
            // a feather: a leaf along the diagonal, its quill a line through it
            double cx = bx + bs * 0.55, cy = by + bs * 0.45, len = bs * 0.36, wid = bs * 0.15;
            shapes.Add(new((x, y) =>
            {
                double u = ((x - cx) - (y - cy)) / Math.Sqrt(2), v = ((x - cx) + (y - cy)) / Math.Sqrt(2);
                return u * u / (len * len) + v * v / (wid * wid) <= 1;
            }, 0xFFFFFF));
            var q0 = (bx + bs * 0.18, by + bs * 0.82); var q1 = (cx + len * 0.5, cy - len * 0.5);
            shapes.Add(new((x, y) => SegDist(x, y, q0, q1) <= bs * 0.035, 0xFFFFFF));
            shapes.Add(new((x, y) => SegDist(x, y, (cx - len * 0.45, cy + len * 0.45), q1) <= bs * 0.018, 0xA01C22));
        }
        return Raster(shapes, size);
    }

    /// <summary>An isometric block: three shaded faces, outlined.</summary>
    static void Block(List<Shape> shapes, double cx, double cy, double r, double line, (uint top, uint left, uint right) c)
    {
        double h = r * 0.866;
        (double, double) T = (cx, cy - r), UR = (cx + h, cy - r / 2), LR = (cx + h, cy + r / 2), B = (cx, cy + r),
            LL = (cx - h, cy + r / 2), UL = (cx - h, cy - r / 2), C = (cx, cy);
        var hex = new[] { T, UR, LR, B, LL, UL };
        shapes.Add(new(Inset(new[] { T, UR, C, UL }, 0), c.top));
        shapes.Add(new(Inset(new[] { UL, C, B, LL }, 0), c.left));
        shapes.Add(new(Inset(new[] { C, UR, LR, B }, 0), c.right));
        // edges between the faces, thinner
        var inner = line * 0.6;
        shapes.Add(new((x, y) => SegDist(x, y, C, UR) <= inner / 2 || SegDist(x, y, C, UL) <= inner / 2 || SegDist(x, y, C, B) <= inner / 2, 0x1A2433));
        shapes.Add(new((x, y) => Edges(hex).Any(e => SegDist(x, y, e.a, e.b) <= line / 2), 0x1A2433));
    }

    static Func<double, double, bool> Inset((double, double)[] poly, double d) =>
        (x, y) => Inside(poly, x, y) && (d <= 0 || Edges(poly).All(e => SegDist(x, y, e.a, e.b) >= d));

    // the polygon filled and its outline (half outside, half inside)
    static Func<double, double, bool> Stroke((double, double)[] poly, double w) =>
        (x, y) => Inside(poly, x, y) || Edges(poly).Any(e => SegDist(x, y, e.a, e.b) <= w / 2);

    static IEnumerable<((double, double) a, (double, double) b)> Edges((double, double)[] p) =>
        p.Select((a, i) => (a, p[(i + 1) % p.Length]));

    static bool Inside((double x, double y)[] p, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            if ((p[i].y > y) != (p[j].y > y) && x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x) c = !c;
        return c;
    }

    static double SegDist(double x, double y, (double x, double y) a, (double x, double y) b)
    {
        double dx = b.x - a.x, dy = b.y - a.y;
        var t = Math.Clamp(((x - a.x) * dx + (y - a.y) * dy) / (dx * dx + dy * dy), 0, 1);
        double px = a.x + t * dx - x, py = a.y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    static bool RoundRect(double x, double y, double x0, double y0, double s, double r)
    {
        double qx = Math.Max(Math.Max(x0 + r - x, x - (x0 + s - r)), 0), qy = Math.Max(Math.Max(y0 + r - y, y - (y0 + s - r)), 0);
        return x >= x0 && x <= x0 + s && y >= y0 && y <= y0 + s && qx * qx + qy * qy <= r * r;
    }

    /// <summary>RGBA pixels: each pixel 8×8 samples, the last shape covering a sample giving its color.</summary>
    static byte[] Raster(List<Shape> shapes, int size)
    {
        const int ss = 8;
        var px = new byte[size * size * 4];
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                double r = 0, g = 0, b = 0; int n = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double u = (x + (sx + 0.5) / ss) / size, v = (y + (sy + 0.5) / ss) / size;
                        for (int k = shapes.Count - 1; k >= 0; k--)
                            if (shapes[k].Covers(u, v))
                            {
                                var c = shapes[k].Rgb;
                                r += c >> 16; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
                                break;
                            }
                    }
                int o = 4 * (y * size + x);
                if (n > 0) { px[o] = (byte)(r / n); px[o + 1] = (byte)(g / n); px[o + 2] = (byte)(b / n); }
                px[o + 3] = (byte)(255 * n / (ss * ss));
            }
        });
        return px;
    }
}
