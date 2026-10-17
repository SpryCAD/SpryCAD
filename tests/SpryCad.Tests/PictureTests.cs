using SpryCad.Core.Sketching;

namespace SpryCad.Tests;

/// <summary>Sketch pictures' processing (PictureFilter): background removal and edge detection on made-up images.</summary>
static class PictureTests
{
    // a w×h image of the background color with a filled square of another from (x0, y0), size s
    static byte[] Square(int w, int h, (byte, byte, byte) bg, (byte, byte, byte) fg, int x0, int y0, int s)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = x >= x0 && x < x0 + s && y >= y0 && y < y0 + s ? fg : bg;
                int i = (y * w + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
            }
        return px;
    }

    static byte A(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    public static void Run(TestRunner t)
    {
        t.Test("Sketch picture: the background color found at the border and removed, the rest kept", () =>
        {
            var px = Square(60, 40, (250, 248, 245), (20, 20, 120), 20, 10, 15);
            var bg = PictureFilter.BorderColor(px, 60, 40);
            t.Eq(PictureFilter.Hex(bg), "#FAF8F5");
            t.True(PictureFilter.TryParseColor("#FAF8F5", out var back) && back == bg, "color round trip");
            var o = PictureFilter.Apply(px, 60, 40, new PictureLook("#FAF8F5", 0.15, false, PictureEdges.None, 0.5));
            t.Eq((A(o, 60, 0, 0), A(o, 60, 59, 39), A(o, 60, 27, 17)), ((byte)0, (byte)0, (byte)255));
            t.True(A(px, 60, 0, 0) == 255, "the original changed");
        });

        t.Test("Sketch picture: connected removal keeps background color enclosed by the drawing", () =>
        {
            // a dark ring (a square with a white hole) on white: the hole is white but not reached from the border
            var px = Square(50, 50, (255, 255, 255), (0, 0, 0), 10, 10, 30);
            for (int y = 20; y < 30; y++) for (int x = 20; x < 30; x++) { int i = (y * 50 + x) * 4; px[i] = px[i + 1] = px[i + 2] = 255; }
            var all = PictureFilter.Apply(px, 50, 50, new PictureLook("#FFFFFF", 0.1, false, PictureEdges.None, 0.5));
            var outer = PictureFilter.Apply(px, 50, 50, new PictureLook("#FFFFFF", 0.1, true, PictureEdges.None, 0.5));
            t.Eq((A(all, 50, 2, 2), A(all, 50, 25, 25)), ((byte)0, (byte)0));
            t.Eq((A(outer, 50, 2, 2), A(outer, 50, 25, 25), A(outer, 50, 15, 15)), ((byte)0, (byte)255, (byte)255));
        });

        t.Test("Sketch picture: edges found along a square's sides, a pixel wide, none in flat areas", () =>
        {
            const int w = 80, h = 80;
            var px = Square(w, h, (240, 240, 240), (40, 40, 40), 20, 20, 40);
            // grain in the paper: no edges from it
            var rnd = new Random(1);
            for (int i = 0; i < w * h; i++) { var d = rnd.Next(-6, 7); for (int c = 0; c < 3; c++) px[i * 4 + c] = (byte)Math.Clamp(px[i * 4 + c] + d, 0, 255); }
            var e = PictureFilter.Edges(px, w, h, 0.5);
            int Near(int x0, int x1, int y0, int y1) { int n = 0; for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (e[y * w + x]) n++; return n; }
            // each side found along its length (rows 25 to 55 of the left side: one or two pixels each, near x = 20)
            for (int y = 25; y < 55; y++) t.True(Near(17, 23, y, y + 1) is >= 1 and <= 2, $"left side at row {y}: {Near(17, 23, y, y + 1)}");
            for (int x = 25; x < 55; x++) t.True(Near(x, x + 1, 57, 63) is >= 1 and <= 2, $"bottom side at column {x}");
            t.Eq(Near(0, w, 0, 12) + Near(30, 50, 30, 50) + Near(68, w, 0, h), 0);
            // drawn over the picture, and alone (nothing else shown)
            var over = PictureFilter.Apply(px, w, h, new PictureLook(null, 0.15, false, PictureEdges.Over, 0.5));
            var only = PictureFilter.Apply(px, w, h, new PictureLook(null, 0.15, false, PictureEdges.Only, 0.5));
            int ex = Enumerable.Range(17, 6).First(x => e[40 * w + x]);
            t.Eq((A(over, w, 5, 5), A(only, w, 5, 5), A(only, w, ex, 40)), ((byte)255, (byte)0, (byte)255));
        });

        t.Test("Sketch picture: a flat image has no edges, and more sensitivity finds fainter ones", () =>
        {
            var flat = Square(40, 40, (128, 128, 128), (128, 128, 128), 0, 0, 0);
            t.True(!PictureFilter.Edges(flat, 40, 40, 1).Any(b => b), "edges in a flat image");
            // a strong square and a faint one
            var px = Square(100, 60, (255, 255, 255), (0, 0, 0), 10, 10, 30);
            for (int y = 15; y < 45; y++) for (int x = 60; x < 90; x++) { int i = (y * 100 + x) * 4; px[i] = px[i + 1] = px[i + 2] = 205; }
            int Faint(bool[] e) { int n = 0; for (int y = 0; y < 60; y++) for (int x = 55; x < 100; x++) if (e[y * 100 + x]) n++; return n; }
            int Strong(bool[] e) { int n = 0; for (int y = 0; y < 60; y++) for (int x = 0; x < 50; x++) if (e[y * 100 + x]) n++; return n; }
            var lo = PictureFilter.Edges(px, 100, 60, 0); var hi = PictureFilter.Edges(px, 100, 60, 1);
            t.True(Strong(lo) > 100 && Strong(hi) > 100, $"strong square: {Strong(lo)}, {Strong(hi)}");
            t.True(Faint(hi) > 100 && Faint(lo) < Faint(hi), $"faint square: {Faint(lo)} at 0, {Faint(hi)} at 1");
        });
    }
}
