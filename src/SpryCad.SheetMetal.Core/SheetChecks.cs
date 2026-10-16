namespace SpryCad.SheetMetal;

/// <summary>
/// Manufacturing checks on the bend graph (sheet metal spec section 9), analytic and on every rebuild. The corner
/// checks (SM-CORN-01, SM-FOLD-01 at corners) come with the corners (<see cref="SheetCorners"/>); these are the flat's.
/// </summary>
public static class SheetChecks
{
    const double Tol = 1e-6;

    /// <summary>SM-FLAT-01 and SM-REL-01: the flat does not overlap itself; no bend zone ends inside material.</summary>
    public static List<RuleResult> Flat(BendGraph g)
    {
        var results = new List<RuleResult>();
        var pieces = FlatPattern2D.Pieces(g);
        // (arcs as short straight pieces: these checks need only straight edges)
        var shapes = pieces.Select(p => p.Outline.Straightened()).ToList();
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
                if (Overlap(shapes[i], shapes[j]))
                    results.Add(new RuleResult("SM-FLAT-01", $"{pieces[i].Id}/{pieces[j].Id}", false,
                        $"The flat pattern overlaps itself: {pieces[i].Id} and {pieces[j].Id} lie on the same material when unfolded. Fix: shorten or trim one of them."));
        foreach (var b in g.Bends)
        {
            // just past each end of the bend zone, halfway across it: material there means the bend tears into it
            var s = FlatPattern2D.Strip(g, b).Outer;
            Vec2 q = s[0], p = s[1];
            var e = (q - p).Normalized();
            var across = b.FlatAxis.Normal * (b.Allowance / 2);
            foreach (var (end, dir) in new[] { (p, -e), (q, e) })
            {
                var probe = end + across + dir * 1e-4;
                var inside = pieces.FirstOrDefault(x => x.Id != b.Id.Value && Inside(x.Outline.Straightened(), probe));
                if (inside != null)
                    results.Add(new RuleResult("SM-REL-01", b.Id.Value, false,
                        $"{b.Id} ends inside material ({inside.Id}): it needs a bend relief there."));
            }
        }
        return results;
    }

    static string Mm(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm";

    /// <summary>
    /// SM-DIST-01 and SM-DIST-02: each hole in a panel at least <paramref name="toBend"/> (by the bend's inside radius)
    /// from the bend zones on the panel's edges, and at least <paramref name="toEdge"/> from its free edges.
    /// </summary>
    public static List<RuleResult> Distances(BendGraph g, Func<Bend, double> toBend, double toEdge)
    {
        var results = new List<RuleResult>();
        foreach (var p in g.Panels)
        {
            var poly = p.FlatOutline;
            var bendEdges = g.Bends.Where(b => b.A == p.Id).Select(b => (b, edge: b.EdgeA))
                .Concat(g.Bends.Where(b => b.B == p.Id).Select(b => (b, edge: b.EdgeB))).ToList();
            for (int h = 1; h < poly.LoopCount; h++)
            {
                var pts = poly.Segs(h).SelectMany(s => s.Points(Math.PI / 90)).ToList();
                var id = $"{p.Id}.Hole{h - 1}";
                for (int e = 0; e < poly.Outer.Count; e++)
                {
                    var seg = poly.Seg(0, e);
                    var d = pts.Min(x => Distance(seg, x));
                    var bend = bendEdges.FirstOrDefault(x => x.edge == e).b;
                    if (bend != null)
                    {
                        // from the bend zone: the edge is the zone's tangent line
                        var need = toBend(bend);
                        if (d < need - Tol)
                            results.Add(new RuleResult("SM-DIST-01", id, false, $"A hole in {p.Id} is {Mm(d)} from the bend {bend.Id}; the shop's rule is at least {Mm(need)}. Fix: move the hole or add a relief cut."));
                    }
                    else if (d < toEdge - Tol)
                        results.Add(new RuleResult("SM-DIST-02", id, false, $"A hole in {p.Id} is {Mm(d)} from the panel's edge; the shop's rule is at least {Mm(toEdge)}."));
                }
            }
        }
        return results;
    }

    /// <summary>SM-FLG-01: each flange at least the tooling's minimum long (to its outside mold line, from the bend).</summary>
    public static List<RuleResult> MinFlange(BendGraph g, Func<Bend, double> min)
    {
        var results = new List<RuleResult>();
        foreach (var b in g.Bends.Where(b => b.EdgeB == 0 && !b.IsHem))
        {
            var bo = g.Panel(b.B).FlatOutline.Outer;
            var near = bo[0];
            var depth = bo.Max(v => Vec2.Dot(v - near, b.FlatAxis.Normal));
            var length = depth + BendMath.Ossb(b.Angle, b.InsideRadius, g.Thickness);
            var need = min(b);
            if (length < need - Tol)
                results.Add(new RuleResult("SM-FLG-01", b.Id.Value, false, $"The flange at {b.Id} is {Mm(length)} long; its tooling needs at least {Mm(need)} to bend it."));
        }
        return results;
    }

    /// <summary>SM-GRAIN-01: bend lines at least <paramref name="minDeg"/> across the grain (<paramref name="grainDeg"/> in the flat).</summary>
    public static List<RuleResult> Grain(BendGraph g, double grainDeg, double minDeg)
    {
        var results = new List<RuleResult>();
        foreach (var b in g.Bends)
        {
            var bendDeg = Math.Atan2(b.FlatAxis.Dir.Y, b.FlatAxis.Dir.X) * 180 / Math.PI;
            var across = Math.Abs(((bendDeg - grainDeg) % 180 + 180) % 180);
            across = Math.Min(across, 180 - across);
            if (across < minDeg - 1e-9)
                results.Add(new RuleResult("SM-GRAIN-01", b.Id.Value, false, $"{b.Id} runs {across:0.#}° from the grain; this material should be bent at least {minDeg:0.#}° across it."));
        }
        return results;
    }

    /// <summary>
    /// SM-HEM-01: a hem's return clear of the panel it is folded back over (a teardrop's return angles toward it). The
    /// return's surface facing the panel must stay on its side of the panel's face. Null when clear; else the message,
    /// and the longest return (flat, from the bend) that would be.
    /// </summary>
    public static (string message, double maxFlat)? HemClearance(BendGraph folded, Bend hem)
    {
        var a = folded.Panel(hem.A);
        var b = folded.Panel(hem.B);
        bool up = hem.Direction == BendDirection.Up;
        double t = folded.Thickness;
        // the panel's face the hem folds over, as a plane in the folded part; the return's face toward it
        var origin = a.FoldedFrame.Apply(new Vec3(0, 0, up ? 0 : -t));
        var normal = a.FoldedFrame.ApplyDir(new Vec3(0, 0, up ? 1 : -1));
        double Clear(Vec2 p) => Vec3.Dot(b.FoldedFrame.Apply(new Vec3(p.X, p.Y, up ? 0 : -t)) - origin, normal);
        var o = b.FlatOutline.Outer;
        double near = Math.Min(Clear(o[0]), Clear(o[1])), far = Math.Min(Clear(o[2]), Clear(o[3]));
        if (far >= -Tol) return null;
        var depth = Vec2.Dot(o[2] - o[1], hem.FlatAxis.Normal);
        var maxFlat = near <= 0 ? 0 : depth * near / (near - far);
        return ($"The hem {hem.Id} runs into the panel it folds over ({Mm(-far)} too far). Fix: shorten it to at most {Mm(maxFlat)} past the bend, or open its angle.", maxFlat);
    }

    static double Distance(Seg2 s, Vec2 x)
    {
        if (s.IsArc)
        {
            // to the nearest point along the arc (in pieces: near enough for a rule check)
            var pts = s.Points(Math.PI / 180).ToList();
            return Enumerable.Range(0, pts.Count - 1).Min(i => Distance(new Seg2(pts[i], pts[i + 1]), x));
        }
        var d = s.B - s.A;
        var l2 = Vec2.Dot(d, d);
        var t = l2 == 0 ? 0 : Math.Clamp(Vec2.Dot(x - s.A, d) / l2, 0, 1);
        return (s.A + d * t - x).Length;
    }

    /// <summary>Whether two regions share area (touching along edges or at points is not overlapping).</summary>
    public static bool Overlap(Polygon2 a, Polygon2 b)
    {
        foreach (var (p, q) in Segments(a))
            foreach (var (r, s) in Segments(b))
                if (Cross(p, q, r, s)) return true;
        // one inside the other, or the same: a point just inside an edge of one, inside the other
        return Probes(a).Any(x => Inside(b, x) && !OnBoundary(b, x)) || Probes(b).Any(x => Inside(a, x) && !OnBoundary(a, x));
    }

    static IEnumerable<(Vec2, Vec2)> Segments(Polygon2 poly)
    {
        foreach (var l in new[] { poly.Outer }.Concat(poly.Holes))
            for (int i = 0; i < l.Count; i++) yield return (l[i], l[(i + 1) % l.Count]);
    }

    /// <summary>Points just inside the region beside each of its edges' middles (outer loops counterclockwise, holes clockwise).</summary>
    static IEnumerable<Vec2> Probes(Polygon2 poly) => Segments(poly).Select(s => (s.Item1 + s.Item2) * 0.5 + (s.Item2 - s.Item1).Normalized().Left * 1e-4);

    /// <summary>Whether segments p–q and r–s cross at a point inside both (not at an end, not along each other).</summary>
    static bool Cross(Vec2 p, Vec2 q, Vec2 r, Vec2 s)
    {
        double d1 = Vec2.Cross(q - p, r - p), d2 = Vec2.Cross(q - p, s - p), d3 = Vec2.Cross(s - r, p - r), d4 = Vec2.Cross(s - r, q - r);
        double lp = (q - p).Length, lr = (s - r).Length;
        return d1 * d2 < 0 && d3 * d4 < 0 && Math.Abs(d1) > Tol * lp && Math.Abs(d2) > Tol * lp && Math.Abs(d3) > Tol * lr && Math.Abs(d4) > Tol * lr;
    }

    /// <summary>Even-odd: inside the outer loop and in none of the holes.</summary>
    public static bool Inside(Polygon2 poly, Vec2 x)
    {
        bool inside = false;
        foreach (var (a, b) in Segments(poly))
            if ((a.Y > x.Y) != (b.Y > x.Y) && x.X < a.X + (x.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)) inside = !inside;
        return inside;
    }

    static bool OnBoundary(Polygon2 poly, Vec2 x) => Segments(poly).Any(s =>
    {
        var d = s.Item2 - s.Item1;
        var len = d.Length;
        if (len == 0) return (x - s.Item1).Length < Tol;
        var t = Vec2.Dot(x - s.Item1, d) / (len * len);
        return t >= -Tol && t <= 1 + Tol && Math.Abs(Vec2.Cross(d, x - s.Item1)) / len < Tol;
    });
}
