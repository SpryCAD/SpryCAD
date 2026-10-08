using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Helical sweeps: exact screw surfaces; volumes by Pappus for a screw motion (angle × first moment of the profile about the axis).</summary>
static class HelicalTests
{
    // ∫ r dA of a polygon in the (r, h) half plane: (1/6) Σ (r_i + r_j)(r_i h_j − r_j h_i)
    static double FirstMoment(IReadOnlyList<Vec2> p)
    {
        double m = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            m += (a.X + b.X) * (a.X * b.Y - b.X * a.Y);
        }
        return Math.Abs(m) / 6;
    }

    // the part of a convex polygon with r ≤ R
    static List<Vec2> ClipInside(IReadOnlyList<Vec2> p, double R)
    {
        var res = new List<Vec2>();
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            bool ai = a.X <= R, bi = b.X <= R;
            if (ai) res.Add(a);
            if (ai != bi) res.Add(a + (b - a) * ((R - a.X) / (b.X - a.X)));
        }
        return res;
    }

    static double Volume(TestRunner t, NBody b)
    {
        t.Eq(b.Validate(), null);
        var mp = NMass.Of(b);
        t.True(mp != null, "integrable");
        var tess = NTess.ToSolid(b);
        t.Eq(tess.Topology.OpenHalfEdges, 0);
        return mp!.Volume;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Helical sweep: a coil spring (circle profile, 3 turns) has the Pappus volume; mirrored and left-handed alike", () =>
        {
            // the profile in the XY plane, a circle r 2 at x = 10; the helix about the Y axis, lead 6
            var coil = new ProfileRegion(ProfileLoop.Circle(new Vec2(10, 0), 2, "C"));
            var b = NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, 6, 3 * 2 * Math.PI, "S");
            double v = 3 * 2 * Math.PI * (Math.PI * 4) * 10;
            var got = Volume(t, b);
            t.True(Math.Abs(got - v) < 1e-7 * v, $"volume {got:R} vs {v:R}");
            t.True(b.Faces.Any(f => f.Surface.Kind == SurfaceKind.Screw), "screw faces");
            // left hand (negative lead) and a mirrored copy: the same volume
            var lh = NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, -6, 3 * 2 * Math.PI, "L");
            t.True(Math.Abs(Volume(t, lh) - v) < 1e-7 * v, "left hand");
            var mir = b.Transformed(Transform.Mirror(Plane.FromPointNormal(Vec3.Zero, Vec3.UnitX)));
            t.True(Math.Abs(Volume(t, mir) - v) < 1e-7 * v, "mirrored");
            // a profile taller than the lead is refused
            bool refused = false;
            try { NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, 3, 2 * 2 * Math.PI, "X"); } catch (ArgumentException) { refused = true; }
            t.True(refused, "overlapping turns refused");
        });

        t.Test("Helical sweep: a 60° thread cut into a cylinder removes angle × the first moment of the part inside", () =>
        {
            // cylinder r 10 along Y from y = 0 to 30
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var cyl = NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }, frame, 0, 30, "Shaft");
            // a 60° V, pitch 3: apex at r 8.4, flanks reaching r 10.9 (past the surface), centred at y = 6
            double pitch = 3, apexR = 8.4, outR = 10.9, h = (outR - apexR) * Math.Tan(Math.PI / 6);
            var tri = new[] { new Vec2(apexR, 6), new Vec2(outR, 6 - h), new Vec2(outR, 6 + h) };
            var tool = NHelical.Sweep(new[] { new ProfileRegion(ProfileLoop.Polygon(tri, "T")) }, Frame.Front, Vec3.Zero, Vec3.UnitY, pitch, 4 * 2 * Math.PI, "Th");
            t.True(Math.Abs(Volume(t, tool) - 4 * 2 * Math.PI * FirstMoment(tri)) < 1e-7 * Volume(t, tool), "tool volume");
            var cut = NBoolean.Run(cyl, tool, BoolOp.Subtract);
            var removed = 4 * 2 * Math.PI * FirstMoment(ClipInside(tri, 10));
            var expect = Math.PI * 100 * 30 - removed;
            var got = Volume(t, cut);
            t.True(Math.Abs(got - expect) < 1e-6 * expect, $"threaded volume {got:R} vs {expect:R} (removed {removed:R})");
            // the groove meets the cylinder along helices
            t.True(cut.Edges.Any(e => e.Curve is NHelix), "helical edges");
        });

        t.Test("Screw surface across a coaxial plane and cone: one curve of exact points (no tracing)", () =>
        {
            // a thread flank (a line in a plane through the Y axis) swept 4 turns, lead 3
            var sc = new ScrewSurface(ScrewMotion.About(Vec3.Zero, Vec3.UnitY, 3, Vec3.UnitX), new NLine(new Vec3(8.4, 6, 0), new Vec3(2.5, -1.44, 0)), 0, 2.9, 0, 8 * Math.PI);
            var screw = NSurface.ScrewOf(sc);
            var plane = NSurface.FromPlane(Plane.FromPointNormal(new Vec3(0, 9, 0), Vec3.UnitY));
            var cone = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cone, Origin = new Vec3(0, 2, 0), Axis = Vec3.UnitY, RefDir = Vec3.UnitX, Radius = 8, Param2 = Math.PI / 4 });
            foreach (var (o, name) in new[] { (plane, "plane"), (cone, "cone") })
            {
                var cs = NIntersect.Analytic(screw, o, 1e-6, 1e-3);
                t.True(cs != null && cs.Count == 1 && cs[0] is NPolyline, $"{name}: one curve");
                var pl = (NPolyline)cs![0];
                double worst = 0;
                foreach (var p in pl.Pts) worst = Math.Max(worst, Math.Max(Math.Abs(o.F_(p)), sc.Closest(p).dist));
                t.True(worst < 1e-9, $"{name}: points on both surfaces ({worst:E2})");
                // the chord tolerance holds between the points
                double sag = 0;
                for (int i = 0; i + 1 < pl.Pts.Length; i++)
                {
                    var m = (pl.Pts[i] + pl.Pts[i + 1]) * 0.5;
                    sag = Math.Max(sag, sc.Closest(m).dist);
                }
                t.True(sag < 2e-3, $"{name}: chords within the tolerance ({sag:E2})");
            }
            // a plane along the axis, out of reach: nothing
            var far = NSurface.FromPlane(Plane.FromPointNormal(new Vec3(20, 0, 0), Vec3.UnitX));
            t.Eq(NIntersect.Analytic(screw, far, 1e-6)!.Count, 0);
        });

        t.Test("Screw surface crossed by a line along its axis, a circle about it and a line through it: closed form, as a brute-force scan finds", () =>
        {
            var sc = new ScrewSurface(ScrewMotion.About(Vec3.Zero, Vec3.UnitY, 3, Vec3.UnitX), new NLine(new Vec3(8.4, 6, 0), new Vec3(2.5, -1.44, 0)), 0, 2.9, 0, 8 * Math.PI);
            var screw = NSurface.ScrewOf(sc);
            NEdge LineEdge(Vec3 a, Vec3 b) => new(new NLine(a, b - a), 0, (b - a).Length, new NVertex(a), new NVertex(b));
            var at30 = new Vec3(Math.Cos(Math.PI / 6), 0, Math.Sin(Math.PI / 6)) * 9.5;
            var across = new Vec3(Math.Cos(0.05), 0, -Math.Sin(0.05)) * 12; // square to the axis, turned a little about it
            var edges = new (string name, NEdge e)[]
            {
                ("along the axis", LineEdge(at30 + new Vec3(0, -5, 0), at30 + new Vec3(0, 30, 0))),
                ("through the axis", LineEdge(new Vec3(0, 10.3, 0) - across, new Vec3(0, 10.3, 0) + across)),
                ("about the axis", new NEdge(new NCircle(new Vec3(0, 10, 0), Vec3.UnitY, Vec3.UnitX, 9.5), 0, 2 * Math.PI, new NVertex(new Vec3(9.5, 10, 0)), new NVertex(new Vec3(9.5, 10, 0)))),
            };
            foreach (var (name, e) in edges)
            {
                var hits = NIntersect.EdgeSurface(e, screw, 1e-7).Params;
                // brute force: sign changes of the signed distance on a fine scan, kept where the root is on the surface
                // (the generator's own stretch: the signed distance also counts its line extended past the profile)
                var brute = new List<double>();
                double lo = e.T0, hi = e.T1; int n = 20000;
                double F(double t) => screw.F_(e.Curve.Eval(t));
                double pt = lo, pf = F(lo);
                for (int i = 1; i <= n; i++)
                {
                    var tt = lo + (hi - lo) * i / n; var f = F(tt);
                    if (pf * f < 0)
                    {
                        var r = Roots.Bisect(F, pt, tt);
                        var cv = sc.Closest(e.Curve.Eval(r)).v;
                        if (Math.Abs(F(r)) < 1e-6 && cv > sc.V0 - 0.03 && cv < sc.V1 + 0.03) brute.Add(r);
                    }
                    pt = tt; pf = f;
                }
                t.True(hits.Count > 0, $"{name}: crossings found");
                t.True(hits.Count == brute.Count, $"{name}: {hits.Count} crossings [{string.Join(", ", hits.Select(h => h.ToString("0.####")))}] vs brute force {brute.Count} [{string.Join(", ", brute.Select(h => h.ToString("0.####")))}]");
                for (int i = 0; i < Math.Min(hits.Count, brute.Count); i++)
                {
                    t.True(Math.Abs(hits[i] - brute[i]) < 1e-6, $"{name}: crossing {i} at {hits[i]:0.######} vs {brute[i]:0.######}");
                    t.True(sc.Closest(e.Curve.Eval(hits[i])).dist < 1e-9, $"{name}: crossing {i} on the surface");
                }
            }
        });

        t.Test("Helical sweep run-out: a round wire runs on along the end's normal; a V leaves a radius along the helix's tangent", () =>
        {
            // a circle profile: a right cylinder 5 long on the end (exact: Pappus + π r² · 5)
            var coil = new ProfileRegion(ProfileLoop.Circle(new Vec2(10, 0), 2, "C"));
            var b = NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, 6, 3 * 2 * Math.PI, "S", exitLength: 5);
            double v = 3 * 2 * Math.PI * (Math.PI * 4) * 10 + Math.PI * 4 * 5;
            var got = Volume(t, b);
            t.True(Math.Abs(got - v) < 1e-7 * v, $"volume {got:R} vs {v:R}");
            t.True(b.Faces.Any(f => f.Tag.Name.StartsWith("S.X.") && f.Surface.Kind == SurfaceKind.Cylinder), "a cylinder on the end");
            // a V thread in a 10 mm cylinder running out past r 11: the end cap clear of it, the cut valid
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var cyl = NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }, frame, 0, 30, "Shaft");
            double apexR = 8.4, outR = 10.9, h = (outR - apexR) * Math.Tan(Math.PI / 6);
            var tri = new[] { new Vec2(apexR, 6), new Vec2(outR, 6 - h), new Vec2(outR, 6 + h) };
            // (both hands advance up the shaft from y = 6)
            foreach (var lead in new[] { 3.0, -3.0 })
            {
                var tri2 = tri;
                var tool = NHelical.Sweep(new[] { new ProfileRegion(ProfileLoop.Polygon(tri2, "T")) }, Frame.Front, Vec3.Zero, Vec3.UnitY, lead, 4 * 2 * Math.PI, "Th", exitRadius: 11);
                var tv = Volume(t, tool);
                t.True(tv > 4 * 2 * Math.PI * FirstMoment(tri), "the run-out adds to the sweep");
                var end = tool.Faces.Single(f => f.Tag.Name == "Th.End");
                t.True(end.Coedges.All(co => new Vec2(co.Edge.V0.P.X, co.Edge.V0.P.Z).Length > 11 - 1e-9), "the end cap lies outside r 11");
                t.True(tool.Faces.Count(f => f.Tag.Name.StartsWith("Th.X.")) == 3, "three run-out planes");
                var cut = NBoolean.Run(cyl, tool, BoolOp.Subtract);
                Volume(t, cut);
            }
        });

        t.Test("Helical sweep: a V groove crossing the shaft's end face (out the top, out the bottom, in from below) cuts exactly", () =>
        {
            // cylinder r 10 along Y from y = 0 to 30; its seam at angle 0 (x = +10), where the sweep starts
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var cyl = NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }, frame, 0, 30, "Shaft");
            double apexR = 8.4, outR = 10.9, h = (outR - apexR) * Math.Tan(Math.PI / 6), turns = 4;
            foreach (var (lead, y0) in new[] { (3.0, 24.0), (-3.0, 6.0), (3.0, -6.0) })
            {
                var tri = new[] { new Vec2(apexR, y0), new Vec2(outR, y0 - h), new Vec2(outR, y0 + h) };
                // the left-hand one about the axis pointing down: it advances down (out the bottom), turns·lead in y
                var tool = NHelical.Sweep(new[] { new ProfileRegion(ProfileLoop.Polygon(tri, "T")) }, Frame.Front, Vec3.Zero, lead < 0 ? -Vec3.UnitY : Vec3.UnitY, lead, turns * 2 * Math.PI, "Th");
                var cut = NBoolean.Run(cyl, tool, BoolOp.Subtract);
                // a profile point at height v sweeps through v .. v + turns·lead: it lies in the shaft over (2π/|lead|)·|that ∩ [0, 30]| of angle
                var removed = SweptInside(ClipInside(tri, 10), lead, turns, 0, 30);
                var expect = Math.PI * 100 * 30 - removed;
                var got = Volume(t, cut);
                t.True(Math.Abs(got - expect) < 1e-6 * expect, $"lead {lead}, y0 {y0}: volume {got:R} vs {expect:R} (removed {removed:R})");
            }
        });
    }

    // ∫ r · (2π/|lead|)·|[v, v + turns·lead] ∩ [lo, hi]| dA over a convex polygon in the (r, v) half plane: the overlap is linear in v
    // between its kinks, so cut the polygon into bands there and integrate ∫ r dA and ∫ r v dA exactly on each
    static double SweptInside(IReadOnlyList<Vec2> p, double lead, double turns, double lo, double hi)
    {
        double dv = turns * lead;
        double Overlap(double v) => Math.Max(0, Math.Min(Math.Max(v, v + dv), hi) - Math.Max(Math.Min(v, v + dv), lo));
        var cuts = new[] { lo, hi, lo - dv, hi - dv, double.NegativeInfinity, double.PositiveInfinity }.Distinct().OrderBy(c => c).ToArray();
        double total = 0;
        for (int k = 0; k + 1 < cuts.Length; k++)
        {
            var band = ClipBand(p, cuts[k], cuts[k + 1]);
            if (band.Count < 3) continue;
            double m1 = 0, m2 = 0;   // ∫ r dA, ∫ r v dA
            for (int i = 0; i < band.Count; i++)
            {
                var a = band[i]; var b = band[(i + 1) % band.Count];
                double c = a.X * b.Y - b.X * a.Y;
                m1 += (a.X + b.X) * c / 6;
                m2 += (a.X * (2 * a.Y + b.Y) + b.X * (a.Y + 2 * b.Y)) * c / 24;
            }
            if (m1 < 0) { m1 = -m1; m2 = -m2; }
            // the overlap's line on this band from two sample heights
            double v0 = band.Min(q => q.Y), v1 = band.Max(q => q.Y);
            double o0 = Overlap(v0), o1 = Overlap(v1), slope = v1 > v0 ? (o1 - o0) / (v1 - v0) : 0;
            total += (o0 - slope * v0) * m1 + slope * m2;
        }
        return 2 * Math.PI / Math.Abs(lead) * total;
    }

    // the part of a convex polygon with lo ≤ v ≤ hi
    static List<Vec2> ClipBand(IReadOnlyList<Vec2> p, double lo, double hi)
    {
        List<Vec2> Half(IReadOnlyList<Vec2> q, Func<Vec2, double> f)
        {
            var res = new List<Vec2>();
            for (int i = 0; i < q.Count; i++)
            {
                var a = q[i]; var b = q[(i + 1) % q.Count];
                double fa = f(a), fb = f(b);
                if (fa >= 0) res.Add(a);
                if ((fa >= 0) != (fb >= 0)) res.Add(a + (b - a) * (fa / (fa - fb)));
            }
            return res;
        }
        var r = double.IsNegativeInfinity(lo) ? p.ToList() : Half(p, q => q.Y - lo);
        return double.IsPositiveInfinity(hi) ? r : Half(r, q => hi - q.Y);
    }
}

/// <summary>The Helical Sweep and Thread features in documents.</summary>
static class ThreadFeatureTests
{
    static SpryCad.Core.Sketching.Sketch CircleAt(Vec2 c, double r)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var cp = Vec2.Distance(c, Vec2.Zero) < 1e-12 ? sk.Point(SpryCad.Core.Sketching.Sketch.OriginId)! : sk.AddPoint(c);
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = cp.Id, R = r });
        return sk;
    }

    // ∫ r dA of a polygon in the (r, z) half plane
    static double FirstMoment(IReadOnlyList<Vec2> p)
    {
        double m = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; m += (a.X + b.X) * (a.X * b.Y - b.X * a.Y); }
        return Math.Abs(m) / 6;
    }

    // the part of a convex polygon on one side of r = R
    static List<Vec2> Clip(IReadOnlyList<Vec2> p, double R, bool inside)
    {
        var res = new List<Vec2>();
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            bool ai = inside ? a.X <= R : a.X >= R, bi = inside ? b.X <= R : b.X >= R;
            if (ai) res.Add(a);
            if (ai != bi) res.Add(a + (b - a) * ((R - a.X) / (b.X - a.X)));
        }
        return res;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Helical Sweep feature: a cut running out of the side of a shaft (automatic and given lengths); a boss runs out a pitch by default", () =>
        {
            // a 20 mm shaft along Z, 30 long; a V groove at z = 5 sketched on the Top plane (sketch y = -z), the helix advancing up Z
            var doc = new SpryCad.Core.Model.CadDocument();
            var c = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 10) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c.Id, Depth = "30" });
            var before = KernelTests.Vex(new SpryCad.Core.Regen.Regenerator().Rebuild(doc).State.Bodies[0].Solid);
            SpryCad.Core.Model.HelicalSweepFeature Add(SpryCad.Core.Model.CadDocument d, bool cut, bool runOut, string len)
            {
                var sk = new SpryCad.Core.Sketching.Sketch();
                var p1 = sk.AddPoint(new Vec2(8.6, -5)); var p2 = sk.AddPoint(new Vec2(11, -5 - 1.4)); var p3 = sk.AddPoint(new Vec2(11, -5 + 1.4));
                sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p1.Id, P2 = p2.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p2.Id, P2 = p3.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p3.Id, P2 = p1.Id });
                var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(0, -30));
                sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
                var s = d.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = sk });
                return d.AddFeature(new SpryCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "4", Revolutions = "4", IsCut = cut, RunOut = runOut, RunOutLength = len });
            }
            double Removed(bool runOut, string len)
            {
                var d = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
                var h = Add(d, true, runOut, len);
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(d);
                t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
                t.Eq(r.Status[h.Id].Warnings.Count, 0);
                t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
                return before - KernelTests.Vex(r.State.Bodies[0].Solid);
            }
            var plain = Removed(false, "");
            var auto = Removed(true, "");
            var given = Removed(true, "2");
            t.True(auto > plain && given > plain && auto > given, $"plain {plain:0.###}, 2 mm {given:0.###}, automatic {auto:0.###}");
            // the round trip keeps the run-out
            var d2 = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            var h2 = Add(d2, true, true, "3");
            var back = (SpryCad.Core.Model.HelicalSweepFeature)SpryCad.Core.Model.CadDocument.FromJson(d2.ToJson()).Find(h2.Id)!;
            t.True(back.RunOut && back.RunOutLength == "3", "saved and loaded");
            // a boss without a length runs out a pitch
            var d3 = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            var h3 = Add(d3, false, true, "");
            var r3 = new SpryCad.Core.Regen.Regenerator().Rebuild(d3);
            t.True(r3.Status[h3.Id].Ok, r3.Status[h3.Id].Error ?? "");
            t.True(r3.State.Bodies[0].Solid.Native!.Faces.Any(f => f.Tag.Name.Contains(".X.")), "a boss's run-out");
        });

        t.Test("Helical Sweep feature: the axis from the model (a shaft's cylindrical face, the Z axis) instead of a centerline", () =>
        {
            // a 20 mm shaft along Z; a V groove at z = 5 on the Top plane (sketch y = -z), with or without a centerline
            var doc = new SpryCad.Core.Model.CadDocument();
            var c = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 10) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c.Id, Depth = "30" });
            var r0 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            var before = KernelTests.Vex(r0.State.Bodies[0].Solid);
            var cyl = r0.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
            double Removed(bool centerline, SpryCad.Core.Model.DirRef? axis)
            {
                var d = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
                var sk = new SpryCad.Core.Sketching.Sketch();
                var p1 = sk.AddPoint(new Vec2(8.6, -5)); var p2 = sk.AddPoint(new Vec2(11, -5 - 1.4)); var p3 = sk.AddPoint(new Vec2(11, -5 + 1.4));
                sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p1.Id, P2 = p2.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p2.Id, P2 = p3.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p3.Id, P2 = p1.Id });
                if (centerline)
                {
                    var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(0, -30));
                    sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
                }
                var s = d.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = sk });
                var h = d.AddFeature(new SpryCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "4", Revolutions = "4", IsCut = true, Axis = axis });
                // saved and loaded with its axis
                d = SpryCad.Core.Model.CadDocument.FromJson(d.ToJson());
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(d);
                t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
                t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
                return before - KernelTests.Vex(r.State.Bodies[0].Solid);
            }
            var byLine = Removed(true, null);
            var byFace = Removed(false, new SpryCad.Core.Model.DirRef { Face = new SpryCad.Core.Model.FaceRef { Name = cyl.Tag.Name, Hint = new Vec3(10, 0, 15) } });
            var byZ = Removed(false, new SpryCad.Core.Model.DirRef { Axis = "Z" });
            t.True(byLine > 1, $"the groove removes material ({byLine:0.###})");
            // the same groove whichever way the axis was given (the face's axis runs up Z, as the centerline does)
            t.True(Math.Abs(byFace - byLine) < 1e-6 * byLine, $"by the face {byFace:0.######} vs the centerline {byLine:0.######}");
            t.True(Math.Abs(byZ - byLine) < 1e-6 * byLine, $"by Z {byZ:0.######} vs the centerline {byLine:0.######}");
        });

        t.Test("Helical Sweep feature: a round-wire spring cut flat through its first turn stays two half-round faces (no tube face); its mesh lies on the wire", () =>
        {
            // wire r 1 on r 5, pitch 5, 3 turns and a 10 mm run-out; a through cut below y = 0 (the start's height)
            // slices the first turn lengthwise. The two half-round faces met again past the cut and were merged
            // into one face all the way round the wire, which tessellated with holes.
            var doc = new SpryCad.Core.Model.CadDocument();
            var sk = CircleAt(new Vec2(5, 0), 1);
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(0, 6));
            sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = sk });
            var hs = doc.AddFeature(new SpryCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "5", Revolutions = "3", RunOut = true, RunOutLength = "10" });
            var cs = new SpryCad.Core.Sketching.Sketch();
            var q0 = cs.Point(SpryCad.Core.Sketching.Sketch.OriginId)!; var q1 = cs.AddPoint(new Vec2(7.7, 0)); var q2 = cs.AddPoint(new Vec2(7.7, -3.7)); var q3 = cs.AddPoint(new Vec2(0, -3.7));
            foreach (var (p, q) in new[] { (q0, q1), (q1, q2), (q2, q3), (q3, q0) }) cs.Add(new SpryCad.Core.Sketching.SkLine { P1 = p.Id, P2 = q.Id });
            var c2 = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = cs });
            var cut = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c2.Id, IsCut = true, End = SpryCad.Core.Model.EndCondition.ThroughAll, Reverse = true });
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[hs.Id].Ok && r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var solid = r.State.Bodies[0].Solid;
            t.Eq(solid.Native!.Validate(), null);
            t.Eq(solid.Topology.OpenHalfEdges, 0);
            t.Eq(solid.Native.Faces.Count(f => f.Surface.Kind == SurfaceKind.Screw), 2);
            // the uncut spring less a sliver: 3 turns of π r² along the wire's helix, and the run-out
            var vol = KernelTests.Vex(solid);
            t.True(vol > 3 * 2 * Math.PI * 5 * Math.PI && vol < 3 * 2 * Math.PI * 5 * Math.PI + Math.PI * 10, $"volume {vol:0.###}");
            // the display mesh of the wire's faces lies on the wire (long screw faces are tessellated in slabs: a bare
            // chord across a slab cut sank half the wire's radius, a dark notch every quarter turn)
            var dm = SpryCad.Kernel.DisplayMesh.Build(solid);
            double worst = 0;
            foreach (var fr in dm.Faces.Where(fr => fr.Name.Contains(".H.")))
                for (int ti = fr.IndexStart; ti < fr.IndexStart + fr.IndexCount; ti += 3)
                {
                    var c = Vec3.Zero;
                    for (int q = 0; q < 3; q++) { var v = dm.Indices[ti + q]; c += new Vec3(dm.Positions[3 * v], dm.Positions[3 * v + 1], dm.Positions[3 * v + 2]) / 3; }
                    var n = solid.Native.Faces.First(f => f.Tag.Name == fr.Name).Surface;
                    worst = Math.Max(worst, n.Screw!.Closest(c).dist);
                }
            t.True(worst < 0.05, $"the wire's mesh lies within 0.05 of it (worst {worst:0.####})");
        });

        t.Test("Circular Pattern of a helical cut about its axis: a two-start thread (the grooves' helical faces met in closed form, not traced)", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var c = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 10) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c.Id, Depth = "30" });
            var sk = new SpryCad.Core.Sketching.Sketch();
            var p1 = sk.AddPoint(new Vec2(8.6, -5)); var p2 = sk.AddPoint(new Vec2(11, -5 - 1.4)); var p3 = sk.AddPoint(new Vec2(11, -5 + 1.4));
            sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p1.Id, P2 = p2.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p2.Id, P2 = p3.Id }); sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p3.Id, P2 = p1.Id });
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(0, -30));
            sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = sk });
            // lead 8, two starts: the second groove half a turn round (4 mm along), clear of the first
            var h = doc.AddFeature(new SpryCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "8", Revolutions = "2", IsCut = true });
            var pat = doc.AddFeature(new SpryCad.Core.Model.CircularPatternFeature { Seeds = { h.Id }, Axis = new SpryCad.Core.Model.DirRef { Axis = "Z" }, Count = "2" });
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[pat.Id].Ok && r.Status[pat.Id].Warnings.Count == 0, r.Status[pat.Id].Error ?? string.Join("; ", r.Status[pat.Id].Warnings));
            double V(int i) => KernelTests.Vex(r.States[i].Bodies[0].Solid);
            var one = V(1) - V(3); var two = V(1) - V(4);
            t.True(Math.Abs(two - 2 * one) < 1e-6 * two, $"the second start removes as much as the first ({one:0.######}, both {two:0.######})");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            // it was 30 s of tracing the grooves' faces against each other
            t.True(r.Status[pat.Id].Milliseconds < 3000, $"the pattern in {r.Status[pat.Id].Milliseconds:0} ms");
        });

        t.Test("Acme lead screw demo: the multi-start nut moved onto the screw fits it with its clearance (no overlap); half a pitch out it clashes", () =>
        {
            foreach (var variant in new[] { Array.Empty<(string, string)>(), new[] { ("N", "2") }, new[] { ("D", "16"), ("P", "3"), ("N", "4") } })
            {
                var doc = Demos.All.First(x => x.file == "acme-lead-screw.sprt").build();
                foreach (var (n, e) in variant) doc.Equations.First(q => q.Name == n).Expr = e;
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
                var label = string.Join(", ", variant.Select(v => v.Item1 + "=" + v.Item2));
                t.True(r.Status.Values.All(s => s.Ok && s.Warnings.Count == 0), $"{label}: {string.Join("; ", r.Status.Values.Select(s => s.Error ?? string.Join(",", s.Warnings)).Where(x => x != ""))}");
                t.Eq(r.State.Bodies.Count, 2);
                var screw = r.State.Bodies[0].Solid.Native!; var nut = r.State.Bodies[1].Solid.Native!;
                var gap = r.EquationValues["Gap"]; var p = r.EquationValues["P"];
                double Overlap(double dz)
                {
                    var moved = nut.Transformed(Transform.Translation(new Vec3(-gap, 0, dz)));
                    var common = NBoolean.Run(screw, moved, BoolOp.Intersect);
                    return common.Faces.Count == 0 ? 0 : NMass.Of(common)!.Volume;
                }
                // on the screw as it stands (the nut's spaces where the screw's teeth are): clear
                t.True(Overlap(0) < 1e-6, $"{label}: the nut on the screw overlaps it by {Overlap(0):0.######} mm3");
                // half a pitch out (its spaces over the screw's spaces): the teeth clash
                t.True(Overlap(p / 2) > 1, $"{label}: half a pitch out it clashes ({Overlap(p / 2):0.###} mm3)");
            }
        });

        t.Test("Screw surfaces of the same helices meet along helices (or not at all); a plane through the axis cuts the generator's copies", () =>
        {
            var m = ScrewMotion.About(Vec3.Zero, Vec3.UnitY, 4, Vec3.UnitX);
            var gen = new NLine(new Vec3(8.4, 6, 0), new Vec3(2.5, -1.44, 0));
            var sa = new ScrewSurface(m, gen, 0, 2.9, 0, 6 * Math.PI);
            NSurface Turned(double ang) => NSurface.ScrewOf(new ScrewSurface(m, gen.Transformed(Transform.Rotation(Vec3.Zero, Vec3.UnitY, ang)), 0, 2.9, 0, 6 * Math.PI));
            // half a turn round: 2 mm along, past the flank's 1.44 mm rise: apart
            var apart = NIntersect.Analytic(NSurface.ScrewOf(sa), Turned(Math.PI), 1e-6);
            t.True(apart != null && apart.Count == 0, "half a turn round: apart, no tracing");
            // the other flank of a V (its mirror across the profile's middle) crosses this one: one helix, on both
            var other = new NLine(new Vec3(8.4, 6, 0), new Vec3(2.5, 1.44, 0));
            var sb = NSurface.ScrewOf(new ScrewSurface(m, other.Transformed(Transform.Rotation(Vec3.Zero, Vec3.UnitY, 0.3)), 0, 2.9, 0, 6 * Math.PI));
            var hx = NIntersect.Analytic(NSurface.ScrewOf(sa), sb, 1e-6);
            t.True(hx != null && hx.Count == 1 && hx[0] is NHelix, $"one helix ({hx?.Count})");
            foreach (var u in new[] { 0.3, 2.0, 5.5 })
            {
                var p = hx![0].Eval(u);
                t.True(sa.Closest(p).dist < 1e-9 && sb.Screw!.Closest(p).dist < 1e-9, $"helix point {u} on both");
            }
            // a plane through the axis: the generator carried to it each half turn (6π: 6 or 7 copies)
            var plane = NSurface.FromPlane(Plane.FromPointNormal(Vec3.Zero, new Vec3(Math.Sin(1.0), 0, Math.Cos(1.0))));
            var copies = NIntersect.Analytic(NSurface.ScrewOf(sa), plane, 1e-6);
            t.True(copies != null && copies.Count >= 6 && copies.All(cv => cv is NLine), $"copies of the generator ({copies?.Count})");
            foreach (var cv in copies!)
                foreach (var tt in new[] { 0.0, 1.5 })
                {
                    var p = cv.Eval(tt);
                    t.True(Math.Abs(plane.F_(p)) < 1e-9 && sa.Closest(p).dist < 1e-9, "on the plane and the surface");
                }
        });

        t.Test("Helical Sweep feature: a coil spring from a sketch (circle and centerline), saved and loaded", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var sk = CircleAt(new Vec2(12, 0), 1.5);
            var a = sk.AddPoint(new Vec2(0, -5)); var b = sk.AddPoint(new Vec2(0, 5));
            sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = sk });
            var hs = doc.AddFeature(new SpryCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "4", Revolutions = "5" });
            t.Eq(hs.Name, "Helical1");
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[hs.Id].Ok, r.Status[hs.Id].Error ?? "");
            double v = 5 * 2 * Math.PI * (Math.PI * 2.25) * 12;
            t.True(Math.Abs(KernelTests.Vex(r.State.Bodies[0].Solid) - v) < 1e-7 * v, "Pappus volume");
            var again = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            var r2 = new SpryCad.Core.Regen.Regenerator().Rebuild(again);
            t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - v) < 1e-7 * v, "same after loading");
        });

        t.Test("Thread feature: M10 on a shaft and in a hole (auto size), clearance removes more, saved and loaded", () =>
        {
            foreach (var external in new[] { true, false })
            {
                var doc = new SpryCad.Core.Model.CadDocument();
                SpryCad.Core.Model.Feature body;
                double surfaceR;
                if (external)
                {
                    // a 10 mm shaft, 30 long along +Z
                    var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 5) });
                    body = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "30" });
                    surfaceR = 5;
                }
                else
                {
                    // a 30 × 30 × 30 block with a hole of the M10 minor diameter through it along +Z
                    var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CoreTests.Rect(30, 30) });
                    doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "30" });
                    surfaceR = SpryCad.Core.Regen.Threads.MinorDiameter(10, 1.5) / 2;
                    var h = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(new Vec2(15, 15), surfaceR) });
                    body = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = h.Id, IsCut = true, End = SpryCad.Core.Model.EndCondition.ThroughAllBoth });
                }
                var before = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
                var cylFace = before.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
                var axisPoint = external ? Vec3.Zero : new Vec3(15, 15, 0);
                var hint = axisPoint + new Vec3(surfaceR, 0, 28); // near the far end (z = 30)
                double Removed(string clearance)
                {
                    var d = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
                    var th = d.AddFeature(new SpryCad.Core.Model.ThreadFeature
                    {
                        Face = new SpryCad.Core.Model.FaceRef { Name = cylFace.Tag.Name, Hint = hint }, Length = "15", Clearance = clearance, Chamfer = false,
                    });
                    var r = new SpryCad.Core.Regen.Regenerator().Rebuild(d);
                    t.True(r.Status[th.Id].Ok, $"{(external ? "external" : "internal")}: {r.Status[th.Id].Error}");
                    t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
                    // saved and loaded, the same
                    var r2 = new SpryCad.Core.Regen.Regenerator().Rebuild(SpryCad.Core.Model.CadDocument.FromJson(d.ToJson()));
                    var v = KernelTests.Vex(r.State.Bodies[0].Solid);
                    t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - v) < 1e-9 * v, "same after loading");
                    return KernelTests.Vex(before.State.Bodies[0].Solid) - v;
                }
                var removed = Removed("0");
                // the groove's cross-section in the material, swept between 15 mm ± a pitch of thread
                var groove = SpryCad.Core.Regen.Threads.Groove(external, surfaceR, 5, 1.5, 0).groove;
                var m = FirstMoment(Clip(groove, surfaceR, external));
                double lo = 2 * Math.PI * m * (15 - 1.5) / 1.5, hi = 2 * Math.PI * m * (15 + 1.5) / 1.5;
                t.True(removed > lo && removed < hi, $"{(external ? "external" : "internal")}: removed {removed:0.###} within {lo:0.###}..{hi:0.###}");
                var looser = Removed("0.2");
                t.True(looser > removed + 1, $"clearance removes more: {looser:0.###} vs {removed:0.###}");
            }
        });

        t.Test("Thread feature: cosmetic (no groove, a helix to draw), the view override both ways; left hand cuts as much as right hand", () =>
        {
            // a 10 mm shaft 20 long on a 30 mm collar 5 thick
            var doc = new SpryCad.Core.Model.CadDocument();
            var c = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 15) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c.Id, Depth = "5" });
            var top = doc.AddFeature(new SpryCad.Core.Model.PlaneFeature { Base = SpryCad.Core.Model.PlaneRef.Front, Offset = "5" });
            var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = new SpryCad.Core.Model.PlaneRef { Feature = top.Id }, Sketch = CircleAt(Vec2.Zero, 5) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "20" });
            var before = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            var v0 = KernelTests.Vex(before.State.Bodies[0].Solid);
            var cylFace = before.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 5) < 1e-9);
            SpryCad.Core.Model.ThreadFeature Thread(SpryCad.Core.Model.CadDocument d, bool cosmetic, bool left = false) => d.AddFeature(new SpryCad.Core.Model.ThreadFeature
            {
                Face = new SpryCad.Core.Model.FaceRef { Name = cylFace.Tag.Name, Hint = new Vec3(5, 0, 24) }, End = SpryCad.Core.Model.ThreadEnd.UpToNext,
                Chamfer = true, Clearance = "0.2", Cosmetic = cosmetic, LeftHand = left,
            });
            bool Modeled(SpryCad.Core.Regen.RegenResult r) => r.State.Bodies[0].Solid.Native!.Faces.Any(f => f.Surface.Kind == SurfaceKind.Screw);
            // cosmetic: the chamfer only (no turning down, no groove); a helix along the shaft from its free end
            var dc = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            var th = Thread(dc, true);
            var regen = new SpryCad.Core.Regen.Regenerator();
            var rc = regen.Rebuild(dc);
            t.True(rc.Status[th.Id].Ok, rc.Status[th.Id].Error ?? "");
            t.True(!Modeled(rc), "no groove");
            t.True(rc.State.Bodies[0].Solid.Native!.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cone), "the chamfer is kept");
            var removed = v0 - KernelTests.Vex(rc.State.Bodies[0].Solid);
            t.True(removed > 0 && removed < 30, $"only the chamfer removed ({removed:0.###}: a 45° ring about 1 mm deep)");
            var cos = rc.State.Cosmetics[th.Id];
            t.True(Math.Abs(cos.Radius - 5) < 1e-9 && Math.Abs(cos.Lead - 1.5) < 1e-9 && cos.External, "the helix: on the shaft, the M10 pitch");
            t.True(Math.Abs(cos.Origin.Z - 25) < 1e-6 && cos.Axis.Z < -0.999 && cos.Length > 15 && cos.Length <= 20, $"from the free end down the shaft ({cos.Origin}, {cos.Axis}, {cos.Length:0.###})");
            // the view override, on the same regenerator (the cache rebuilds the thread)
            regen.ThreadOverride = SpryCad.Core.Regen.ThreadDisplay.Modeled;
            var rm = regen.Rebuild(dc);
            t.True(Modeled(rm) && rm.State.Cosmetics.Count == 0, "all modeled");
            var modeledRemoved = v0 - KernelTests.Vex(rm.State.Bodies[0].Solid);
            var dm = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            Thread(dm, false);
            regen = new SpryCad.Core.Regen.Regenerator { ThreadOverride = SpryCad.Core.Regen.ThreadDisplay.Cosmetic };
            var rmc = regen.Rebuild(dm);
            t.True(!Modeled(rmc) && rmc.State.Cosmetics.Count == 1, "all cosmetic");
            regen.ThreadOverride = SpryCad.Core.Regen.ThreadDisplay.AsSet;
            t.True(Modeled(regen.Rebuild(dm)), "as set: modeled");
            // saved and loaded
            t.True(((SpryCad.Core.Model.ThreadFeature)SpryCad.Core.Model.CadDocument.FromJson(dc.ToJson()).Find(th.Id)!).Cosmetic, "saved");
            // a left-hand thread runs into the shaft as the right-hand one does (the same groove, mirrored)
            var dl = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            Thread(dl, false, left: true);
            var rl = new SpryCad.Core.Regen.Regenerator().Rebuild(dl);
            var leftRemoved = v0 - KernelTests.Vex(rl.State.Bodies[0].Solid);
            t.True(Math.Abs(leftRemoved - modeledRemoved) < 1e-3 * modeledRemoved, $"left hand removes {leftRemoved:0.###}, right hand {modeledRemoved:0.###}");
        });

        t.Test("Thread feature: blind, up to next and through all; lead-in chamfers; no buried groove where a turned-down thread ends", () =>
        {
            // a 10 mm shaft 20 long on a 30 mm collar 5 thick (the shoulder the thread must not cut)
            var doc = new SpryCad.Core.Model.CadDocument();
            var c = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 15) });
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = c.Id, Depth = "5" });
            var top = doc.AddFeature(new SpryCad.Core.Model.PlaneFeature { Base = SpryCad.Core.Model.PlaneRef.Front, Offset = "5" });
            var s = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = new SpryCad.Core.Model.PlaneRef { Feature = top.Id }, Sketch = CircleAt(Vec2.Zero, 5) });
            var shaft = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "20" });
            var before = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            var cylFace = before.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 5) < 1e-9);
            var collarFaces = before.State.Bodies[0].Solid.Native!.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 15) < 1e-9);
            (double removed, SpryCad.Kernel.Native.NBody body) Run(SpryCad.Core.Model.ThreadEnd end, string length, bool chamfer, string clearance,
                SpryCad.Core.Model.ThreadRunout runout = SpryCad.Core.Model.ThreadRunout.None)
            {
                var d = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
                var th = d.AddFeature(new SpryCad.Core.Model.ThreadFeature
                {
                    Face = new SpryCad.Core.Model.FaceRef { Name = cylFace.Tag.Name, Hint = new Vec3(5, 0, 24) }, End = end, Length = length, Chamfer = chamfer, Clearance = clearance,
                    Runout = runout,
                });
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(d);
                t.True(r.Status[th.Id].Ok, $"{end}: {r.Status[th.Id].Error}");
                t.Eq(r.State.Bodies.Count, 1);
                t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
                return (KernelTests.Vex(before.State.Bodies[0].Solid) - KernelTests.Vex(r.State.Bodies[0].Solid), r.State.Bodies[0].Solid.Native!);
            }
            var blind = Run(SpryCad.Core.Model.ThreadEnd.Blind, "8", false, "0").removed;
            var upTo = Run(SpryCad.Core.Model.ThreadEnd.UpToNext, "", false, "0");
            t.True(upTo.removed > blind * 1.8, $"up to next ({upTo.removed:0.##}) threads about the whole 20 mm (blind 8: {blind:0.##})");
            // the collar (the shoulder) is untouched: its cylinder is still one face, and no screw face reaches it
            t.Eq(upTo.body.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 15) < 1e-9), collarFaces);
            t.True(upTo.body.Faces.Where(f => f.Surface.Kind == SurfaceKind.Screw).SelectMany(f => f.Coedges).All(co => co.Edge.V0.P.Z > 5 - 1e-6), "the groove stops short of the shoulder");
            // through all runs into the collar (as a nut's thread runs out of its far face)
            var thru = Run(SpryCad.Core.Model.ThreadEnd.ThroughAll, "", false, "0").removed;
            t.True(thru > upTo.removed, $"through all ({thru:0.##}) removes more than up to next ({upTo.removed:0.##})");
            // a lead-in chamfer: a cone at the start end
            var ch = Run(SpryCad.Core.Model.ThreadEnd.UpToNext, "", true, "0");
            t.True(ch.removed > upTo.removed, "the chamfer removes material");
            t.True(ch.body.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cone), "a conical lead-in");
            // with a clearance the shaft is turned down over the thread: the groove's outer flat lies above the turned
            // surface all along, so none of it is left (where the turning stopped short, it ran on buried in the shaft:
            // a tunnel under the surface)
            var (g, turn) = SpryCad.Core.Regen.Threads.Groove(true, 5, 5, 1.5, 0.2);
            t.True(turn != null, "0.2 mm clearance turns an M10 shaft down");
            var outer = g.Max(q => q.X);
            var cl = Run(SpryCad.Core.Model.ThreadEnd.Blind, "12", true, "0.2").body;
            t.True(!cl.Faces.Any(f => f.Surface.Kind == SurfaceKind.Screw && f.Surface.Screw!.MinRadius > outer - 1e-6), "no buried groove");
            // a relief groove against the shoulder: a cylinder below the root, the collar untouched, the thread ending in it
            var rootR = g.Min(q => q.X);
            var gr0 = SpryCad.Core.Regen.Threads.Groove(true, 5, 5, 1.5, 0).groove.Min(q => q.X);
            var rel = Run(SpryCad.Core.Model.ThreadEnd.UpToNext, "", false, "0", SpryCad.Core.Model.ThreadRunout.Relief);
            t.True(rel.body.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - (gr0 - 0.15 * 1.5)) < 1e-6), "the relief's floor below the root");
            t.Eq(rel.body.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 15) < 1e-9), collarFaces);
            t.True(rel.removed > upTo.removed, "the relief removes material");
            // and along the shaft, after a blind thread
            var relB = Run(SpryCad.Core.Model.ThreadEnd.Blind, "8", false, "0", SpryCad.Core.Model.ThreadRunout.Relief);
            t.True(relB.removed > blind, "a relief after a blind thread");
            // a tangent run-out: planes on the groove's end, the end cap clear of the shaft, more removed than plain
            var tan = Run(SpryCad.Core.Model.ThreadEnd.Blind, "8", false, "0", SpryCad.Core.Model.ThreadRunout.Tangent);
            t.True(tan.removed > blind, $"the run-out removes material ({tan.removed:0.###} vs {blind:0.###})");
            t.True(tan.body.Faces.Any(f => f.Tag.Name.Contains(".X.")), "run-out faces");
            var tanU = Run(SpryCad.Core.Model.ThreadEnd.UpToNext, "", false, "0.2", SpryCad.Core.Model.ThreadRunout.Tangent);
            t.Eq(tanU.body.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.R - 15) < 1e-9), collarFaces);
            t.True(tanU.body.Faces.Where(f => f.Tag.Name.Contains(".X.")).SelectMany(f => f.Coedges).All(co => co.Edge.V0.P.Z > 5 - 1e-6), "the run-out stops short of the shoulder");
        });
    }

}
