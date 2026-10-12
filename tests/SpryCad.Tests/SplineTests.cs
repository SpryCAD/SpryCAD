using SpryCad.Kernel;

namespace SpryCad.Tests;

static class SplineTests
{
    public static void Run(TestRunner t)
    {
        static Vec3 V(double x, double y) => new(x, y, 0);
        static double Near(BSplineCurve c, Vec3 p)
        {
            double lo = c.Knots[c.Degree], hi = c.Knots[^(c.Degree + 1)], best = double.MaxValue;
            for (int i = 0; i <= 64; i++)
            {
                var s = c.ClosestParam(p, lo, hi, lo + (hi - lo) * i / 64);
                if (!double.IsNaN(s)) best = Math.Min(best, Vec3.Distance(c.Eval(s), p));
            }
            return best;
        }

        t.Test("Spline fit: an open spline through its points, ends on the first and last", () =>
        {
            var pts = new[] { V(0, 0), V(10, 8), V(20, -3), V(32, 5), V(40, 0) };
            var c = SplineFit.Through(pts, closed: false)!;
            t.Eq(c.Degree, 3);
            t.True(Vec3.Distance(c.Eval(c.Knots[3]), pts[0]) < 1e-12, "starts on the first point");
            t.True(Vec3.Distance(c.Eval(c.Knots[^4]), pts[^1]) < 1e-12, "ends on the last");
            foreach (var p in pts) t.True(Near(c, p) < 1e-9, $"through {p}");
            // two points: a line
            var l = SplineFit.Through(new[] { V(0, 0), V(5, 5) }, false)!;
            t.True(Vec3.Distance(l.Eval(0.5), V(2.5, 2.5)) < 1e-12, "two points: straight");
            t.True(SplineFit.Through(new[] { V(1, 1), V(1, 1) }, false) == null, "one distinct point: none");
        });

        t.Test("Spline fit: a closed spline through its points, smooth (C2) where it closes", () =>
        {
            var pts = new[] { V(0, 0), V(12, -2), V(20, 6), V(14, 15), V(3, 11), V(-4, 5) };
            var c = SplineFit.Through(pts, closed: true)!;
            double lo = c.Knots[3], hi = c.Knots[^4];
            t.True(Vec3.Distance(c.Eval(lo), pts[0]) < 1e-9 && Vec3.Distance(c.Eval(hi), pts[0]) < 1e-9, "closes on the first point");
            foreach (var p in pts) t.True(Near(c, p) < 1e-9, $"through {p}");
            c.Derivs(lo, out _, out var d1a, out var d2a);
            c.Derivs(hi, out _, out var d1b, out var d2b);
            t.True(Vec3.Distance(d1a, d1b) < 1e-7 * d1a.Length, $"tangent continuous: {d1a} vs {d1b}");
            t.True(Vec3.Distance(d2a, d2b) < 1e-6 * Math.Max(1, d2a.Length), $"curvature continuous: {d2a} vs {d2b}");
            // knot insertion keeps the curve
            var ins = SplineFit.Insert(c, 0.3 * (lo + hi));
            for (int i = 0; i <= 20; i++) { var u = lo + (hi - lo) * i / 20; t.True(Vec3.Distance(ins.Eval(u), c.Eval(u)) < 1e-12, "insert keeps the curve"); }
        });

        // a profile of two lines and a spline: (0, 0) → (40, 0), the spline up and over to (0, 20), back down
        var frame = new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var arch = SplineFit.Through(new[] { V(40, 0), V(34, 16), V(18, 24), V(0, 20) }, false)!;
        ProfileLoop Loop(Vec2 shift = default)
        {
            var c = shift == default ? arch : arch with { Poles = arch.Poles.Select(q => q + new Vec3(shift.X, shift.Y, 0)).ToArray() };
            var l = new ProfileLoop();
            l.Verts.Add(new ProfileVertex(new Vec2(0, 0) + shift, "L1"));
            l.AddSpline(c, 0, 1, "S1");
            l.Verts.Add(new ProfileVertex(new Vec2(0, 20) + shift, "L2"));
            return l;
        }
        // the region's area and first moment about y (for Pappus) from a fine polygon
        (double area, double mx) Shape(ProfileLoop l)
        {
            var pts = new List<Vec2> { new(0, 0) };
            for (int i = 0; i <= 20000; i++) { var p = arch.Eval(i / 20000.0); pts.Add(new Vec2(p.X, p.Y)); }
            double a = 0, cx = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                var cr = p.X * q.Y - q.X * p.Y;
                a += cr; cx += (p.X + q.X) * cr;
            }
            return (a / 2, cx / 6);
        }

        t.Test("Spline profile: extruded, a ruled B-spline side, exact volume, valid", () =>
        {
            var segs = Kernel.Native.NBuild.Segments(Loop());
            t.Eq(segs.Count, 3);
            t.True(segs[1].Spline != null && segs[1].Spline!.T0 == 0 && Math.Abs(segs[1].Spline!.T1 - 1) < 1e-12, "the spline one segment");
            var body = Kernel.Native.NBuild.Extrude(new[] { new ProfileRegion(Loop()) }, frame, 0, 15, "E");
            t.Eq(body.Validate(), null);
            t.Eq(body.Faces.Count(f => f.Surface.Kind == SurfaceKind.BSpline), 1);
            var (area, _) = Shape(Loop());
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, area * 15, area * 15 * 1e-6);
            // reversed (the loop the other way round: the spline run backwards)
            var rev = Kernel.Native.NBuild.Extrude(new[] { new ProfileRegion(Loop().Reversed()) }, frame, 0, 15, "R");
            t.Eq(rev.Validate(), null);
            t.Near(Kernel.Native.NMass.Of(rev)!.Volume, area * 15, area * 15 * 1e-6);
            // a closed spline alone: two halves
            var blob = SplineFit.Through(new[] { V(0, 0), V(10, -3), V(14, 8), V(4, 12) }, true)!;
            var bl = new ProfileLoop(); bl.AddSpline(blob, 0, 1, "B");
            var bs = Kernel.Native.NBuild.Segments(bl);
            t.Eq(bs.Count, 2);
            var bb = Kernel.Native.NBuild.Extrude(new[] { new ProfileRegion(bl) }, frame, 0, 5, "B");
            t.Eq(bb.Validate(), null);
            var dense = new ProfileLoop(Enumerable.Range(0, 20000).Select(i => { var p = blob.Eval(i / 20000.0); return new ProfileVertex(new Vec2(p.X, p.Y), "B"); }));
            t.Near(Kernel.Native.NMass.Of(bb)!.Volume, Math.Abs(dense.SignedArea()) * 5, Math.Abs(dense.SignedArea()) * 5 * 1e-6);
        });

        t.Test("Spline profile: revolved, a rational surface of revolution, exact volume (Pappus)", () =>
        {
            // about the y axis, the profile to its right: x ≥ 0 throughout
            var body = Kernel.Native.NBuild.Revolve(new[] { new ProfileRegion(Loop(new Vec2(5, 0))) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "R");
            t.Eq(body.Validate(), null);
            var (area, mx) = Shape(Loop());
            // the region shifted 5 along x: its first moment about the axis is mx + 5·area
            var vol = 2 * Math.PI * (mx + 5 * area);
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, vol, vol * 1e-6);
            var half = Kernel.Native.NBuild.Revolve(new[] { new ProfileRegion(Loop(new Vec2(5, 0))) }, frame, Vec2.Zero, new Vec2(0, 1), Math.PI * 0.75, "H");
            t.Eq(half.Validate(), null);
            t.Near(Kernel.Native.NMass.Of(half)!.Volume, vol * 0.375, vol * 1e-6);
        });

        t.Test("Sketch spline: lines and a spline make a profile; extruded, revolved, saved and read back", () =>
        {
            var doc = new Core.Model.CadDocument();
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(5, 0)); var b = sk.AddPoint(new Vec2(45, 0));
            var m1 = sk.AddPoint(new Vec2(39, 16)); var m2 = sk.AddPoint(new Vec2(23, 24)); var c = sk.AddPoint(new Vec2(5, 20));
            sk.Add(new Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
            // the spline runs from the line's end round to the other line's start
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = new() { b.Id, m1.Id, m2.Id, c.Id } });
            sk.Add(new Core.Sketching.SkLine { P1 = c.Id, P2 = a.Id });
            // the revolve's axis: a centerline along y
            var o = sk.Point(Core.Sketching.Sketch.OriginId)!; var up = sk.AddPoint(new Vec2(0, 30));
            var axis = sk.Add(new Core.Sketching.SkLine { P1 = o.Id, P2 = up.Id, Construction = true });
            var regions = Core.Sketching.ProfileExtractor.Extract(sk, out var warn);
            t.Eq(regions.Count, 1);
            t.True(regions[0].Outer.Verts.Any(v => v.Spline != null), "the spline's pieces in the profile");
            var s = doc.AddFeature(new Core.Model.SketchFeature { Plane = Core.Model.PlaneRef.Front, Sketch = sk });
            var ex = doc.AddFeature(new Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "12" });
            var r = new Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[ex.Id].Ok, r.Status[ex.Id].Error ?? "");
            var body = r.State.Bodies.Single().Solid.Native!;
            t.Eq(body.Validate(), null);
            t.True(body.Faces.Any(f => f.Surface.Kind == SurfaceKind.BSpline), "a B-spline side");
            var area = Math.Abs(new ProfileLoop(Enumerable.Range(0, 20001).SelectMany(i =>
            {
                // the polygon of the region: the spline densely, then the two line ends
                var q = sk.SplineCurve(spl)!.Eval(i / 20000.0); return new[] { new ProfileVertex(new Vec2(q.X, q.Y), "S") };
            }).Prepend(new ProfileVertex(new Vec2(5, 0), "L"))).SignedArea());
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, area * 12, area * 12 * 1e-6);
            // saved and read back: the same spline (its points by id), the same body
            var back = Core.Model.CadDocument.FromJson(doc.ToJson());
            var sk2 = ((Core.Model.SketchFeature)back.Features[0]).Sketch;
            t.True(sk2.Entities.OfType<Core.Sketching.SkSpline>().Single().Points.SequenceEqual(spl.Points), "spline points kept");
            var r2 = new Core.Regen.Regenerator().Rebuild(back);
            t.Near(Kernel.Native.NMass.Of(r2.State.Bodies.Single().Solid.Native!)!.Volume, area * 12, area * 12 * 1e-6);
            // revolved about the centerline instead (the profile right of it)
            back.Features.RemoveAt(1);
            var rv = back.AddFeature(new Core.Model.RevolveFeature { SketchId = back.Features[0].Id, AxisEntity = axis.Id, Angle = "360" });
            var r3 = new Core.Regen.Regenerator().Rebuild(back);
            t.True(r3.Status[rv.Id].Ok, r3.Status[rv.Id].Error ?? "");
            t.Eq(r3.State.Bodies.Single().Solid.Native!.Validate(), null);
        });

        t.Test("Sketch spline: tangent to a line at its start and to an arc at its end (the spline's ends follow them)", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 0));
            var m = sk.AddPoint(new Vec2(20, 9)); var c = sk.AddPoint(new Vec2(30, 0));
            var ac = sk.AddPoint(new Vec2(35, 0)); var ae = sk.AddPoint(new Vec2(35, 5));
            var line = sk.Add(new Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = new() { b.Id, m.Id, c.Id } });
            // the arc about (35, 0) from (30, 0) round to (35, 5): it leaves (30, 0) going -y (counter-clockwise: clockwise in this view... start at angle π)
            var arc = sk.Add(new Core.Sketching.SkArc { Center = ac.Id, Start = ae.Id, End = c.Id });
            var free = sk.SplineCurve(spl)!;
            free.Derivs(0, out _, out var f0, out _);
            t.True(Math.Abs(f0.Y) > 0.1 * f0.Length, "free: not along the line");
            sk.AddConstraint(Core.Sketching.CType.Tangent, line.Id, spl.Id);
            sk.AddConstraint(Core.Sketching.CType.Tangent, arc.Id, spl.Id);
            var tied = sk.SplineCurve(spl)!;
            tied.Derivs(0, out var p0, out var d0, out _);
            tied.Derivs(1, out var p1, out var d1, out _);
            t.True(Vec3.Distance(p0, new Vec3(10, 0, 0)) < 1e-12 && Vec3.Distance(p1, new Vec3(30, 0, 0)) < 1e-12, "the ends stay");
            t.True(Math.Abs(d0.Y) < 1e-9 * d0.Length && d0.X > 0, $"leaves the line along it: {d0}");
            // at (30, 0) the arc's radius is along x: the spline arrives square to it, continuing into the arc (the arc
            // arrives at its end (30, 0) counter-clockwise, i.e. going -y; the spline arrives there going +y)
            t.True(Math.Abs(d1.X) < 1e-9 * d1.Length, $"arrives square to the arc's radius: {d1}");
            t.True(d1.Y > 0, $"continuing smoothly into the arc: {d1}");
            foreach (var q in new[] { b, m, c }) t.True(Enumerable.Range(0, 2001).Min(i => Vec3.Distance(tied.Eval(i / 2000.0), new Vec3(q.X, q.Y, 0))) < 1e-3, "through its points");
            // the solver leaves it to the spline (nothing over defined, nothing moved)
            var res = Core.Sketching.SketchSolver.Solve(sk);
            t.True(res.Status != Core.Sketching.SketchStatus.OverDefined && res.Status != Core.Sketching.SketchStatus.Failed, $"solves: {res.Status}");
            t.True(Vec2.Distance(sk.P(m.Id), new Vec2(20, 9)) < 1e-9, "nothing moved");
        });

        t.Test("Spline profile: lofted to a scaled copy (exact volume) and to a circle", () =>
        {
            // a ruled loft between the profile and a copy 1.5 times as big 20 above: every level a copy scaled by
            // 1 + 0.5 z / 20, so the volume is A·h·(1 + (s − 1) + (s − 1)² / 3)
            var top = new Frame(new Vec3(0, 0, 20), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            var big = Loop().Transformed(p => p * 1.5, false);
            var loft = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, new[] { new ProfileRegion(Loop()) }), new Kernel.Native.NLoft.Section(top, new[] { new ProfileRegion(big) }) }, null, null, "LF");
            t.Eq(loft.Validate(), null);
            var (area, _) = Shape(Loop());
            var want = area * 20 * (1 + 0.5 + 0.25 / 3);
            t.Near(Kernel.Native.NMass.Of(loft)!.Volume, want, want * 1e-6);
            // a closed spline to a circle (no vertices to match: the spline's two halves meet the circle's)
            var blob = SplineFit.Through(new[] { V(-6, -4), V(7, -5), V(9, 6), V(-5, 7) }, true)!;
            var bl = new ProfileLoop(); bl.AddSpline(blob, 0, 1, "B");
            var round = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, new[] { new ProfileRegion(bl) }), new Kernel.Native.NLoft.Section(top, new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 5, "C")) }) }, null, null, "LC");
            t.Eq(round.Validate(), null);
            var v = Kernel.Native.NMass.Of(round)!.Volume;
            var a0 = Math.Abs(bl.SignedArea()); var a1 = Math.PI * 25;
            t.True(v > 20 * Math.Min(a0, a1) && v < 20 * Math.Max(a0, a1), $"between the end areas: {v}");
        });

        t.Test("Spline profile: swept along a line and an arc (Pappus round the arc)", () =>
        {
            // the profile in the x–y plane, swept up z 30, then round a quarter turn about a line parallel to y at x = -20
            var p0 = new Vec3(0, 0, 0); var p1 = new Vec3(0, 0, 30);
            var c = new Vec3(-20, 0, 30); var axis = Vec3.UnitY;
            var end = c + Transform.Rotation(Vec3.Zero, axis, -Math.PI / 2).Vector(p1 - c);
            // (turning about -y takes +z towards -x: the arc leaves along +z)
            var path = new List<Kernel.Native.NSweep.Seg>
            {
                new(p0, p1),
                new(p1, end, c, -axis, Math.PI / 2),
            };
            var body = Kernel.Native.NSweep.Sweep(new[] { new ProfileRegion(Loop()) }, frame, path, "SW");
            t.Eq(body.Validate(), null);
            var (area, mx) = Shape(Loop());
            // straight part: area·30; the bend: a quarter turn of the profile about x = -20 (its moment about that line)
            var want = area * 30 + Math.PI / 2 * (mx + 20 * area);
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, want, want * 1e-6);
        });

        t.Test("Spline profile: a boolean with a box, and a STEP round trip", () =>
        {
            var body = Kernel.Native.NBuild.Extrude(new[] { new ProfileRegion(Loop()) }, frame, 0, 15, "E");
            var box = Kernel.Native.NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(10, 10), new Vec2(50, 10), new Vec2(50, 40), new Vec2(10, 40) }, "B")) }, frame, -5, 8, "B");
            var cut = Kernel.Native.NBoolean.Run(body, box, BoolOp.Subtract);
            t.Eq(cut.Validate(), null);
            var full = Kernel.Native.NMass.Of(body)!.Volume;
            var common = Kernel.Native.NBoolean.Run(body, box, BoolOp.Intersect);
            t.Near(Kernel.Native.NMass.Of(cut)!.Volume + Kernel.Native.NMass.Of(common)!.Volume, full, full * 1e-6);
            using var ms = new MemoryStream();
            StepWriter.Write(ms, new[] { ("spline", Kernel.Native.NTess.ToSolid(cut)) }, "spline");
            t.Eq(StepWriter.LastError, null);
            var back = StepImport.Read(ms.ToArray(), "S.").Solids.Single().Body;
            t.Eq(back.Validate(), null);
            t.Near(Kernel.Native.NMass.Of(back)?.Volume ?? Kernel.Native.NTess.ToSolid(back, 1e-3).Volume(), Kernel.Native.NMass.Of(cut)!.Volume, full * 1e-5);
        });
    }
}
