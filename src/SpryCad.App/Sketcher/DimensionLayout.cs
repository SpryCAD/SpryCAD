using Avalonia;
using Avalonia.Controls;
using SpryCad.App.Viewport;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Sketcher;

/// <summary>
/// Dimension label layout. Labels the user placed (clicked when making the dimension, or dragged; saved in the
/// file) stay where they are. The others are laid out in screen space, so the spacing holds at any zoom: each
/// tries spots in order of preference (linear dimensions off the side of the sketch away from its middle, stacked
/// outward like on a drawing, shorter ones nearer; radii just outside their circle; angles on the bisector) and
/// takes the first that overlaps no other label and no sketch geometry, else the first clear of the labels.
/// </summary>
public sealed partial class SketchEditor
{
    // spacing, in screen pixels
    const double FirstOffset = 24, Step = 22, LabelGap = 3, RowGap = 16;

    /// <summary>Where the layout put the labels the user did not place (sketch coordinates), by dimension id.</summary>
    readonly Dictionary<long, Vec2> _autoLabels = new();

    /// <summary>Where a dimension's label is: where the user put it, else where the layout put it.</summary>
    Vec2 LabelPos(SkConstraint c)
        => !c.HasPlacedLabel && _autoLabels.TryGetValue(c.Id, out var p) ? p : DimAnchor(c) + new Vec2(c.LabelX, c.LabelY);

    /// <summary>
    /// Auto Arrange: the selected dimensions (none selected: all of the sketch's) go back to the automatic label
    /// layout, forgetting where the user put them. Returns how many changed.
    /// </summary>
    public int AutoArrangeDimensions()
    {
        var selected = Selected.Where(r => r.Kind == SkRefKind.Constraint).Select(r => Sk.Constraint(r.Id))
            .OfType<SkConstraint>().Where(c => c.IsDimension).ToList();
        var placed = (selected.Count > 0 ? selected : Sk.Constraints.Where(c => c.IsDimension)).Where(c => c.HasPlacedLabel).ToList();
        if (placed.Count == 0) return 0;
        Snapshot();
        foreach (var c in placed) c.LabelPlaced = false;
        UpdateOverlay(_vp);
        _vp.Redraw();
        return placed.Count;
    }

    /// <summary>
    /// Where the dimensions would be seen from a camera (world space): their labels, laid out for that camera's
    /// zoom (the layout spaces rows in pixels), and the points they measure. Null without dimensions. The view's
    /// own layout is left as it is.
    /// </summary>
    public Box3? DimensionsExtent(Camera cam)
    {
        var dims = Sk.Constraints.Where(c => c.IsDimension).ToList();
        if (dims.Count == 0) return null;
        var probe = MakeLabel();
        Size SizeOf(SkConstraint c) => LabelSize(probe, DimText(c));
        var keep = new Dictionary<long, Vec2>(_autoLabels);
        _layoutCamera = cam;
        try
        {
            LayoutLabels(dims, SizeOf);
            var px = cam.PixelSize;
            var box = Box3.Empty;
            foreach (var c in dims)
            {
                var at = LabelPos(c); var s = SizeOf(c);
                var h = new Vec2(s.Width / 2 * px, s.Height / 2 * px);
                box.Add(Frame.ToWorld(at - h)); box.Add(Frame.ToWorld(at + h));
                box.Add(Frame.ToWorld(new Vec2(at.X - h.X, at.Y + h.Y))); box.Add(Frame.ToWorld(new Vec2(at.X + h.X, at.Y - h.Y)));
                if (c.Type is CType.Distance or CType.HDistance or CType.VDistance or CType.Offset)
                    try { var (a, b) = DimPoints(c); box.Add(Frame.ToWorld(a)); box.Add(Frame.ToWorld(b)); } catch { /* deleted */ }
            }
            return box.IsEmpty ? null : box;
        }
        finally
        {
            _layoutCamera = null;
            _autoLabels.Clear();
            foreach (var (k, v) in keep) _autoLabels[k] = v;
        }
    }

    /// <summary>The size a label takes on screen with this text.</summary>
    static Size LabelSize(Border b, string text)
    {
        ((TextBlock)b.Child!).Text = text;
        b.Measure(Size.Infinity);
        return b.DesiredSize;
    }

    void LayoutLabels(List<SkConstraint> dims, Func<SkConstraint, Size> sizeOf)
    {
        _autoLabels.Clear();
        if (dims.All(c => c.HasPlacedLabel)) return;
        var px = Px;
        Rect RectAt(Vec2 at, Size s) { var p = Screen(at); return new Rect(p.X - s.Width / 2, p.Y - s.Height / 2, s.Width, s.Height); }

        // what labels must not cover: the placed labels, then each laid out one in turn; and the sketch geometry
        var taken = dims.Where(c => c.HasPlacedLabel).Select(c => RectAt(LabelPos(c), sizeOf(c)).Inflate(LabelGap)).ToList();
        var lines = new List<(Point a, Point b)>();
        foreach (var e in Sk.Entities)
            foreach (var (a, b) in SketchDrawing.Segments2D(Sk, e)) lines.Add((Screen(a), Screen(b)));
        var middle = SketchMiddle();
        var auto = dims.Where(c => !c.HasPlacedLabel).OrderBy(LayoutRank).ToList(); // (a stable sort: ties in the order made)

        // 1) horizontal and vertical linear dimensions, as on a drawing: in rows around the outside of the sketch
        //    (and the model face behind it), each on the side nearest what it measures. Shortest first, each in the
        //    innermost row where it overlaps nothing along the edge: a chain of dimensions shares a row (lined up),
        //    overlapping ones stack outward. Rows are spaced by the labels' size across them.
        var rest = new List<SkConstraint>();
        var region = OutsideRegion(dims, out var overPart);
        var plans = new List<(SkConstraint c, int side, double lo, double hi, Size size, Vec2 a, Vec2 b, bool horizontal)>();
        foreach (var c in auto)
        {
            if (region == null || !AxisDim(c, out var horizontal, out var a, out var b)) { rest.Add(c); continue; }
            var (x0, y0, x1, y1) = region.Value;
            // the side nearest what it measures; but not over the part (where it runs on past the rows) if the
            // other side is clear of it
            int Choose(int s0, double d0, int s1, double d1) => overPart[s0] != overPart[s1] ? (overPart[s0] ? s1 : s0) : d0 <= d1 ? s0 : s1;
            int side; double lo, hi;
            if (horizontal)
            {
                (lo, hi) = (Math.Min(a.X, b.X), Math.Max(a.X, b.X));
                side = Choose(0, Math.Min(a.Y, b.Y) - y0, 1, y1 - Math.Max(a.Y, b.Y));   // below / above
            }
            else
            {
                (lo, hi) = (Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
                side = Choose(2, Math.Min(a.X, b.X) - x0, 3, x1 - Math.Max(a.X, b.X));   // left / right
            }
            plans.Add((c, side, lo, hi, sizeOf(c), a, b, horizontal));
        }
        var across = plans.GroupBy(p => p.side).ToDictionary(g => g.Key, g => g.Max(p => p.side < 2 ? p.size.Height : p.size.Width));
        var rows = new Dictionary<(int side, int row), List<(double lo, double hi)>>();
        var drawn = new List<(Point a, Point b)>(); // dimension and extension lines laid out so far (screen)
        foreach (var (c, side, lo, hi, size, a, b, horizontal) in plans)
        {
            var (x0, y0, x1, y1) = region!.Value;
            var mid = (lo + hi) / 2;
            var tol = 1e-6 * Math.Max(1, hi - lo);
            // the innermost row whose lines cross none laid out so far; failing that, the innermost with room
            (List<(double, double)> used, Vec2 at, Rect rect, (Point, Point)[] lines)? pick = null, fallback = null;
            for (int row = 0; row < 12 && pick == null; row++)
            {
                var dist = (RowGap + across[side] / 2 + row * (across[side] + RowGap)) * px;
                var at = side switch
                {
                    0 => new Vec2(mid, y0 - dist), 1 => new Vec2(mid, y1 + dist),
                    2 => new Vec2(x0 - dist, mid), _ => new Vec2(x1 + dist, mid),
                };
                if (!rows.TryGetValue((side, row), out var used)) rows[(side, row)] = used = new();
                if (used.Any(u => lo < u.hi - tol && u.lo < hi - tol)) continue; // overlaps along the edge (touching is a chain)
                var r = RectAt(at, size);
                if (taken.Any(t => t.Intersects(r))) continue;
                // the dimension as it is drawn: its line along the row, and an extension line from each measured point
                var segs = horizontal
                    ? new[] { (new Vec2(lo, at.Y), new Vec2(hi, at.Y)), (a, new Vec2(a.X, at.Y)), (b, new Vec2(b.X, at.Y)) }
                    : new[] { (new Vec2(at.X, lo), new Vec2(at.X, hi)), (a, new Vec2(at.X, a.Y)), (b, new Vec2(at.X, b.Y)) };
                var lines2 = segs.Select(s => (Screen(s.Item1), Screen(s.Item2))).ToArray();
                fallback ??= (used, at, r, lines2);
                if (lines2.Any(s => drawn.Any(d => Crosses(s, d)))) continue;
                pick = (used, at, r, lines2);
            }
            if ((pick ?? fallback) is { } p)
            {
                p.used.Add((lo, hi));
                _autoLabels[c.Id] = p.at;
                taken.Add(p.rect.Inflate(LabelGap));
                drawn.AddRange(p.lines);
            }
            else rest.Add(c);
        }

        // 2) the others (sloped, radius, angle; or no room in the rows): spots near their geometry
        foreach (var c in rest)
        {
            var size = sizeOf(c);
            Vec2? clearOfLabels = null, first = null, chosen = null;
            foreach (var at in Candidates(c, middle, px))
            {
                first ??= at;
                var r = RectAt(at, size);
                if (taken.Any(t => t.Intersects(r))) continue;
                clearOfLabels ??= at;
                if (lines.Any(l => SegmentHitsRect(l.a, l.b, r))) continue;
                chosen = at;
                break;
            }
            var pos = chosen ?? clearOfLabels ?? first ?? DimAnchor(c) + new Vec2(c.LabelX, c.LabelY);
            _autoLabels[c.Id] = pos;
            taken.Add(RectAt(pos, size).Inflate(LabelGap));
        }
    }

    /// <summary>Layout order: linear dimensions first, shorter ones nearer the geometry; then radii, then angles.</summary>
    (int, double) LayoutRank(SkConstraint c)
    {
        try
        {
            return c.Type switch
            {
                CType.Distance or CType.HDistance or CType.VDistance or CType.Offset => (0, (DimPoints(c).b - DimPoints(c).a).Length),
                CType.Radius or CType.Diameter => (1, Sk.Radius(Sk.Entity(c.Refs[0])!)),
                _ => (2, 0),
            };
        }
        catch { return (3, 0); } // refers to something deleted mid-operation
    }

    /// <summary>
    /// A horizontal or vertical linear dimension: which, and its two measured points. False for sloped ones.
    /// </summary>
    bool AxisDim(SkConstraint c, out bool horizontal, out Vec2 a, out Vec2 b)
    {
        horizontal = false; a = b = default;
        if (c.Type is not (CType.Distance or CType.HDistance or CType.VDistance)) return false;
        try { (a, b) = DimPoints(c); } catch { return false; }
        if (c.Type == CType.HDistance) { horizontal = true; return true; }
        if (c.Type == CType.VDistance) return true;
        var d = b - a; var tol = 1e-6 * Math.Max(1, d.Length);
        if (d.Length < 1e-12) return false;
        if (Math.Abs(d.Y) < tol) { horizontal = true; return true; }
        return Math.Abs(d.X) < tol;
    }

    /// <summary>
    /// What the dimension rows go around (sketch coordinates): the sketch's geometry and every point a dimension
    /// measures to (e.g. the origin), extended on each side to the model's outline in the sketch plane where that
    /// edge is close by (rows then run outside the part there; far edges of a big part are left out, so a small
    /// sketch keeps its dimensions near it). Null without geometry.
    /// </summary>
    (double x0, double y0, double x1, double y1)? OutsideRegion(List<SkConstraint> dims, out bool[] overPart)
    {
        // per side (below, above, left, right): the part runs on past the region there, so rows would lie over it
        overPart = new bool[4];
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(Vec2 p) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        foreach (var e in Sk.Entities)
        {
            foreach (var (p, q) in SketchDrawing.Segments2D(Sk, e)) { Add(p); Add(q); }
            foreach (var id in e.PointIds) if (Sk.Point(id) is { } pt) Add(pt.P);
        }
        foreach (var c in dims)
            if (c.Type is CType.Distance or CType.HDistance or CType.VDistance or CType.Offset)
                try { var (p, q) = DimPoints(c); Add(p); Add(q); } catch { /* deleted mid-operation */ }
        if (x0 > x1) return null;
        var sketchSize = Math.Max(x1 - x0, y1 - y0);
        double mx0 = double.MaxValue, my0 = double.MaxValue, mx1 = double.MinValue, my1 = double.MinValue;
        foreach (var body in _vp.Bodies)
        {
            var bb = body.Mesh.Bounds;
            if (bb.IsEmpty) continue;
            for (int i = 0; i < 8; i++)
            {
                var p = Frame.ToLocal(new Vec3((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z));
                mx0 = Math.Min(mx0, p.X); my0 = Math.Min(my0, p.Y); mx1 = Math.Max(mx1, p.X); my1 = Math.Max(my1, p.Y);
            }
        }
        if (mx0 <= mx1)
        {
            // a model edge counts where it is close: within half the sketch's size (and at least 60 pixels)
            var near = Math.Max(0.5 * sketchSize, 60 * Px);
            if (mx0 < x0 && x0 - mx0 <= near) x0 = mx0;
            if (my0 < y0 && y0 - my0 <= near) y0 = my0;
            if (mx1 > x1 && mx1 - x1 <= near) x1 = mx1;
            if (my1 > y1 && my1 - y1 <= near) y1 = my1;
            var eps = 1e-6 * Math.Max(1, sketchSize);
            overPart = new[] { my0 < y0 - eps, my1 > y1 + eps, mx0 < x0 - eps, mx1 > x1 + eps };
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>Whether two segments cross (touching at an end, or running along each other, does not count).</summary>
    static bool Crosses((Point a, Point b) s, (Point a, Point b) t)
    {
        static double O(Point p, Point q, Point r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        const double e = 0.5; // screen pixels
        double d1 = O(t.a, t.b, s.a), d2 = O(t.a, t.b, s.b), d3 = O(s.a, s.b, t.a), d4 = O(s.a, s.b, t.b);
        return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
    }

    /// <summary>The middle of the sketch's geometry: labels go to the side away from it (outside the profile).</summary>
    Vec2 SketchMiddle()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var e in Sk.Entities)
            foreach (var id in e.PointIds)
                if (Sk.Point(id) is { } p) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return x0 > x1 ? Vec2.Zero : new Vec2((x0 + x1) / 2, (y0 + y1) / 2);
    }

    /// <summary>Spots for a dimension's label, most preferred first (sketch coordinates).</summary>
    IEnumerable<Vec2> Candidates(SkConstraint c, Vec2 middle, double px)
    {
        var list = new List<Vec2>();
        try
        {
            switch (c.Type)
            {
                case CType.Distance:
                case CType.HDistance:
                case CType.VDistance:
                case CType.Offset:
                    {
                        var (a, b) = DimPoints(c);
                        // the dimension line runs along dir (as DrawDimension draws it), offset along nrm
                        Vec2 dir = c.Type == CType.HDistance ? new Vec2(1, 0) : c.Type == CType.VDistance ? new Vec2(0, 1)
                            : (b - a).LengthSq > 1e-18 ? (b - a).Normalized : new Vec2(1, 0);
                        var nrm = dir.Perp;
                        var mid = (a + b) * 0.5;
                        var outward = Vec2.Dot(mid - middle, nrm) >= 0 ? 1 : -1;
                        var len = Math.Abs(Vec2.Dot(b - a, dir));
                        // stacked outward on the outside, then the inside; then slid along the line, for crowded spots
                        foreach (var side in new[] { outward, -outward })
                            for (int k = 0; k < 6; k++)
                                list.Add(mid + nrm * (side * (FirstOffset + k * Step) * px));
                        foreach (var t in new[] { 0.3, -0.3 })
                            for (int k = 0; k < 4; k++)
                                list.Add(mid + dir * (t * len) + nrm * (outward * (FirstOffset + k * Step) * px));
                        break;
                    }
                case CType.Radius:
                case CType.Diameter:
                    {
                        var e = Sk.Entity(c.Refs[0])!;
                        var center = DimAnchor(c);
                        var r = Sk.Radius(e);
                        // start from the outward direction (an arc: its middle)
                        Vec2 u0;
                        if (e is SkArc arc)
                        {
                            var s = (Sk.P(arc.Start) - center).Angle; var en = (Sk.P(arc.End) - center).Angle;
                            var sweep = en - s; while (sweep <= 0) sweep += 2 * Math.PI;
                            u0 = Vec2.FromAngle(s + sweep / 2, 1);
                        }
                        else u0 = (center - middle).LengthSq > 1e-12 ? (center - middle).Normalized : Vec2.FromAngle(Math.PI / 4, 1);
                        for (int k = 0; k < 4; k++)
                            foreach (var j in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8 })
                                list.Add(center + Rotate(u0, j * Math.PI / 8) * (r + (FirstOffset * 0.75 + k * Step) * px));
                        break;
                    }
                case CType.Angle:
                    {
                        var vertex = DimAnchor(c);
                        var (r1, r2) = SketchSolver.AngleRays(Sk, c);
                        var bis = r1.Normalized + r2.Normalized;
                        bis = bis.LengthSq > 1e-12 ? bis.Normalized : r1.Normalized.Perp;
                        var half = Math.Acos(Math.Clamp(Vec2.Dot(r1.Normalized, r2.Normalized), -1, 1)) / 2;
                        // on the bisector, then nearer each ray (still inside the measured angle)
                        for (int k = 0; k < 6; k++)
                            foreach (var f in new[] { 0.0, 0.5, -0.5 })
                                list.Add(vertex + Rotate(bis, f * half) * ((FirstOffset * 1.8 + k * Step) * px));
                        break;
                    }
            }
        }
        catch { /* refers to something deleted mid-operation */ }
        return list;
    }

    static Vec2 Rotate(Vec2 v, double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new Vec2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>Whether a segment touches a rectangle (Liang-Barsky clipping).</summary>
    static bool SegmentHitsRect(Point a, Point b, Rect r)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            var t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        return Clip(-dx, a.X - r.Left) && Clip(dx, r.Right - a.X) && Clip(-dy, a.Y - r.Top) && Clip(dy, r.Bottom - a.Y);
    }
}
