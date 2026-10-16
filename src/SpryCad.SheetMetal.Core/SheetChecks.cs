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
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
                if (Overlap(pieces[i].Outline, pieces[j].Outline))
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
                var inside = pieces.FirstOrDefault(x => x.Id != b.Id.Value && Inside(x.Outline, probe));
                if (inside != null)
                    results.Add(new RuleResult("SM-REL-01", b.Id.Value, false,
                        $"{b.Id} ends inside material ({inside.Id}): it needs a bend relief there."));
            }
        }
        return results;
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
