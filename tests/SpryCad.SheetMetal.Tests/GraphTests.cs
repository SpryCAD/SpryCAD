namespace SpryCad.SheetMetal.Tests;

/// <summary>The bend graph (sheet metal spec section 4): folding is exact, the graph is a tree.</summary>
static class GraphTests
{
    const double Deg = Math.PI / 180;

    /// <summary>
    /// A strip of flanges along x, 20 wide (y from 0 to 20), joined by bends parallel to y: flange i is
    /// <paramref name="oml"/>[i] long between the outside mold lines of its bends (its free end at the strip's ends),
    /// bend i between flanges i and i + 1. Laid out flat from the dimensions, as a base flange would.
    /// </summary>
    static BendGraph Strip(SheetRules rules, double[] oml, double[] angles, BendDirection[] dirs, int fixedPanel)
    {
        double t = rules.Thickness, r = rules.InsideRadius;
        var panels = new List<Panel>();
        var bends = new List<Bend>();
        double x = 0;
        for (int i = 0; i < oml.Length; i++)
        {
            double flat = oml[i];
            if (i > 0) flat -= BendMath.Ossb(angles[i - 1], r, t);
            if (i < oml.Length - 1) flat -= BendMath.Ossb(angles[i], r, t);
            if (flat <= 0) throw new ArgumentException($"flange {i} is too short");
            panels.Add(new Panel(new PanelId("P" + i), Polygon2.Rectangle(new Vec2(x, 0), new Vec2(x + flat, 20)), Frame3.Identity));
            x += flat;
            if (i < oml.Length - 1)
            {
                var (ba, source) = rules.Allowance(angles[i]);
                // B (the next flange, +x) on the axis's left: the axis runs along −y
                bends.Add(new Bend(new BendId("B" + i), new PanelId("P" + i), new PanelId("P" + (i + 1)),
                    new Line2(new Vec2(x + ba / 2, 0), new Vec2(0, -1)), angles[i], dirs[i], r, ba, rules.Toolset, source));
                x += ba;
            }
        }
        return new BendGraph(t, panels, bends, new PanelId("P" + fixedPanel));
    }

    /// <summary>
    /// The folded flange lengths measured again: each bend's virtual sharp (where the outside surfaces of the two
    /// flanges meet) placed along the flanges it joins; a flange's length from sharp to sharp, or to its free end.
    /// </summary>
    static double[] Remeasure(BendGraph folded, double[] angles, BendDirection[] dirs)
    {
        double t = folded.Thickness;
        int n = folded.Panels.Count;
        // a panel's outside surface at a bend: the bottom (z = −t) for Up bends, the top for Down
        static (double x, double z) XZ(Vec3 p) => (p.X, p.Z);
        (double x, double z) Sharp(int i)
        {
            double zOut = dirs[i] == BendDirection.Up ? -t : 0;
            Frame3 fa = folded.Panels[i].FoldedFrame, fb = folded.Panels[i + 1].FoldedFrame;
            var (ax, az) = XZ(fa.Apply(new Vec3(0, 0, zOut)));
            var (adx, adz) = XZ(fa.ApplyDir(new Vec3(1, 0, 0)));
            var (bx, bz) = XZ(fb.Apply(new Vec3(0, 0, zOut)));
            var (bdx, bdz) = XZ(fb.ApplyDir(new Vec3(1, 0, 0)));
            // a + s·ad = b + u·bd
            double den = adx * -bdz - adz * -bdx;
            double s = ((bx - ax) * -bdz - (bz - az) * -bdx) / den;
            return (ax + s * adx, az + s * adz);
        }
        double Along(int panel, (double x, double z) p)
        {
            var f = folded.Panels[panel].FoldedFrame;
            var d = f.ApplyDir(new Vec3(1, 0, 0));
            var o = f.Apply(new Vec3(0, 0, 0));
            return (p.x - o.X) * d.X + (p.z - o.Z) * d.Z;
        }
        var lengths = new double[n];
        for (int i = 0; i < n; i++)
        {
            var box = folded.Panels[i].FlatOutline.Outer;
            double lo = box.Min(p => p.X), hi = box.Max(p => p.X);
            double start = i > 0 ? Along(i, Sharp(i - 1)) : lo;
            double end = i < n - 1 ? Along(i, Sharp(i)) : hi;
            lengths[i] = end - start;
        }
        return lengths;
    }

    public static void Run(TestRunner t)
    {
        var rules = SheetRules.Resolve(StarterProfile.Profile, "Steel CRS", "14 ga");

        t.Test("Bend graph: an L bracket folds to its dimensions; the bend's flanges are perpendicular", () =>
        {
            var g = Strip(rules, new[] { 50.0, 30 }, new[] { 90 * Deg }, new[] { BendDirection.Up }, 0).Fold();
            var m = Remeasure(g, new[] { 90 * Deg }, new[] { BendDirection.Up });
            t.Near(m[0], 50, 1e-9);
            t.Near(m[1], 30, 1e-9);
            // the second flange stands up (+z) from the first, the fixed one in place
            t.Eq(g.Panels[0].FoldedFrame, Frame3.Identity);
            var up = g.Panels[1].FoldedFrame.ApplyDir(new Vec3(1, 0, 0));
            t.Near(up.X, 0, 1e-12);
            t.Near(up.Z, 1, 1e-12);
            // its inside surface (the top side) lies R from the bend axis, its outside R + t
            var bend = g.Bends[0];
            var axis = new Vec3(bend.TangentA.Point.X, 0, rules.InsideRadius);
            var b = g.Panels[1].FoldedFrame;
            var tangentB = bend.TangentB.Point;
            t.Near((b.Apply(new Vec3(tangentB.X, 0, 0)) - axis).Length, rules.InsideRadius, 1e-9);
            t.Near((b.Apply(new Vec3(tangentB.X, 0, -rules.Thickness)) - axis).Length, rules.InsideRadius + rules.Thickness, 1e-9);
        });

        t.Test("Bend graph: folding and measuring again gives the flange lengths, 1 to 10 bends, both ways, any fixed panel", () =>
        {
            var rng = new Random(6935);
            for (int bendsCount = 1; bendsCount <= 10; bendsCount++)
                for (int trial = 0; trial < 5; trial++)
                {
                    var angles = Enumerable.Range(0, bendsCount).Select(_ => (10 + rng.NextDouble() * 150) * Deg).ToArray();
                    var dirs = Enumerable.Range(0, bendsCount).Select(_ => rng.Next(2) == 0 ? BendDirection.Up : BendDirection.Down).ToArray();
                    // long enough for any setback: OSSB of 160° at R = t ≈ 2·1.9·5.7 ≈ 22
                    var oml = Enumerable.Range(0, bendsCount + 1).Select(_ => 60 + rng.NextDouble() * 40).ToArray();
                    var g = Strip(rules, oml, angles, dirs, rng.Next(bendsCount + 1)).Fold();
                    var m = Remeasure(g, angles, dirs);
                    for (int i = 0; i < oml.Length; i++) t.Near(m[i], oml[i], 1e-8);
                }
        });

        t.Test("Bend graph: the flat length is the panels' contributions plus the allowances", () =>
        {
            var angles = new[] { 90 * Deg, 45 * Deg };
            var g = Strip(rules, new[] { 40.0, 60, 25 }, angles, new[] { BendDirection.Up, BendDirection.Down }, 1);
            var total = g.Panels.Sum(p => p.FlatOutline.Outer.Max(q => q.X) - p.FlatOutline.Outer.Min(q => q.X)) + g.Bends.Sum(b => b.Allowance);
            double t0 = rules.Thickness, r = rules.InsideRadius;
            var expected = 40 + 60 + 25 - 2 * BendMath.Ossb(angles[0], r, t0) - 2 * BendMath.Ossb(angles[1], r, t0)
                + rules.Allowance(angles[0]).Allowance + rules.Allowance(angles[1]).Allowance;
            t.Near(total, expected, 1e-9);
            t.Near(g.Panels[^1].FlatOutline.Outer.Max(q => q.X), expected, 1e-9);
        });

        t.Test("Bend graph: loops and stray panels are refused with a clear message", () =>
        {
            var g = Strip(rules, new[] { 50.0, 50, 50 }, new[] { 90 * Deg, 90 * Deg }, new[] { BendDirection.Up, BendDirection.Up }, 0);
            var loop = g with { Bends = g.Bends.Append(g.Bends[0] with { Id = new BendId("B9"), A = new PanelId("P2"), B = new PanelId("P0") }).ToList() };
            t.Throws<BendGraphException>(() => loop.Fold(), "closes a loop");
            var stray = g with { Panels = g.Panels.Append(new Panel(new PanelId("P7"), Polygon2.Rectangle(new Vec2(0, 30), new Vec2(5, 40)), Frame3.Identity)).ToList() };
            t.Throws<BendGraphException>(() => stray.Fold(), "No bend joins P7");
            t.Throws<BendGraphException>(() => (g with { FixedPanel = new PanelId("Q") }).Fold(), "not a panel");
            var flatBend = g with { Bends = new[] { g.Bends[0] with { Angle = 0 }, g.Bends[1] } };
            t.Throws<BendGraphException>(() => flatBend.Fold(), "more than 0°");
        });

        t.Test("Bend graph: the same inputs fold to the same numbers", () =>
        {
            var angles = new[] { 30 * Deg, 120 * Deg, 75 * Deg };
            var dirs = new[] { BendDirection.Up, BendDirection.Down, BendDirection.Up };
            var a = Strip(rules, new[] { 60.0, 70, 80, 90 }, angles, dirs, 2).Fold();
            var b = Strip(rules, new[] { 60.0, 70, 80, 90 }, angles, dirs, 2).Fold();
            for (int i = 0; i < a.Panels.Count; i++) t.Eq(a.Panels[i].FoldedFrame, b.Panels[i].FoldedFrame);
        });
    }
}
