using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Lofts through sections, with and without guides: valid closed bodies, exact where the volume is known.</summary>
static class LoftTests
{
    static Frame At(double z) => new(new Vec3(0, 0, z), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
    static ProfileRegion Rect(double w, double h, double cx = 0, double cy = 0) =>
        new(ProfileLoop.Polygon(new[] { new Vec2(cx - w / 2, cy - h / 2), new Vec2(cx + w / 2, cy - h / 2), new Vec2(cx + w / 2, cy + h / 2), new Vec2(cx - w / 2, cy + h / 2) }, "L"));
    static ProfileRegion Circle(double r, string name = "C") => new(ProfileLoop.Circle(Vec2.Zero, r, name));
    static NLoft.Section Sec(double z, params ProfileRegion[] r) => new(At(z), r);

    static double Volume(TestRunner t, NBody b, string what)
    {
        t.Eq(b.Validate(), null);
        var mp = NMass.Of(b);
        t.True(mp != null, $"{what}: integrable");
        t.Eq(NTess.ToSolid(b).Topology.OpenHalfEdges, 0);
        return mp!.Volume;
    }

    static void Exact(TestRunner t, NBody b, double volume, string what, double rel = 1e-9)
    {
        var v = Volume(t, b, what);
        t.True(Math.Abs(v - volume) < rel * volume, $"{what}: volume {v:R} vs {volume:R}");
    }

    static Vec3 Rot(Vec3 p, double a) => new(p.X * Math.Cos(a) - p.Y * Math.Sin(a), p.X * Math.Sin(a) + p.Y * Math.Cos(a), p.Z);

    public static void Run(TestRunner t)
    {
        t.Test("Loft: square to square (a prism of planes), square to a smaller square (a frustum)", () =>
        {
            var b = NLoft.Loft(new[] { Sec(0, Rect(20, 20)), Sec(30, Rect(20, 20)) }, null, null, "F");
            Exact(t, b, 20 * 20 * 30, "prism");
            t.Eq(b.Faces.Count, 6);
            t.True(b.Faces.All(f => f.Surface.Kind == SurfaceKind.Plane), "every face a plane");
            t.True(b.Edges.All(e => e.Curve is NLine), "every edge a line");
            t.True(b.Faces.Any(f => f.Tag.Name == "F.Start") && b.Faces.Any(f => f.Tag.Name == "F.End"), "caps named");
            var f = NLoft.Loft(new[] { Sec(0, Rect(20, 20)), Sec(30, Rect(10, 10)) }, null, null, "F");
            Exact(t, f, 30.0 / 3 * (400 + 100 + 200), "frustum");
            t.True(f.Faces.All(x => x.Surface.Kind == SurfaceKind.Plane), "frustum faces planes");
        });

        t.Test("Loft: circle to circle (a cone frustum, exact); three equal circles (a cylinder)", () =>
        {
            var b = NLoft.Loft(new[] { Sec(0, Circle(10)), Sec(20, Circle(5)) }, null, null, "F");
            Exact(t, b, Math.PI * 20 / 3 * (100 + 50 + 25), "cone frustum");
            t.Eq(b.Faces.Count, 4); // two halves and the caps
            t.True(b.Faces.Count(x => x.Surface.Kind == SurfaceKind.Cone) == 2, "the sides are cones");
            var c = NLoft.Loft(new[] { Sec(0, Circle(8)), Sec(10, Circle(8)), Sec(25, Circle(8)) }, null, null, "F");
            Exact(t, c, Math.PI * 64 * 25, "cylinder");
            t.True(c.Faces.Count(x => x.Surface.Kind == SurfaceKind.Cylinder) == 2, "the sides are cylinders");
        });

        t.Test("Loft: square to a point (a pyramid); a tube (circles with holes)", () =>
        {
            var p = NLoft.Loft(new[] { Sec(0, Rect(20, 20)), new NLoft.Section(At(30), Array.Empty<ProfileRegion>(), new Vec3(0, 0, 30)) }, null, null, "P");
            Exact(t, p, 400 * 30 / 3.0, "pyramid");
            t.Eq(p.Faces.Count, 5);
            var a = NLoft.Loft(new[] { new NLoft.Section(At(-10), Array.Empty<ProfileRegion>(), new Vec3(0, 0, -10)), Sec(0, Circle(6)) }, null, null, "P");
            Exact(t, a, Math.PI * 36 * 10 / 3, "cone to its apex");
            var tube = new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "O"), new[] { ProfileLoop.Circle(Vec2.Zero, 6, "I") });
            var tube2 = new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 7, "O"), new[] { ProfileLoop.Circle(Vec2.Zero, 4, "I") });
            var b = NLoft.Loft(new[] { Sec(0, tube), Sec(15, tube2) }, null, null, "T");
            double Cone(double r0, double r1, double h) => Math.PI * h / 3 * (r0 * r0 + r0 * r1 + r1 * r1);
            Exact(t, b, Cone(10, 7, 15) - Cone(6, 4, 15), "tube");
        });

        t.Test("Loft: square to circle, rotated rectangles, three sections with end tangency, two regions", () =>
        {
            var b = NLoft.Loft(new[] { Sec(0, Rect(20, 20)), Sec(25, Circle(8)) }, null, null, "Q");
            var v = Volume(t, b, "square to circle");
            t.True(v > Math.PI * 64 * 25 && v < 400 * 25, $"between the cylinder and the prism: {v}");
            t.Eq(b.Faces.Count, 6); // four sides (a line to a quarter circle each), two caps
            // a rectangle turned 30° (least twist: its own corners, not the next ones)
            var r2 = new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(-15, -5), new Vec2(15, -5), new Vec2(15, 5), new Vec2(-15, 5) }.Select(q => new Vec2(q.X * Math.Cos(0.5) - q.Y * Math.Sin(0.5), q.X * Math.Sin(0.5) + q.Y * Math.Cos(0.5))), "L"));
            var tw = NLoft.Loft(new[] { Sec(0, Rect(30, 10)), Sec(20, r2) }, null, null, "W");
            var vt = Volume(t, tw, "twisted");
            t.True(vt < 300 * 20 && vt > 200 * 20, $"twisted volume {vt}");
            var three = NLoft.Loft(new[] { Sec(0, Rect(20, 20)), Sec(20, Circle(6)), Sec(40, Rect(16, 16)) }, null,
                new NLoft.Options(NLoft.Tangency.Normal, 1, NLoft.Tangency.Normal, 1), "3");
            Volume(t, three, "three sections, square ends");
            // the end tangency: the side leaves the start square to its plane
            var side = three.Faces.First(f => f.Surface.Kind == SurfaceKind.BSpline).Surface.Nurbs!;
            var dv = side.Derivs(0.5 * (side.U0 + side.U1), side.V0).sv;
            t.True(Math.Abs(dv.Normalized.Z) > 1 - 1e-9, $"leaves square: {dv}");
            var two = NLoft.Loft(new[] { Sec(0, Rect(10, 10, -20), Rect(10, 10, 20)), Sec(10, Circle(3, "A"), new ProfileRegion(ProfileLoop.Circle(new Vec2(25, 0), 3, "B"))) }, null, null, "2");
            t.Eq(two.Lumps().Count, 2);
            Volume(t, two, "two regions");
        });

        t.Test("Loft: square to hexagon (vertex counts differ), round a bend on tilted planes, a guide on three sections", () =>
        {
            var hex = new ProfileRegion(ProfileLoop.Polygon(Enumerable.Range(0, 6).Select(i => Vec2.FromAngle(i * Math.PI / 3, 9)), "H"));
            var h = NLoft.Loft(new[] { Sec(0, Rect(16, 16)), Sec(20, hex) }, null, null, "H");
            var vh = Volume(t, h, "square to hexagon");
            t.True(vh > 20 * Math.Min(256, 1.5 * Math.Sqrt(3) * 81) * 0.9 && vh < 20 * 256, $"volume {vh}");
            t.True(h.Faces.Count >= 8, $"faces split where the corners do not match: {h.Faces.Count}");
            // three circles round a quarter bend: planes at 0°, 45° and 90° about the Y axis through (40, 0, 0)
            Frame Tilt(double a)
            {
                var o = new Vec3(40 - 40 * Math.Cos(a), 0, 40 * Math.Sin(a));
                var n = new Vec3(Math.Sin(a), 0, Math.Cos(a));
                var x = new Vec3(Math.Cos(a), 0, -Math.Sin(a));
                return new Frame(o, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY));
            }
            var bend = NLoft.Loft(new[] { new NLoft.Section(Tilt(0), new[] { Circle(6) }), new NLoft.Section(Tilt(Math.PI / 4), new[] { Circle(6) }), new NLoft.Section(Tilt(Math.PI / 2), new[] { Circle(6) }) }, null, null, "B");
            var vb = Volume(t, bend, "bend");
            var torus = Math.PI * 36 * 40 * Math.PI / 2;
            t.True(Math.Abs(vb - torus) < 0.02 * torus, $"close to the torus's quarter: {vb} vs {torus}");
            // a guide through three circles (r 10, 8, 10 at z 0, 20, 40) bowing out at x = 14 halfway
            double cx = (14 * 14 - 10 * 10 - 400) / (2.0 * 4);
            var c = new Vec3(cx, 0, 20); var a = new Vec3(10, 0, 0); var e = new Vec3(10, 0, 40);
            var r = a - c; var axis = Vec3.Cross(r, e - c).Normalized;
            var arc = new List<NSweep.Seg> { new(a, e, c, axis, Math.Acos(Vec3.Dot(r.Normalized, (e - c).Normalized))) };
            string? err = null;
            try { NLoft.Loft(new[] { Sec(0, Circle(10)), Sec(20, Circle(8)), Sec(40, Circle(10)) }, new[] { arc }, null, "G"); }
            catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("does not meet"), "the guide must meet the middle circle: " + err);
            var g3 = NLoft.Loft(new[] { Sec(0, Circle(10)), Sec(20, Circle(14)), Sec(40, Circle(10)) }, new[] { arc }, null, "G");
            Volume(t, g3, "guide through three sections");
        });

        t.Test("Loft: a guide bows the side out through its point; the sections stay exact", () =>
        {
            // circles r 10 at z 0 and 40; a guide arc in XZ from (10,0,0) through (16,0,20) to (10,0,40)
            // its centre on z = 20, as far from (10, 0) as from (16, 20): (10 - cx)² + 20² = (16 - cx)²
            double cx = (16 * 16 - 10 * 10 - 400) / (2.0 * (16 - 10));
            var c = new Vec3(cx, 0, 20);
            var a = new Vec3(10, 0, 0); var e = new Vec3(10, 0, 40);
            var r = a - c;
            var axis = Vec3.Cross(r, e - c).Normalized; // turning from a to e through (16, 0, 20)
            double ang = Math.Acos(Vec3.Dot(r.Normalized, (e - c).Normalized));
            var guide = new List<NSweep.Seg> { new(a, e, c, axis, ang) };
            var mid = c + (r * Math.Cos(ang / 2) + Vec3.Cross(axis, r) * Math.Sin(ang / 2));
            t.True(Vec3.Distance(mid, new Vec3(16, 0, 20)) < 1e-9, $"the guide's middle {mid}");
            var b = NLoft.Loft(new[] { Sec(0, Circle(10)), Sec(40, Circle(10)) }, new[] { guide }, null, "G");
            var v = Volume(t, b, "guided");
            t.True(v > Math.PI * 100 * 40 * 1.02, $"bulges: {v} vs cylinder {Math.PI * 100 * 40}");
            // the guide is an edge: it passes through the guide's middle
            var ge = b.Edges.Where(x => x.Curve is NSpline).ToList();
            t.True(ge.Any(x => Enumerable.Range(0, 201).Min(i => Vec3.Distance(x.At(i / 200.0), mid)) < 1e-3), "an edge through the guide's middle");
            t.True(ge.Any(x => Enumerable.Range(0, 2001).Min(i => Vec3.Distance(x.At(i / 2000.0), mid)) < 1e-5), "close to it");
            // and follows the guide all along (within the fit's tolerance), the faces either side on it
            var onGuide = ge.Single(x => Enumerable.Range(0, 201).Min(i => Vec3.Distance(x.At(i / 200.0), mid)) < 1e-3);
            double off = 0;
            for (int i = 0; i <= 400; i++)
            {
                var q = onGuide.At(i / 400.0);
                off = Math.Max(off, Math.Abs(Vec3.Distance(q, c) - r.Length) + Math.Abs(q.Y));
                foreach (var u in onGuide.Uses) off = Math.Max(off, Vec3.Distance(u.Face.Surface.Project(q), q));
            }
            t.True(off < 1e-5, $"on the guide and its faces: {off:E2}");
            foreach (var f in b.Faces.Where(f => f.Surface.Kind == SurfaceKind.BSpline))
                t.True(f.Surface.Nurbs!.MinCurvatureRadius() > 5, $"no wiggles: {f.Surface.Nurbs!.MinCurvatureRadius()}");
            // the side opposite is untouched (a straight line along x = -10)
            t.True(b.Edges.Any(x => x.Curve is NLine && Math.Abs(x.V0.P.X + 10) < 1e-9 && Math.Abs(x.V1.P.X + 10) < 1e-9), "the far side stays straight");
            // smooth across the guide: the faces either side have the same normal along it
            var faces = b.Faces.Where(f => f.Surface.Kind == SurfaceKind.BSpline).ToList();
            t.Eq(faces.Count, 2);
            var p = new Vec3(16, 0, 20);
            var n0 = faces[0].Surface.Normal(faces[0].Surface.Project(p)) * (faces[0].Sense ? 1 : -1);
            var n1 = faces[1].Surface.Normal(faces[1].Surface.Project(p)) * (faces[1].Sense ? 1 : -1);
            t.True(Vec3.Dot(n0, n1) > 1 - 1e-6, $"tangent across the guide: {n0} {n1}");
        });

        t.Test("Loft: two guides at a rectangle's corners; a guide that misses is explained", () =>
        {
            // rectangle 20 × 10 at z 0 and z 30; guides from the corners (10, ±5) out to (14, ±5) at z 15 and back
            List<NSweep.Seg> Guide(double y)
            {
                double cx = (14 * 14 - 10 * 10 - 225) / (2.0 * 4); // circle through (10,0), (14,15), (10,30) in (x, z)
                var c = new Vec3(cx, y, 15);
                var a = new Vec3(10, y, 0); var e = new Vec3(10, y, 30);
                var r = a - c; var axis = Vec3.Cross(r, e - c).Normalized;
                return new List<NSweep.Seg> { new(a, e, c, axis, Math.Acos(Vec3.Dot(r.Normalized, (e - c).Normalized))) };
            }
            var b = NLoft.Loft(new[] { Sec(0, Rect(20, 10)), Sec(30, Rect(20, 10)) }, new[] { Guide(-5), Guide(5) }, null, "G");
            var v = Volume(t, b, "two guides");
            t.True(v > 200 * 30, $"bulges: {v}");
            t.True(b.Faces.Count(f => f.Surface.Kind == SurfaceKind.Plane) >= 4, "the far side and the faces between the guides stay planes");
            // a guide off the profile
            var off = new List<NSweep.Seg> { new(new Vec3(12, 0, 0), new Vec3(12, 0, 30)) };
            string? err = null;
            try { NLoft.Loft(new[] { Sec(0, Rect(20, 10)), Sec(30, Rect(20, 10)) }, new[] { off }, null, "G"); } catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("does not meet"), "explained: " + err);
        });
    }
}

/// <summary>The Loft feature: profile, point and guide sketches in a document; boss and cut; saved, loaded and patterned.</summary>
static class LoftFeatureTests
{
    static SpryCad.Core.Sketching.Sketch CircleAt(Vec2 c, double r)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var p = sk.AddPoint(c);
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = p.Id, R = r });
        return sk;
    }

    static SpryCad.Core.Sketching.Sketch PointAt(Vec2 c)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        sk.Add(new SpryCad.Core.Sketching.SkPointEntity { P = sk.AddPoint(c).Id });
        return sk;
    }

    // a rectangle w × h centred on c
    static SpryCad.Core.Sketching.Sketch RectAt(Vec2 c, double w, double h)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var p = new[] { sk.AddPoint(c + new Vec2(-w / 2, -h / 2)), sk.AddPoint(c + new Vec2(w / 2, -h / 2)), sk.AddPoint(c + new Vec2(w / 2, h / 2)), sk.AddPoint(c + new Vec2(-w / 2, h / 2)) };
        for (int i = 0; i < 4; i++) sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p[i].Id, P2 = p[(i + 1) % 4].Id });
        return sk;
    }

    static SpryCad.Core.Model.SketchFeature OnPlane(SpryCad.Core.Model.CadDocument doc, double offset, SpryCad.Core.Sketching.Sketch sk)
    {
        if (offset == 0) return doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = sk });
        var pl = doc.AddFeature(new SpryCad.Core.Model.PlaneFeature { Base = SpryCad.Core.Model.PlaneRef.Top, Offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = new SpryCad.Core.Model.PlaneRef { Feature = pl.Id }, Sketch = sk });
    }

    public static void Run(TestRunner t)
    {
        t.Test("Loft feature: square to circle, saved and loaded, patterned; square to a point", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var a = OnPlane(doc, 0, SpryCad.Tests.CoreTests.Rect(40, 40));
            var b = OnPlane(doc, 30, CircleAt(new Vec2(20, 20), 12));
            var lf = doc.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { a.Id, b.Id } });
            t.Eq(lf.Name, "Loft1");
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[lf.Id].Ok, r.Status[lf.Id].Error ?? "");
            var vol = KernelTests.Vex(r.State.Bodies[0].Solid);
            t.True(vol > Math.PI * 144 * 30 && vol < 1600 * 30, $"volume {vol}");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            var again = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            t.True(again.Features.Last() is SpryCad.Core.Model.LoftFeature l2 && l2.SectionSketchIds.SequenceEqual(new[] { a.Id, b.Id }), "loft saved");
            var r2 = new SpryCad.Core.Regen.Regenerator().Rebuild(again);
            t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - vol) < 1e-9 * vol, "same volume after loading");
            doc.AddFeature(new SpryCad.Core.Model.LinearPatternFeature { Seeds = { lf.Id }, Direction = new SpryCad.Core.Model.DirRef { Axis = "X" }, Spacing = "60", Count = "2" });
            var r3 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r3.Status[doc.Features.Last().Id].Ok, r3.Status[doc.Features.Last().Id].Error ?? "");
            t.True(Math.Abs(r3.State.Bodies.Sum(x => KernelTests.Vex(x.Solid)) - 2 * vol) < 1e-6 * vol, "two lofts");

            var doc2 = new SpryCad.Core.Model.CadDocument();
            var s0 = OnPlane(doc2, 0, SpryCad.Tests.CoreTests.Rect(40, 40));
            var s1 = OnPlane(doc2, 30, PointAt(new Vec2(20, 20)));
            var py = doc2.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { s0.Id, s1.Id } });
            var rp = new SpryCad.Core.Regen.Regenerator().Rebuild(doc2);
            t.True(rp.Status[py.Id].Ok, rp.Status[py.Id].Error ?? "");
            t.True(Math.Abs(KernelTests.Vex(rp.State.Bodies[0].Solid) - 1600 * 10) < 1e-6 * 16000, "pyramid volume");
        });

        t.Test("Loft feature: lofted up from a block's top face and merged with it", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var blk = OnPlane(doc, 0, RectAt(Vec2.Zero, 60, 60));
            doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = blk.Id, Depth = "20" });
            var s0 = OnPlane(doc, 20, RectAt(Vec2.Zero, 40, 40));
            var s1 = OnPlane(doc, 50, CircleAt(Vec2.Zero, 10));
            var s2 = OnPlane(doc, 70, CircleAt(Vec2.Zero, 14));
            var lf = doc.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { s0.Id, s1.Id, s2.Id }, StartTangency = SpryCad.Core.Model.LoftTangency.Normal });
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[lf.Id].Ok, r.Status[lf.Id].Error ?? "");
            t.Eq(r.State.Bodies.Count, 1);
            var body = r.State.Bodies[0].Solid;
            t.True(body.Native != null, "still exact");
            t.Eq(body.Topology.OpenHalfEdges, 0);
            var loft = KernelTests.Vex(new SpryCad.Core.Regen.Regenerator().Rebuild(doc).State.Tools[lf.Id].Tool);
            t.True(Math.Abs(KernelTests.Vex(body) - (60 * 60 * 20 + loft)) < 1e-6 * KernelTests.Vex(body), "the block plus the loft");
        });

        t.Test("Loft feature: a cut loft pockets a block exactly; a guide sketch bows a side out", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var blk = OnPlane(doc, 0, RectAt(Vec2.Zero, 100, 100));
            var ex = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = blk.Id, Depth = "30" });
            var top = OnPlane(doc, 30, RectAt(Vec2.Zero, 20, 20));
            var bot = OnPlane(doc, 10, RectAt(Vec2.Zero, 10, 10));
            var cut = doc.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { top.Id, bot.Id }, IsCut = true });
            t.Eq(cut.Name, "Cut-Loft1");
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var removed = 100 * 100 * 30 - KernelTests.Vex(r.State.Bodies[0].Solid);
            var frustum = 20.0 / 3 * (400 + 100 + 200);
            t.True(Math.Abs(removed - frustum) < 1e-6 * frustum, $"removed {removed} vs {frustum}");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // a 40 × 40 square at y 0 and y 40 (corner (40, 0) in Top's sketch is world (40, y, 0)); a guide on Front
            // from (40, 0) to (40, 40) through (46, 20)
            var doc2 = new SpryCad.Core.Model.CadDocument();
            var q0 = OnPlane(doc2, 0, SpryCad.Tests.CoreTests.Rect(40, 40));
            var q1 = OnPlane(doc2, 40, SpryCad.Tests.CoreTests.Rect(40, 40));
            var gsk = new SpryCad.Core.Sketching.Sketch();
            double cx = (46.0 * 46 - 40 * 40 - 400) / (2 * 6);
            var gc = gsk.AddPoint(new Vec2(cx, 20)); var g0 = gsk.AddPoint(new Vec2(40, 0)); var g1 = gsk.AddPoint(new Vec2(40, 40));
            gsk.Add(new SpryCad.Core.Sketching.SkArc { Center = gc.Id, Start = g0.Id, End = g1.Id });
            var gf = doc2.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = gsk });
            var lg = doc2.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { q0.Id, q1.Id }, GuideSketchIds = { gf.Id } });
            var rg = new SpryCad.Core.Regen.Regenerator().Rebuild(doc2);
            t.True(rg.Status[lg.Id].Ok, rg.Status[lg.Id].Error ?? "");
            t.Eq(rg.Status[gf.Id].Warnings.Count, 0); // a guide is open by design
            var vg = KernelTests.Vex(rg.State.Bodies[0].Solid);
            t.True(vg > 1600 * 40 + 100, $"bows out: {vg}");
            t.True(rg.State.Bodies[0].Solid.Bounds.Max.X > 45.99, $"reaches the guide: {rg.State.Bodies[0].Solid.Bounds.Max.X}");
            t.Eq(rg.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
        });
    }
}
