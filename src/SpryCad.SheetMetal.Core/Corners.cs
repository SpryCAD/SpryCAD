using System.Globalization;

namespace SpryCad.SheetMetal;

/// <summary>A check's outcome on one entity (sheet metal spec section 9), said in plain words.</summary>
public sealed record RuleResult(string Rule, string Entity, bool Passed, string Message);

/// <summary>How a corner is to be made: its treatment and gap, and whether they are the profile's or overridden.</summary>
public sealed record CornerSpec(CornerTreatment Treatment, double Gap, double RuleGap, IReadOnlyCollection<CornerTreatment> Allowed,
    bool ZeroGapAllowed, bool Overridden);

/// <summary>
/// Corners (sheet metal spec section 7): where flanges on two edges of a panel meet at the edges' common corner. Found
/// from the graph, never from the solid. The flanges are fitted to their panel's edges again on every rebuild (an
/// edge moved by another flange's setback moves them too), then lengthened toward the corner by its treatment:
/// <list type="bullet">
/// <item>Open: both stop <c>Gap</c> short of the other's inside face (the outside corner left open).</item>
/// <item>Overlap A (and Closed): the first flange reaches the other's outside face; the second stops <c>Gap</c> short
/// of its inside face. Overlap B: the other way round.</item>
/// </list>
/// What is left of the corner in the flat is a rectangular corner relief: the bend zones end at the panel's corner and
/// never overlap. For now a corner is between two 90° bends the same way round on a square corner of the panel.
/// </summary>
public static class SheetCorners
{
    /// <summary>A corner's persistent id: its two bends, the first (in the panel's order) first.</summary>
    public static CornerId IdOf(Bend first, Bend second) => new($"{first.Id}+{second.Id}");

    /// <summary>A bend whose panel B is a flange fitted to A's edge (made by an edge flange: B's edge 0 on the bend).</summary>
    static bool IsFlange(BendGraph g, Bend b) => b.EdgeB == 0 && b.EdgeA >= 0;

    /// <summary>The corners of the graph: bends on two edges of one panel that meet, each with the vertex they meet at.</summary>
    public static List<(Bend first, Bend second, Vec2 at)> Find(BendGraph g)
    {
        var list = new List<(Bend, Bend, Vec2)>();
        foreach (var p in g.Panels)
        {
            var o = p.FlatOutline.Outer;
            int m = o.Count;
            var onEdge = g.Bends.Where(b => b.A == p.Id && b.EdgeA >= 0).ToDictionary(b => b.EdgeA);
            foreach (var (i, b1) in onEdge.OrderBy(kv => kv.Key))
                if (onEdge.TryGetValue((i + 1) % m, out var b2)) list.Add((b1, b2, o[(i + 1) % m]));
        }
        return list;
    }

    /// <summary>
    /// The graph with its flanges fitted to their edges and lengthened at their corners by <paramref name="spec"/>, its
    /// <see cref="BendGraph.Corners"/> listed; the corners' checks (gap, allowed treatment, folded collision) added to
    /// <paramref name="results"/>. A corner that cannot be made yet is a <see cref="BendGraphException"/>.
    /// </summary>
    public static BendGraph Apply(BendGraph g, Func<CornerId, Bend, Bend, CornerSpec> spec, List<RuleResult> results)
    {
        double t = g.Thickness;
        // how far each flange reaches past each end of its edge: (bend, at its edge's end q (true) or start p) → length
        var ext = new Dictionary<(BendId, bool), double>();
        var corners = new List<Corner>();
        foreach (var (b1, b2, at) in Find(g))
        {
            var id = IdOf(b1, b2);
            var a = g.Panel(b1.A).FlatOutline.Outer;
            int m = a.Count;
            var e1 = (a[(b1.EdgeA + 1) % m] - a[b1.EdgeA]).Normalized();
            var e2 = (a[(b2.EdgeA + 1) % m] - a[b2.EdgeA]).Normalized();
            if (!IsFlange(g, b1) || !IsFlange(g, b2))
                throw new BendGraphException($"Corner {id}: a flange meets a bend of the base flange's profile there; that corner cannot be made yet.");
            if (b1.Direction != b2.Direction)
            {
                // one up, one down: they part at the corner, nothing to make
                corners.Add(new Corner(id, new[] { b1.Id, b2.Id }, CornerTreatment.Open, double.PositiveInfinity));
                continue;
            }
            if (Vec2.Cross(e1, e2) <= 0 || Math.Abs(Vec2.Dot(e1, e2)) > 1e-9)
                throw new BendGraphException($"Corner {id}: only flanges on edges at a square, outside corner of their panel can meet for now.");
            if (Math.Abs(b1.Angle - Math.PI / 2) > 1e-9 || Math.Abs(b2.Angle - Math.PI / 2) > 1e-9)
                throw new BendGraphException($"Corner {id}: only 90° flanges can meet at a corner for now.");
            var s = spec(id, b1, b2);
            double r1 = b1.InsideRadius, r2 = b2.InsideRadius, gap = s.Gap;
            // first flange's reach past the corner (along e1), the second's (back along e2): see the class's notes
            double x1, x2;
            switch (s.Treatment)
            {
                case CornerTreatment.Open: x1 = r2 - gap; x2 = r1 - gap; break;
                case CornerTreatment.OverlapB: x1 = r2 - gap; x2 = r1 + t; break;
                case CornerTreatment.Miter: throw new BendGraphException($"Corner {id}: a mitred corner comes with miter flanges.");
                default: x1 = r2 + t; x2 = r1 - gap; break; // Closed, Overlap A
            }
            x1 = Math.Max(0, x1); x2 = Math.Max(0, x2);
            ext[(b1.Id, true)] = x1;
            ext[(b2.Id, false)] = x2;
            // the folded gap: between each flange's end and the other's inside face (where it reaches past it, none)
            double g1 = r2 - x1, g2 = r1 - x2;
            var actual = s.Treatment switch
            {
                CornerTreatment.Open => Math.Min(g1, g2),
                CornerTreatment.OverlapB => g1,
                _ => g2,
            };
            corners.Add(new Corner(id, new[] { b1.Id, b2.Id }, s.Treatment, actual));
            Check(results, id, s, x1, x2, r1, r2, actual, t);
        }

        // fit the flanges, parents first (a flange on a flange's far edge follows it)
        var panels = g.Panels.ToDictionary(p => p.Id);
        foreach (var (b, _, _) in g.Walk())
        {
            if (!IsFlange(g, b)) continue;
            var a = panels[b.A].FlatOutline.Outer;
            var bo = panels[b.B].FlatOutline.Outer;
            int m = a.Count;
            Vec2 p = a[b.EdgeA], q = a[(b.EdgeA + 1) % m];
            var e = (q - p).Normalized();
            var n = b.FlatAxis.Normal;
            // the flange's depth past the bend: from its edge on the bend to its far edge
            var depth = bo.Max(v => Vec2.Dot(v - p, n)) - b.Allowance;
            double xq = ext.GetValueOrDefault((b.Id, true)), xp = ext.GetValueOrDefault((b.Id, false));
            Vec2 nb = n * b.Allowance, nf = n * (b.Allowance + depth);
            if (bo.Count != 4) throw new BendGraphException($"{b.B}: a flange cut to another shape cannot be fitted to its edge yet.");
            // its edges as made (0 on the bend, 1 the side at p, 2 the far edge, 3 the side at q), lengthened past p
            // and q: where it reaches past its bend zone the near edge runs on beside it (the flat and the solid split
            // edges where another piece's corner lies on them)
            var outline = new[] { q + e * xq + nb, p - e * xp + nb, p - e * xp + nf, q + e * xq + nf };
            panels[b.B] = panels[b.B] with { FlatOutline = new Polygon2(outline, panels[b.B].FlatOutline.Holes) };
        }
        return g with { Panels = g.Panels.Select(p => panels[p.Id]).ToList(), Corners = corners };
    }

    static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + " mm";

    static void Check(List<RuleResult> results, CornerId id, CornerSpec s, double x1, double x2, double r1, double r2, double actual, double t)
    {
        const double tol = 1e-6;
        var what = $"Corner {id}";
        if (!s.Allowed.Contains(s.Treatment))
            results.Add(new RuleResult("SM-CORN-01", id.Value, false, $"{what}: the shop profile does not allow a {s.Treatment} corner."));
        // the flanges' walls cross where each reaches past the other's inside face
        bool clash = x1 > r2 + tol && x2 > r1 + tol;
        if (clash)
            results.Add(new RuleResult("SM-FOLD-01", id.Value, false,
                $"{what}: the flanges overlap {Mm(Math.Min(x1 - r2, x2 - r1))} in the folded part. Fix: set the corner to Open (gap {Mm(s.RuleGap)})."));
        else if (actual < s.RuleGap - tol)
            results.Add(new RuleResult("SM-CORN-01", id.Value, false,
                $"{what}: the flanges are {Mm(actual)} apart in the folded part; the gap rule requires {Mm(s.RuleGap)}. Fix: set the corner's gap to {Mm(s.RuleGap)}."));
        else if (actual < tol && !s.ZeroGapAllowed)
            results.Add(new RuleResult("SM-CORN-01", id.Value, false, $"{what}: the flanges touch with no gap, which the shop profile does not allow."));
        else
            results.Add(new RuleResult("SM-CORN-01", id.Value, true, $"{what}: {s.Treatment}, gap {Mm(actual)}."));
    }
}
