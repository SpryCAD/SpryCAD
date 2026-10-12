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

        t.Test("Loft: closed, a ring of sections round an axis (no end faces, smooth all round)", () =>
        {
            // sections in planes through the Y axis, at angle a round it, the ring's radius R
            const double R = 40;
            NLoft.Section Ring(double a, ProfileRegion r)
            {
                var x = new Vec3(Math.Cos(a), 0, Math.Sin(a));
                return new NLoft.Section(new Frame(x * R, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY)), new[] { r });
            }
            var ring = NLoft.Loft(Enumerable.Range(0, 8).Select(i => Ring(i * Math.PI / 4, Circle(6))).ToArray(), null, new NLoft.Options(Closed: true), "R");
            var v = Volume(t, ring, "ring of circles");
            var torus = 2 * Math.PI * Math.PI * R * 36;
            t.True(Math.Abs(v - torus) < 0.01 * torus, $"close to the torus: {v} vs {torus}");
            t.True(ring.Faces.All(f => !f.Tag.Name.EndsWith(".Start") && !f.Tag.Name.EndsWith(".End")), "no end faces");
            t.Eq(ring.Lumps().Count, 1);
            // smooth across the seam (section 1, where the loft closes): the faces either side have one normal there
            var p = new Vec3(R + 6, 0, 0);
            var near = ring.Faces.Where(f => f.Surface.Kind != SurfaceKind.Plane && Vec3.Distance(f.Surface.Project(p), p) < 1e-6).ToList();
            t.True(near.Count >= 2, $"faces meet at the seam: {near.Count}");
            var ns = near.Select(f => f.Surface.Normal(f.Surface.Project(p)) * (f.Sense ? 1 : -1)).ToList();
            t.True(ns.All(n => Vec3.Dot(n, ns[0]) > 1 - 1e-6), "tangent across the seam");
            // squares and circles alternating round the ring
            var mixed = NLoft.Loft(Enumerable.Range(0, 6).Select(i => Ring(i * Math.PI / 3, i % 2 == 0 ? Rect(10, 10) : Circle(6))).ToArray(), null, new NLoft.Options(Closed: true), "M");
            Volume(t, mixed, "mixed ring");
            string? err = null;
            try { NLoft.Loft(new[] { Ring(0, Circle(5)), Ring(1, Circle(5)) }, null, new NLoft.Options(Closed: true), "X"); } catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("three sections"), "explained: " + err);
        });

        t.Test("Loft: along a centerline (a quarter bend: the torus, to the tolerance; an S bend square to circle to square)", () =>
        {
            // two circles r 6 square to an arc of radius 40 about (40, 0, 0) in XZ, a quarter turn apart
            Frame Tilt(double a)
            {
                var o = new Vec3(40 - 40 * Math.Cos(a), 0, 40 * Math.Sin(a));
                var x = new Vec3(Math.Cos(a), 0, -Math.Sin(a));
                return new Frame(o, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY));
            }
            var arc = new NSweep.Seg(Vec3.Zero, new Vec3(40, 0, 40), new Vec3(40, 0, 0), new Vec3(0, 1, 0), Math.PI / 2);
            t.True(Vec3.Distance(arc.B, new Vec3(40, 0, 40)) < 1e-9 && Vec3.Dot(arc.StartTangent, Vec3.UnitZ) > 1 - 1e-12, "the arc leaves along +Z");
            var opt = new NLoft.Options { Centerline = new[] { arc } };
            var b = NLoft.Loft(new[] { new NLoft.Section(Tilt(0), new[] { Circle(6) }), new NLoft.Section(Tilt(Math.PI / 2), new[] { Circle(6) }) }, null, opt, "C");
            var v = Volume(t, b, "quarter bend");
            var torus = Math.PI * 36 * 40 * Math.PI / 2;
            t.True(Math.Abs(v - torus) < 1e-6 * torus, $"the torus's quarter: {v} vs {torus}");
            // the side stays on the torus all along
            double off = 0;
            foreach (var f in b.Faces.Where(f => f.Surface.Kind == SurfaceKind.BSpline))
            {
                var nb = f.Surface.Nurbs!;
                for (int i = 0; i <= 10; i++)
                    for (int j = 0; j <= 20; j++)
                    {
                        var q = nb.Eval(nb.U0 + (nb.U1 - nb.U0) * i / 10, nb.V0 + (nb.V1 - nb.V0) * j / 20);
                        var d = q - new Vec3(40, 0, 0);
                        var rho = Math.Sqrt(d.X * d.X + d.Z * d.Z);
                        off = Math.Max(off, Math.Abs(Math.Sqrt((rho - 40) * (rho - 40) + d.Y * d.Y) - 6));
                    }
            }
            t.True(off < 1e-5, $"on the torus: {off:E2}");
            // an S: a quarter turn one way, a quarter the other, square to circle to square
            var arc2 = new NSweep.Seg(new Vec3(40, 0, 40), new Vec3(80, 0, 80), new Vec3(40, 0, 80), new Vec3(0, -1, 0), Math.PI / 2);
            t.True(Vec3.Distance(arc2.B, new Vec3(80, 0, 80)) < 1e-9, "the second arc ends at (80, 0, 80)");
            var end = new Frame(new Vec3(80, 0, 80), new Vec3(1, 0, 0) * -1, Vec3.UnitY, Vec3.Cross(new Vec3(-1, 0, 0), Vec3.UnitY));
            var s3 = NLoft.Loft(new[] { new NLoft.Section(Tilt(0), new[] { Rect(10, 10) }), new NLoft.Section(Tilt(Math.PI / 2), new[] { Circle(6) }),
                new NLoft.Section(new Frame(new Vec3(80, 0, 80), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ), new[] { Rect(10, 10) }) }, null, new NLoft.Options { Centerline = new[] { arc, arc2 } }, "S");
            Volume(t, s3, "S bend");
            string? err = null;
            try { NLoft.Loft(new[] { Sec(0, Circle(5)), Sec(20, Circle(5)) }, null, new NLoft.Options { Centerline = new[] { new NSweep.Seg(new Vec3(30, 0, 0), new Vec3(30, 0, 1)) } }, "X"); }
            catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("does not cross"), "explained: " + err);
        });

        t.Test("Loft: options together (closed with a guide, closed along a centerline, a centerline with a guide, with tangency)", () =>
        {
            const double R = 40;
            NLoft.Section Ring(double a, ProfileRegion r)
            {
                var x = new Vec3(Math.Cos(a), 0, Math.Sin(a));
                return new NLoft.Section(new Frame(x * R, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY)), new[] { r });
            }
            // a circle of radius rr in XZ about the origin, as two half turns (a closed chain), running with the sections
            List<NSweep.Seg> Round(double rr) => new()
            {
                new(new Vec3(rr, 0, 0), new Vec3(-rr, 0, 0), Vec3.Zero, new Vec3(0, -1, 0), Math.PI),
                new(new Vec3(-rr, 0, 0), new Vec3(rr, 0, 0), Vec3.Zero, new Vec3(0, -1, 0), Math.PI),
            };
            var torus = 2 * Math.PI * Math.PI * R * 36;
            var secs = Enumerable.Range(0, 6).Select(i => Ring(i * Math.PI / 3, Circle(6))).ToArray();
            // closed, a closed guide round the outside
            var g = NLoft.Loft(secs, new[] { Round(R + 6) }, new NLoft.Options(Closed: true), "G");
            var vg = Volume(t, g, "closed with a guide");
            t.True(Math.Abs(vg - torus) < 0.01 * torus, $"close to the torus: {vg} vs {torus}");
            // closed along a closed centerline: the torus to the tolerance
            var c = NLoft.Loft(secs, null, new NLoft.Options(Closed: true) { Centerline = Round(R) }, "C");
            var vc = Volume(t, c, "closed along a centerline");
            t.True(Math.Abs(vc - torus) < 1e-6 * torus, $"the torus: {vc} vs {torus}");
            // a quarter bend along its centerline with a guide round the outside, and with square ends
            Frame Tilt(double a)
            {
                var o = new Vec3(40 - 40 * Math.Cos(a), 0, 40 * Math.Sin(a));
                var x = new Vec3(Math.Cos(a), 0, -Math.Sin(a));
                return new Frame(o, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY));
            }
            var arc = new NSweep.Seg(Vec3.Zero, new Vec3(40, 0, 40), new Vec3(40, 0, 0), new Vec3(0, 1, 0), Math.PI / 2);
            var outer = new NSweep.Seg(new Vec3(-6, 0, 0), new Vec3(40, 0, 46), new Vec3(40, 0, 0), new Vec3(0, 1, 0), Math.PI / 2);
            var two = new[] { new NLoft.Section(Tilt(0), new[] { Circle(6) }), new NLoft.Section(Tilt(Math.PI / 2), new[] { Circle(6) }) };
            var quarter = Math.PI * 36 * 40 * Math.PI / 2;
            var cg = NLoft.Loft(two, new[] { new List<NSweep.Seg> { outer } }, new NLoft.Options { Centerline = new[] { arc } }, "CG");
            var vcg = Volume(t, cg, "centerline and guide");
            t.True(Math.Abs(vcg - quarter) < 1e-5 * quarter, $"the torus's quarter: {vcg} vs {quarter}");
            var ct = NLoft.Loft(two, null, new NLoft.Options(NLoft.Tangency.Normal, 1, NLoft.Tangency.Normal, 1) { Centerline = new[] { arc } }, "CT");
            Volume(t, ct, "centerline and tangency");
            var side = ct.Faces.First(f => f.Surface.Kind == SurfaceKind.BSpline).Surface.Nurbs!;
            var dv = side.Derivs(0.5 * (side.U0 + side.U1), side.V0).sv;
            t.True(Math.Abs(dv.Normalized.Z) > 1 - 1e-9, $"leaves square to the first section: {dv}");
            // an open guide on a closed loft is explained
            string? err = null;
            try { NLoft.Loft(secs, new[] { new List<NSweep.Seg> { outer } }, new NLoft.Options(Closed: true), "X"); } catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("must be closed"), "explained: " + err);
        });

        t.Test("Loft: a loft forced to twist through itself is refused", () =>
        {
            // a straight guide from one corner of the lower square to the opposite corner of the upper one: a half turn
            var guide = new List<NSweep.Seg> { new(new Vec3(10, 10, 0), new Vec3(-10, -10, 30)) };
            string? err = null;
            try { NLoft.Loft(new[] { Sec(0, Rect(20, 20)), Sec(30, Rect(20, 20)) }, new[] { guide }, null, "X"); } catch (ArgumentException ex) { err = ex.Message; }
            t.True(err != null && err.Contains("passes through itself"), "explained: " + err);
            // a quarter turn in three steps is fine
            var ok = NLoft.Loft(Enumerable.Range(0, 4).Select(i => new NLoft.Section(At(i * 10), new[] { new ProfileRegion(ProfileLoop.Polygon(Enumerable.Range(0, 4).Select(j => Vec2.FromAngle(i * Math.PI / 6 + j * Math.PI / 2, 14)), "N")) })).ToArray(), null, null, "X");
            Volume(t, ok, "quarter turn");
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

        t.Test("Loft feature: a square along an S centerline sketch (the swept volume); saved and loaded", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var a = OnPlane(doc, 0, RectAt(Vec2.Zero, 10, 10));
            var b = OnPlane(doc, 40, RectAt(new Vec2(20, 0), 10, 10));
            // on Front (x, y as world): up from the origin, a quarter turn right (r 10), a quarter back up, then straight to y 40
            var cl = new SpryCad.Core.Sketching.Sketch();
            var o = cl.Point(SpryCad.Core.Sketching.Sketch.OriginId)!;
            var c1 = cl.AddPoint(new Vec2(10, 0)); var p1 = cl.AddPoint(new Vec2(10, 10)); var c2 = cl.AddPoint(new Vec2(10, 20)); var p2 = cl.AddPoint(new Vec2(20, 20)); var p3 = cl.AddPoint(new Vec2(20, 40));
            cl.Add(new SpryCad.Core.Sketching.SkArc { Center = c1.Id, Start = p1.Id, End = o.Id });
            cl.Add(new SpryCad.Core.Sketching.SkArc { Center = c2.Id, Start = p1.Id, End = p2.Id });
            cl.Add(new SpryCad.Core.Sketching.SkLine { P1 = p2.Id, P2 = p3.Id });
            var cf = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = cl });
            var lf = doc.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { a.Id, b.Id }, CenterlineSketchId = cf.Id });
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[lf.Id].Ok, r.Status[lf.Id].Error ?? "");
            t.Eq(r.Status[cf.Id].Warnings.Count, 0); // a centerline is open by design
            var len = 2 * Math.PI / 2 * 10 + 20;
            var vol = KernelTests.Vex(r.State.Bodies[0].Solid);
            t.True(Math.Abs(vol - 100 * len) < 1e-5 * 100 * len, $"the swept volume: {vol} vs {100 * len}");
            var again = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            t.True(again.Features.Last() is SpryCad.Core.Model.LoftFeature l2 && l2.CenterlineSketchId == cf.Id, "centerline saved");
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
