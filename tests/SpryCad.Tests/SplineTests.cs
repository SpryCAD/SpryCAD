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

        t.Test("Spline profile: a helical sweep (a spline generator), its volume as the revolve's; a run-out; STEP", () =>
        {
            // the profile 5 right of the y axis, in a plane through it: a helical sweep of one turn sweeps as much as
            // the revolve (the advance runs along the profile's plane)
            var reg = new[] { new ProfileRegion(Loop(new Vec2(5, 0))) };
            var body = Kernel.Native.NHelical.Sweep(reg, frame, Vec3.Zero, Vec3.UnitY, 60, 2 * Math.PI, "HX");
            t.Eq(body.Validate(), null);
            t.True(body.Faces.Count(f => f.Surface.Kind == SurfaceKind.Screw && f.Surface.Screw!.Gen is Kernel.Native.NSpline) == 1, "a screw face on the spline");
            var (area, mx) = Shape(Loop());
            var vol = 2 * Math.PI * (mx + 5 * area);
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, vol, vol * 1e-6);
            var run = Kernel.Native.NHelical.Sweep(reg, frame, Vec3.Zero, Vec3.UnitY, 60, Math.PI, "HR", exitLength: 10);
            t.Eq(run.Validate(), null);
            using var ms = new MemoryStream();
            StepWriter.Write(ms, new[] { ("helix", Kernel.Native.NTess.ToSolid(body)) }, "helix");
            t.Eq(StepWriter.LastError, null);
            var back = StepImport.Read(ms.ToArray(), "H.").Solids.Single().Body;
            t.Eq(back.Validate(), null);
            t.Near(Kernel.Native.NTess.ToSolid(back, 1e-3).Volume(), vol, vol * 1e-3);
        });

        t.Test("Sweep along a spline path: a tube's volume is its section times the path's length (open and closed)", () =>
        {
            // a circle r 1 centred on the path, the path leaving its plane square (along +z)
            var tube = new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 1, "C")) };
            var path = SplineFit.Through(new[] { new Vec3(0, 0, 0), new Vec3(4, 3, 12), new Vec3(-5, 8, 25), new Vec3(2, 2, 40) }, false, startDir: Vec3.UnitZ)!;
            var seg = new Kernel.Native.NSweep.Seg(path.Eval(0), path.Eval(1), Curve: path);
            // and on along a line from its end, tangent
            path.Derivs(1, out var pe, out var de, out _);
            var line = new Kernel.Native.NSweep.Seg(pe, pe + de.Normalized * 10);
            var body = Kernel.Native.NSweep.Sweep(tube, frame, new[] { seg, line }, "T");
            t.Eq(body.Validate(), null);
            var want = Math.PI * (seg.Length + 10);
            t.Near(Kernel.Native.NMass.Of(body)!.Volume, want, want * 1e-3);

            // a closed spline path (its frame turned to meet itself at the end)
            var ring = SplineFit.Through(new[] { new Vec3(0, 0, 0), new Vec3(20, -4, 10), new Vec3(30, 6, 0), new Vec3(10, 12, -12) }, true)!;
            ring.Derivs(0, out var r0, out var rd, out _);
            var rf = new Frame(r0, rd.Normalized.AnyPerpendicular(), Vec3.Cross(rd.Normalized, rd.Normalized.AnyPerpendicular()), rd.Normalized);
            var closed = Kernel.Native.NSweep.Sweep(tube, rf, new[] { new Kernel.Native.NSweep.Seg(r0, r0, Curve: ring) }, "R");
            t.Eq(closed.Validate(), null);
            var len = new Kernel.Native.NSweep.Seg(r0, r0, Curve: ring).Length;
            t.Near(Kernel.Native.NMass.Of(closed)!.Volume, Math.PI * len, Math.PI * len * 1e-3);
        });

        t.Test("Sketch spline: tangent to another spline (the second follows the first; a round of them does not loop)", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 8)); var c = sk.AddPoint(new Vec2(20, 0));
            var d = sk.AddPoint(new Vec2(30, -10)); var e = sk.AddPoint(new Vec2(40, 5));
            var s1 = sk.Add(new Core.Sketching.SkSpline { Points = new() { a.Id, b.Id, c.Id } });
            var s2 = sk.Add(new Core.Sketching.SkSpline { Points = new() { c.Id, d.Id, e.Id } });
            sk.AddConstraint(Core.Sketching.CType.Tangent, s1.Id, s2.Id);
            sk.SplineCurve(s1)!.Derivs(1, out _, out var d1, out _);
            sk.SplineCurve(s2)!.Derivs(0, out _, out var d2, out _);
            t.True(Vec3.Distance(d1.Normalized, d2.Normalized) < 1e-9, $"the second leaves along the first: {d1.Normalized} vs {d2.Normalized}");
            // the first is its own (its curve as before)
            var free = SplineFit.Through(new[] { new Vec3(0, 0, 0), new Vec3(10, 8, 0), new Vec3(20, 0, 0) }, false)!;
            t.True(Vec3.Distance(free.Eval(0.3), sk.SplineCurve(s1)!.Eval(0.3)) < 1e-12, "the first unchanged");
            // a round (each following the other, at both ends) still gives curves
            var s3 = sk.Add(new Core.Sketching.SkSpline { Points = new() { e.Id, sk.AddPoint(new Vec2(20, 30)).Id, a.Id } });
            sk.AddConstraint(Core.Sketching.CType.Tangent, s2.Id, s3.Id);
            sk.AddConstraint(Core.Sketching.CType.Tangent, s3.Id, s1.Id);
            t.True(sk.SplineCurve(s1) != null && sk.SplineCurve(s2) != null && sk.SplineCurve(s3) != null, "curves round a cycle");
        });

        t.Test("Sketch spline: trimmed between two lines crossing it (two splines left, ending on the lines)", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var ids = new[] { new Vec2(0, 0), new Vec2(10, 10), new Vec2(20, 0), new Vec2(30, 10), new Vec2(40, 0) }.Select(q => sk.AddPoint(q).Id).ToList();
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = ids });
            var l1 = sk.Add(new Core.Sketching.SkLine { P1 = sk.AddPoint(new Vec2(15, -5)).Id, P2 = sk.AddPoint(new Vec2(15, 20)).Id });
            var l2 = sk.Add(new Core.Sketching.SkLine { P1 = sk.AddPoint(new Vec2(25, -5)).Id, P2 = sk.AddPoint(new Vec2(25, 20)).Id });
            var before = sk.SplineCurve(spl)!;
            var (cuts, why) = Core.Sketching.SketchOps.TrimSpline(sk, spl, new Vec2(20, 0.2));
            t.Eq(why, null);
            t.Eq(cuts.Count, 2);
            t.True(sk.Entity(spl.Id) == null, "the old spline gone");
            var left = sk.Entities.OfType<Core.Sketching.SkSpline>().ToList();
            t.Eq(left.Count, 2);
            var ends = left.SelectMany(s => new[] { sk.P(s.Points[0]), sk.P(s.Points[^1]) }).ToList();
            t.True(ends.Any(q => Math.Abs(q.X - 15) < 1e-3) && ends.Any(q => Math.Abs(q.X - 25) < 1e-3), "the cut ends on the lines");
            t.True(cuts.All(c => c.by == l1.Id || c.by == l2.Id), "cut by the lines");
            // the kept pieces follow the old curve closely (the same points, the cut ends on it)
            foreach (var s in left)
            {
                var c = sk.SplineCurve(s)!;
                for (int i = 0; i <= 20; i++)
                {
                    var q = c.Eval(i / 20.0);
                    var d = Enumerable.Range(0, 4001).Min(k => Vec3.Distance(before.Eval(k / 4000.0), q));
                    t.True(d < 0.01, $"near the old curve: {d:g3}");
                }
            }
        });

        t.Test("Sketch spline: a length dimension measures it, and drives it (the solver stretches it to length)", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(0, 0), fix: true); var b = sk.AddPoint(new Vec2(10, 6)); var c = sk.AddPoint(new Vec2(20, 0), fix: true);
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = new() { a.Id, b.Id, c.Id } });
            var dim = new Core.Sketching.SkConstraint { Id = sk.NewId(), Type = Core.Sketching.CType.Length, Refs = new[] { spl.Id } };
            var len0 = Core.Sketching.SketchSolver.Measure(sk, dim);
            t.True(len0 > 20 && len0 < 30, $"measured {len0}");
            // pull it longer: the free middle point moves until the curve is 30 long
            dim.Value = 30; dim.Expr = "30";
            sk.Constraints.Add(dim);
            var res = Core.Sketching.SketchSolver.Solve(sk);
            t.True(res.Status != Core.Sketching.SketchStatus.Failed, $"{res.Status}");
            t.Near(Core.Sketching.SketchSolver.Measure(sk, dim), 30, 1e-6);
            t.True(dim.IsDimension, "a dimension");
        });

        t.Test("Sketch spline: handles set its end derivatives; with a tangent relation, only how long", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 6)); var c = sk.AddPoint(new Vec2(20, 0));
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = new() { a.Id, b.Id, c.Id } });
            spl.StartDir = new Vec2(0, 30);
            sk.SplineCurve(spl)!.Derivs(0, out _, out var d0, out _);
            t.True(Vec3.Distance(d0, new Vec3(0, 30, 0)) < 1e-9, $"the handle is the derivative: {d0}");
            foreach (var q in new[] { a, b, c }) t.True(Enumerable.Range(0, 2001).Min(i => Vec3.Distance(sk.SplineCurve(spl)!.Eval(i / 2000.0), new Vec3(q.X, q.Y, 0))) < 1e-3, "through its points");
            // a line into the start, tangent: the direction is the line's, the length the handle's
            var l = sk.Add(new Core.Sketching.SkLine { P1 = sk.AddPoint(new Vec2(-10, 0)).Id, P2 = a.Id });
            sk.AddConstraint(Core.Sketching.CType.Tangent, l.Id, spl.Id);
            sk.SplineCurve(spl)!.Derivs(0, out _, out var d1, out _);
            t.True(Vec3.Distance(d1, new Vec3(30, 0, 0)) < 1e-9, $"along the line, the handle's length: {d1}");
            // the other end free: the plain fit's derivative there whatever the start's handle
            spl.StartDir = null;
            sk.SplineCurve(spl)!.Derivs(0, out _, out var d2, out _);
            t.True(Math.Abs(d2.Y) < 1e-9 && d2.X > 0, "a tangent relation alone: along the line");
        });

        t.Test("Sketch spline: offset (alone, in a tangent chain, closed): the copy keeps its distance, and follows", () =>
        {
            // every point of the copy at the distance from the original curve (its true offset, not just at the points)
            double Worst(Core.Sketching.Sketch sk, Core.Sketching.SkSpline o, Core.Sketching.SkSpline q, double want)
            {
                var oc = sk.SplineCurve(o)!; var qc = sk.SplineCurve(q)!;
                var poly = Enumerable.Range(0, 4001).Select(i => { var p = oc.Eval(i / 4000.0); return new Vec2(p.X, p.Y); }).ToList();
                double worst = 0;
                for (int i = 0; i <= 200; i++)
                {
                    var p3 = qc.Eval(i / 200.0); var p = new Vec2(p3.X, p3.Y);
                    var dmin = Enumerable.Range(0, poly.Count - 1).Min(k =>
                    {
                        var u = poly[k + 1] - poly[k]; var f = Math.Clamp(Vec2.Dot(p - poly[k], u) / u.LengthSq, 0, 1);
                        return Vec2.Distance(poly[k] + u * f, p);
                    });
                    worst = Math.Max(worst, Math.Abs(dmin - want));
                }
                return worst;
            }
            {
                var sk = new Core.Sketching.Sketch();
                var ids = new[] { new Vec2(0, 0), new Vec2(15, 10), new Vec2(30, 4), new Vec2(45, 12) }.Select(q => sk.AddPoint(q, fix: true).Id).ToList();
                var spl = sk.Add(new Core.Sketching.SkSpline { Points = ids });
                var (off, err) = Core.Sketching.SketchOps.Offset(sk, new[] { spl.Id }, 2);
                t.Eq(err, null);
                var res = Core.Sketching.SketchSolver.Solve(sk);
                t.True(res.Status != Core.Sketching.SketchStatus.Failed && res.Status != Core.Sketching.SketchStatus.OverDefined, $"{res.Status}");
                var copy = sk.Entities.OfType<Core.Sketching.SkSpline>().Single(x => x.Id != spl.Id);
                var w = Worst(sk, spl, copy, 2);
                t.True(w < 0.02, $"the copy 2 from the curve all along: off by {w:g3}");
                // the original moved: the copy follows
                sk.Point(ids[1])!.Y = 14;
                Core.Sketching.SketchSolver.Solve(sk);
                t.True(Worst(sk, spl, copy, 2) < 0.03, "follows");
                // and its distance, as a dimension
                off!.Value = 3; Core.Sketching.SketchSolver.Solve(sk);
                t.True(Worst(sk, spl, copy, 3) < 0.04, "the distance changed");
            }
            {
                // a line running on into a spline (tangent), offset as one chain
                var sk = new Core.Sketching.Sketch();
                var a = sk.AddPoint(new Vec2(-20, 0), fix: true); var b = sk.AddPoint(new Vec2(0, 0), fix: true);
                var ids = new List<long> { b.Id }; ids.AddRange(new[] { new Vec2(15, 8), new Vec2(30, 2) }.Select(q => sk.AddPoint(q, fix: true).Id));
                var line = sk.Add(new Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
                var spl = sk.Add(new Core.Sketching.SkSpline { Points = ids });
                sk.AddConstraint(Core.Sketching.CType.Tangent, line.Id, spl.Id);
                // (the line's chain, as Offset Entities takes it)
                var (_, err) = Core.Sketching.SketchOps.Offset(sk, Core.Sketching.SketchOps.Chain(sk, new[] { line.Id }), 1.5);
                t.Eq(err, null);
                var res = Core.Sketching.SketchSolver.Solve(sk);
                t.True(res.Status != Core.Sketching.SketchStatus.Failed && res.Status != Core.Sketching.SketchStatus.OverDefined, $"{res.Status}");
                var copy = sk.Entities.OfType<Core.Sketching.SkSpline>().Single(x => x.Id != spl.Id);
                t.True(Worst(sk, spl, copy, 1.5) < 0.02, "the spline's copy");
                var lc = sk.Entities.OfType<Core.Sketching.SkLine>().Single(x => x.Id != line.Id);
                t.True(lc.P2 == copy.Points[0] || lc.P1 == copy.Points[0], "the copies joined");
            }
            {
                var sk = new Core.Sketching.Sketch();
                var ids = new[] { new Vec2(0, 0), new Vec2(20, -3), new Vec2(26, 12), new Vec2(5, 15) }.Select(q => sk.AddPoint(q, fix: true).Id).ToList();
                var spl = sk.Add(new Core.Sketching.SkSpline { Points = ids, Closed = true });
                var (_, err) = Core.Sketching.SketchOps.Offset(sk, new[] { spl.Id }, 1);
                t.Eq(err, null);
                Core.Sketching.SketchSolver.Solve(sk);
                var copy = sk.Entities.OfType<Core.Sketching.SkSpline>().Single(x => x.Id != spl.Id);
                t.True(Worst(sk, spl, copy, 1) < 0.05, "a closed spline's copy");
                // outward: the copy's loop the bigger
                var regs = Core.Sketching.ProfileExtractor.Extract(sk, out _);
                t.True(regs.Count == 1 && regs[0].Holes.Count == 1, "the copy round the original (a ring)");
            }
        });

        t.Test("Loft along a spline centerline (a tube's volume) and with a spline guide", () =>
        {
            var path = SplineFit.Through(new[] { new Vec3(0, 0, 0), new Vec3(4, 3, 12), new Vec3(-5, 8, 25), new Vec3(2, 2, 40) }, false, startDir: Vec3.UnitZ)!;
            var seg = new Kernel.Native.NSweep.Seg(path.Eval(0), path.Eval(1), Curve: path);
            path.Derivs(1, out var pe, out var de, out _);
            var tz = de.Normalized; var tx = tz.AnyPerpendicular().Normalized;
            var endFrame = new Frame(pe, tx, Vec3.Cross(tz, tx), tz);
            var circ = new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 2, "C")) };
            var loft = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, circ), new Kernel.Native.NLoft.Section(endFrame, circ) }, null,
                new Kernel.Native.NLoft.Options { Centerline = new[] { seg } }, "LC");
            t.Eq(loft.Validate(), null);
            var want = Math.PI * 4 * seg.Length;
            t.Near(Kernel.Native.NMass.Of(loft)!.Volume, want, want * 2e-3);

            // two squares 30 apart, a spline guide from a corner of one to the same corner of the other, bowing out
            var sq = new[] { new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10) }, "Q")) };
            var top = new Frame(new Vec3(0, 0, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            var g = SplineFit.Through(new[] { new Vec3(10, 10, 0), new Vec3(14, 14, 15), new Vec3(10, 10, 30) }, false)!;
            var guided = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, sq), new Kernel.Native.NLoft.Section(top, sq) },
                new[] { (IReadOnlyList<Kernel.Native.NSweep.Seg>)new[] { new Kernel.Native.NSweep.Seg(g.Eval(0), g.Eval(1), Curve: g) } }, null, "LG");
            t.Eq(guided.Validate(), null);
            // bowed out: more than the plain prism, and through the guide's middle
            t.True(Kernel.Native.NMass.Of(guided)!.Volume > 3000 + 1, "bulges along the guide");
            guided.Link();
            t.True(guided.Edges.Any(e => Vec3.Distance(e.Mid, g.Eval(0.5)) < 0.05), "an edge along the guide");
        });

        t.Test("Sketch spline: curvature continuous (G2) with a line and an arc at its ends", () =>
        {
            var sk = new Core.Sketching.Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 0));
            var m = sk.AddPoint(new Vec2(20, 9)); var c = sk.AddPoint(new Vec2(30, 0));
            var ac = sk.AddPoint(new Vec2(35, 0)); var ae = sk.AddPoint(new Vec2(35, 5));
            var line = sk.Add(new Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
            var spl = sk.Add(new Core.Sketching.SkSpline { Points = new() { b.Id, m.Id, c.Id } });
            var arc = sk.Add(new Core.Sketching.SkArc { Center = ac.Id, Start = ae.Id, End = c.Id });
            sk.AddConstraint(Core.Sketching.CType.Curvature, line.Id, spl.Id);
            sk.AddConstraint(Core.Sketching.CType.Curvature, arc.Id, spl.Id);
            var cv = sk.SplineCurve(spl)!;
            Vec3 K(double u) { cv.Derivs(u, out _, out var d1, out var d2); var l2 = d1.LengthSq; var tn = d1 * (1 / Math.Sqrt(l2)); return (d2 - tn * Vec3.Dot(d2, tn)) * (1 / l2); }
            cv.Derivs(0, out _, out var t0, out _); cv.Derivs(1, out _, out var t1, out _);
            t.True(Math.Abs(t0.Y) < 1e-9 * t0.Length && t0.X > 0, "leaves along the line");
            t.True(K(0).Length < 1e-9, $"straight where it leaves the line: {K(0)}");
            t.True(Math.Abs(t1.X) < 1e-9 * t1.Length && t1.Y > 0, "arrives into the arc");
            t.True(Vec3.Distance(K(1), new Vec3(0.2, 0, 0)) < 1e-9, $"as curved as the arc (1/5, towards its centre): {K(1)}");
            foreach (var q in new[] { b, m, c }) t.True(Enumerable.Range(0, 4001).Min(i => Vec3.Distance(cv.Eval(i / 4000.0), new Vec3(q.X, q.Y, 0))) < 2e-3, "through its points");
            var res = Core.Sketching.SketchSolver.Solve(sk);
            t.True(res.Status is not (Core.Sketching.SketchStatus.Failed or Core.Sketching.SketchStatus.OverDefined), $"{res.Status}");
        });

        t.Test("Sketch spline: offset where it meets a line at a corner (both sides: the copy trimmed, and run on)", () =>
        {
            foreach (var reverse in new[] { false, true })
            {
                var sk = new Core.Sketching.Sketch();
                var a = sk.AddPoint(new Vec2(-20, 0), fix: true); var b = sk.AddPoint(new Vec2(0, 0), fix: true);
                var ids = new List<long> { b.Id }; ids.AddRange(new[] { new Vec2(6, 10), new Vec2(2, 22) }.Select(q => sk.AddPoint(q, fix: true).Id));
                var line = sk.Add(new Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
                var spl = sk.Add(new Core.Sketching.SkSpline { Points = ids });
                var (_, err) = Core.Sketching.SketchOps.Offset(sk, Core.Sketching.SketchOps.Chain(sk, new[] { line.Id }), 2, reverse);
                t.Eq(err, null);
                var res = Core.Sketching.SketchSolver.Solve(sk);
                t.True(res.Status is not (Core.Sketching.SketchStatus.Failed or Core.Sketching.SketchStatus.OverDefined), $"{res.Status}");
                var copy = sk.Entities.OfType<Core.Sketching.SkSpline>().Single(x => x.Id != spl.Id);
                var lc = sk.Entities.OfType<Core.Sketching.SkLine>().Single(x => x.Id != line.Id);
                t.True(lc.P1 == copy.Points[0] || lc.P2 == copy.Points[0], "the copies meet");
                var corner = sk.P(copy.Points[0]);
                t.Near(Math.Abs(corner.Y), 2, 1e-6);
                var oc = sk.SplineCurve(spl)!;
                var (_, fp, _) = Core.Sketching.Sketch.ExtendedFoot(oc, corner);
                t.Near(Vec2.Distance(fp, corner), 2, 1e-6);
                // the copy 2 from the original (run on past its start) all along
                var qc = sk.SplineCurve(copy)!;
                for (int i = 0; i <= 50; i++)
                {
                    var q = qc.Eval(i / 50.0);
                    var (_, f2, _) = Core.Sketching.Sketch.ExtendedFoot(oc, new Vec2(q.X, q.Y));
                    t.True(Math.Abs(Vec2.Distance(f2, new Vec2(q.X, q.Y)) - 2) < 0.03, $"at {i}: {Vec2.Distance(f2, new Vec2(q.X, q.Y)):g4} (reverse {reverse})");
                }
            }
        });

        t.Test("Loft: a rounded rectangle to an obround (eight segments to four)", () =>
        {
            static ProfileLoop RoundRect(double w, double h, double r)
            {
                var l = new ProfileLoop();
                double x = w / 2, y = h / 2;
                l.Verts.Add(new ProfileVertex(new Vec2(-x + r, -y), "B"));
                l.AddArc(new Vec2(x - r, -y + r), r, -Math.PI / 2, 0, true, "A1");
                l.Verts.Add(new ProfileVertex(new Vec2(x, -y + r), "R"));
                l.AddArc(new Vec2(x - r, y - r), r, 0, Math.PI / 2, true, "A2");
                l.Verts.Add(new ProfileVertex(new Vec2(x - r, y), "T"));
                l.AddArc(new Vec2(-x + r, y - r), r, Math.PI / 2, Math.PI, true, "A3");
                l.Verts.Add(new ProfileVertex(new Vec2(-x, y - r), "L"));
                l.AddArc(new Vec2(-x + r, -y + r), r, Math.PI, 1.5 * Math.PI, true, "A4");
                return l;
            }
            // turned by phi (the obround's flat sides then run another way, and its loop starts elsewhere)
            static ProfileLoop Obround(double w, double h, double phi)
            {
                var l = new ProfileLoop(); double r = h / 2, x = w / 2 - r;
                Vec2 R(double px, double py) => new(px * Math.Cos(phi) - py * Math.Sin(phi), px * Math.Sin(phi) + py * Math.Cos(phi));
                l.Verts.Add(new ProfileVertex(R(-x, -r), "OB"));
                l.AddArc(R(x, 0), r, -Math.PI / 2 + phi, Math.PI / 2 + phi, true, "OR");
                l.Verts.Add(new ProfileVertex(R(x, r), "OT"));
                l.AddArc(R(-x, 0), r, Math.PI / 2 + phi, 1.5 * Math.PI + phi, true, "OL");
                return l;
            }
            var a = new[] { new ProfileRegion(RoundRect(40, 24, 4)) };
            var top = new Frame(new Vec3(0, 0, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            foreach (var phi in new[] { 0, 0.5 * Math.PI, Math.PI, 1.5 * Math.PI, 0.3 })
                foreach (var (w, h) in new[] { (36.0, 14.0), (30.0, 20.0) })
                {
                    var b = new[] { new ProfileRegion(Obround(w, h, phi)) };
                    var loft = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, a), new Kernel.Native.NLoft.Section(top, b) }, null, null, "RO");
                    t.Eq(loft.Validate(), null);
                    var v = Kernel.Native.NMass.Of(loft)!.Volume;
                    double a0 = 40 * 24 - (4 - Math.PI) * 16, a1 = (w - h) * h + Math.PI * h * h / 4;
                    t.True(v > 30 * Math.Min(a0, a1) * 0.9 && v < 30 * Math.Max(a0, a1), $"between the ends: {v} (turned {phi:0.##})");
                }
            // the brake duct's: a flat plenum section (5.25 by 1, R.25 corners) to the outlet's tall slot (2.5 across,
            // 3.25 long) on a plane tilted 20°, turned either way
            var flat = new[] { new ProfileRegion(RoundRect(5.25, 1, 0.25)) };
            foreach (var tilt in new[] { 0.0, 20.0, -20.0 })
                foreach (var phi in new[] { 0.5 * Math.PI, 0.0 })
                {
                    var tr = tilt * Math.PI / 180;
                    var up = new Frame(new Vec3(0, 0, 2.5), Vec3.UnitX, new Vec3(0, Math.Cos(tr), Math.Sin(tr)), new Vec3(0, -Math.Sin(tr), Math.Cos(tr)));
                    var slot = new[] { new ProfileRegion(Obround(3.25, 2.5, phi)) };
                    var loft = Kernel.Native.NLoft.Loft(new[] { new Kernel.Native.NLoft.Section(frame, flat), new Kernel.Native.NLoft.Section(up, slot) }, null, null, "RS");
                    t.Eq(loft.Validate(), null);
                }
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
