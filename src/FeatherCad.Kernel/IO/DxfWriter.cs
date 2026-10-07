using System.Globalization;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

/// <summary>
/// Writes a flat pattern as an ASCII DXF (R12 entities, readable by every CAM and laser package): the outline as
/// exact lines, arcs and circles on layer OUTLINE, the bend centerlines on BEND_UP / BEND_DOWN.
/// </summary>
public static class DxfWriter
{
    public static void Write(Stream stream, FlatPattern flat, double scale = 1.0)
    {
        using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
        void G(int code, string v) { w.WriteLine(code.ToString(CultureInfo.InvariantCulture)); w.WriteLine(v); }
        void N(int code, double v) => G(code, v.ToString("0.##########", CultureInfo.InvariantCulture));
        void P(int code, Vec2 p) { N(code, p.X * scale); N(code + 10, p.Y * scale); N(code + 20, 0); }

        G(0, "SECTION"); G(2, "ENTITIES");
        foreach (var region in flat.Regions)
            foreach (var loop in region.AllLoops())
            {
                var segs = NBuild.Segments(loop);
                // a full circle comes back as two half arcs of one circle
                if (segs.Count == 2 && segs.All(s => s.Arc != null) && segs[0].Arc == segs[1].Arc)
                {
                    G(0, "CIRCLE"); G(8, "OUTLINE"); P(10, segs[0].Arc!.Center); N(40, segs[0].Arc!.Radius * scale);
                    continue;
                }
                foreach (var s in segs)
                {
                    if (s.Arc == null) { G(0, "LINE"); G(8, "OUTLINE"); P(10, s.A); P(11, s.B); continue; }
                    // DXF arcs run counterclockwise from the start angle to the end angle
                    var (a, b) = s.Ccw ? (s.A, s.B) : (s.B, s.A);
                    var c = s.Arc.Center;
                    G(0, "ARC"); G(8, "OUTLINE"); P(10, c); N(40, s.Arc.Radius * scale);
                    N(50, Deg((a - c).Angle)); N(51, Deg((b - c).Angle));
                }
            }
        foreach (var bend in flat.Bends)
        {
            G(0, "LINE"); G(8, bend.Up ? "BEND_UP" : "BEND_DOWN"); G(62, bend.Up ? "3" : "1"); P(10, bend.A); P(11, bend.B);
        }
        G(0, "ENDSEC"); G(0, "EOF");
    }

    static double Deg(double rad) { var d = rad * 180 / Math.PI; return d < 0 ? d + 360 : d; }
}
