using System.Globalization;
using System.Text;

namespace SpryCad.SheetMetal.Export;

/// <summary>How the flat pattern is written, from the shop profile's flat output table (sheet metal spec section 3).</summary>
public sealed record OutputStyle(string OutlineLayer, string BendUpLayer, string BendDownLayer, string EtchLayer, string BendNote)
{
    public static readonly OutputStyle Default = new("OUTLINE", "BEND_UP", "BEND_DOWN", "ETCH", "{direction} {angle} R{radius}");

    /// <summary>The style the profile gives (its sm.output row).</summary>
    public static OutputStyle Of(SheetRules rules)
    {
        string T(string col, string fallback) { var v = rules.RuleText(ProfileTables.Output, col); return v.Length > 0 ? v : fallback; }
        return new OutputStyle(T("outline layer", Default.OutlineLayer), T("bend up layer", Default.BendUpLayer),
            T("bend down layer", Default.BendDownLayer), T("etch layer", Default.EtchLayer), T("bend note", Default.BendNote));
    }
}

/// <summary>
/// Writes a flat pattern (sheet metal spec section 10): DXF (R12 entities, readable by every CAM and laser package)
/// and SVG, in millimetres. Contours closed; bend centre lines on the up and down layers with their notes; the bend
/// table below the part. The same pattern gives the same bytes.
/// </summary>
public static class FlatExport
{
    static string F(double v) => (Math.Abs(v) < 5e-11 ? 0 : v).ToString("0.##########", CultureInfo.InvariantCulture);

    public static void WriteDxf(Stream stream, FlatPattern2D flat, OutputStyle style)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
        void G(int code, string v) { w.WriteLine(code.ToString(CultureInfo.InvariantCulture)); w.WriteLine(v); }
        void N(int code, double v) => G(code, F(v));
        void P(int code, Vec2 p) { N(code, p.X); N(code + 10, p.Y); N(code + 20, 0); }

        // the layers, so CAM sees them even before an entity is on them
        G(0, "SECTION"); G(2, "TABLES");
        G(0, "TABLE"); G(2, "LAYER"); G(70, "4");
        foreach (var (name, color) in new[] { (style.OutlineLayer, 7), (style.BendUpLayer, 3), (style.BendDownLayer, 1), (style.EtchLayer, 5) })
        {
            G(0, "LAYER"); G(2, name); G(70, "0"); G(62, color.ToString(CultureInfo.InvariantCulture)); G(6, "CONTINUOUS");
        }
        G(0, "ENDTAB"); G(0, "ENDSEC");

        G(0, "SECTION"); G(2, "ENTITIES");
        for (int li = 0; li < flat.Loops.Count; li++)
        {
            var loop = flat.Loops[li];
            // a closed polyline: one contour, closed (CAM needs no gaps); arcs as true arcs (a vertex's bulge)
            G(0, "POLYLINE"); G(8, style.OutlineLayer); G(66, "1"); G(70, "1"); P(10, new Vec2(0, 0));
            for (int i = 0; i < loop.Count; i++)
            {
                G(0, "VERTEX"); G(8, style.OutlineLayer); P(10, loop[i]);
                if (flat.LoopBulges[li][i] != 0) N(42, flat.LoopBulges[li][i]);
            }
            G(0, "SEQEND"); G(8, style.OutlineLayer);
        }
        var height = Math.Max(2.5, flat.Thickness * 2);
        foreach (var b in flat.Bends)
        {
            var layer = b.Direction == BendDirection.Up ? style.BendUpLayer : style.BendDownLayer;
            G(0, "LINE"); G(8, layer); P(10, b.A); P(11, b.B);
            var mid = (b.A + b.B) * 0.5;
            var d = (b.B - b.A).Normalized();
            var rot = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
            if (rot > 90) rot -= 180; else if (rot <= -90) rot += 180;
            G(0, "TEXT"); G(8, layer); P(10, mid + d.Left * (height * 0.4)); N(40, height); G(1, b.Note(style.BendNote)); N(50, rot);
        }
        // the bend table under the part
        var (min, _) = flat.Bends.Count > 0 || flat.Loops.Count > 0 ? flat.Bounds : (new Vec2(0, 0), new Vec2(0, 0));
        double y = min.Y - height * 3;
        foreach (var row in new[] { FlatPattern2D.BendTableHeader }.Concat(flat.BendTable()))
        {
            G(0, "TEXT"); G(8, style.EtchLayer); P(10, new Vec2(min.X, y)); N(40, height); G(1, string.Join("   ", row));
            y -= height * 1.6;
        }
        G(0, "ENDSEC"); G(0, "EOF");
    }

    public static void WriteSvg(Stream stream, FlatPattern2D flat, OutputStyle style)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        var (min, max) = flat.Loops.Count > 0 ? flat.Bounds : (new Vec2(0, 0), new Vec2(1, 1));
        double m = Math.Max(5, flat.Thickness * 4);
        double x0 = min.X - m, y0 = min.Y - m, wd = max.X - min.X + 2 * m, ht = max.Y - min.Y + 2 * m;
        // SVG's y runs down: the pattern is mirrored into it so it reads as the DXF does
        string Pt(Vec2 p) => $"{F(p.X)},{F(max.Y + min.Y - p.Y)}";
        w.WriteLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(wd)}mm\" height=\"{F(ht)}mm\" viewBox=\"{F(x0)} {F(y0)} {F(wd)} {F(ht)}\">");
        w.WriteLine($"  <g id=\"{Esc(style.OutlineLayer)}\" fill=\"none\" stroke=\"black\" stroke-width=\"0.2\">");
        var path = new StringBuilder();
        for (int li = 0; li < flat.Loops.Count; li++)
        {
            path.Append('M').Append(Pt(flat.Loops[li][0]));
            foreach (var s in flat.Segs(li))
            {
                if (!s.IsArc) { path.Append(" L").Append(Pt(s.B)); continue; }
                // mirrored into SVG's y-down: a counterclockwise arc is drawn clockwise (sweep flag 0)
                var large = Math.Abs(s.Sweep) > Math.PI ? 1 : 0;
                path.Append($" A{F(s.Radius)},{F(s.Radius)} 0 {large} {(s.Sweep > 0 ? 0 : 1)} ").Append(Pt(s.B));
            }
            path.Append(" Z ");
        }
        w.WriteLine($"    <path fill-rule=\"evenodd\" d=\"{path.ToString().Trim()}\"/>");
        w.WriteLine("  </g>");
        foreach (var dir in new[] { BendDirection.Up, BendDirection.Down })
        {
            var layer = dir == BendDirection.Up ? style.BendUpLayer : style.BendDownLayer;
            var color = dir == BendDirection.Up ? "green" : "red";
            w.WriteLine($"  <g id=\"{Esc(layer)}\" stroke=\"{color}\" stroke-width=\"0.2\" stroke-dasharray=\"2 1\">");
            foreach (var b in flat.Bends.Where(b => b.Direction == dir))
                w.WriteLine($"    <line x1=\"{F(b.A.X)}\" y1=\"{F(max.Y + min.Y - b.A.Y)}\" x2=\"{F(b.B.X)}\" y2=\"{F(max.Y + min.Y - b.B.Y)}\"><title>{Esc(b.Note(style.BendNote))}</title></line>");
            w.WriteLine("  </g>");
        }
        w.WriteLine("</svg>");
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
