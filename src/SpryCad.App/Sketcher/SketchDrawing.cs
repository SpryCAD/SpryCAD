using SpryCad.App.Viewport;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Viewport;

/// <summary>Tessellates sketch entities for display and picking.</summary>
public static class SketchDrawing
{
    public static IEnumerable<Vec2> ArcPoints(Vec2 c, double r, double a0, double a1, int minSeg = 8)
    {
        while (a1 <= a0) a1 += 2 * Math.PI;
        var n = Math.Max(minSeg, (int)Math.Ceiling((a1 - a0) / (2 * Math.PI) * 72));
        for (int i = 0; i <= n; i++) yield return c + Vec2.FromAngle(a0 + (a1 - a0) * i / n, r);
    }

    /// <summary>2D polyline segments of an entity.</summary>
    public static IEnumerable<(Vec2 a, Vec2 b)> Segments2D(Sketch sk, SkEntity e)
    {
        switch (e)
        {
            case SkLine l:
                yield return (sk.P(l.P1), sk.P(l.P2));
                break;
            case SkCircle c:
                {
                    Vec2? prev = null;
                    foreach (var p in ArcPoints(sk.P(c.Center), c.R, 0, 2 * Math.PI, 48)) { if (prev != null) yield return (prev.Value, p); prev = p; }
                    break;
                }
            case SkArc a:
                {
                    var c = sk.P(a.Center); var s = sk.P(a.Start); var en = sk.P(a.End);
                    var r = (s - c).Length;
                    Vec2? prev = null;
                    foreach (var p in ArcPoints(c, r, (s - c).Angle, (en - c).Angle)) { if (prev != null) yield return (prev.Value, p); prev = p; }
                    break;
                }
        }
    }

    public static IEnumerable<(Vec3 a, Vec3 b)> Segments(SketchResult r)
    {
        foreach (var e in r.Sketch.Entities)
            foreach (var (a, b) in Segments2D(r.Sketch, e))
                yield return (r.Frame.ToWorld(a), r.Frame.ToWorld(b));
    }

    /// <summary>Draws a non-edited sketch (single color, construction dashed).</summary>
    public static void DrawStatic(DynamicGeometry g, SketchResult r, Rgba color, double pixelSize)
    {
        var f = r.Frame;
        foreach (var e in r.Sketch.Entities)
        {
            if (e is SkPointEntity pe) { PointMark(g, f, r.Sketch.P(pe.P), color, pixelSize, 3); continue; }
            foreach (var (a, b) in Segments2D(r.Sketch, e))
            {
                if (e.Construction) g.Dashed(f.ToWorld(a), f.ToWorld(b), color, pixelSize * 6, top: true);
                else g.Line(f.ToWorld(a), f.ToWorld(b), color, top: true);
            }
        }
    }

    public static void PointMark(DynamicGeometry g, Frame f, Vec2 p, Rgba c, double px, double sizePx)
    {
        var s = px * sizePx;
        g.Quad(f.ToWorld(p.X - s, p.Y - s), f.ToWorld(p.X + s, p.Y - s), f.ToWorld(p.X + s, p.Y + s), f.ToWorld(p.X - s, p.Y + s), c, top: true);
    }
}
