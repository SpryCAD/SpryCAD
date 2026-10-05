using FeatherCad.Kernel;

namespace FeatherCad.Tests;

static class ExactTests
{
    /// <summary>Checks closure, edge/face pairing, and that curves and vertices lie on their exact surfaces.</summary>
    public static void Validate(TestRunner t, ExactBrep b, double curveTol = 1e-6, double splineTol = 2e-3)
    {
        t.Eq(b.Warnings.Count, 0);
        foreach (var e in b.Edges)
        {
            t.True(e.FaceA >= 0 && e.FaceB >= 0, $"edge {e.Index} has two faces");
            var sa = b.Faces[e.FaceA].Surface; var sb = b.Faces[e.FaceB].Surface;
            var tol = e.Curve is BSplineCurve ? splineTol : curveTol;
            foreach (var p in e.Curve.Sample(b.Vertices[e.V0], b.Vertices[e.V1], e.Closed, 0.01))
            {
                var d = Math.Max(sa.Distance(p), sb.Distance(p));
                t.True(d < tol, $"edge {e.Index} ({e.Curve.GetType().Name}) off surface by {d:E2} ({sa.Kind}/{sb.Kind})");
            }
        }
        foreach (var f in b.Faces) t.True(f.Loops.Count > 0, $"face {f.Name} has a boundary");
        foreach (var f in b.Faces)
            foreach (var loop in f.Loops)
            {
                for (int i = 0; i < loop.Edges.Count; i++)
                {
                    var (ei, fwd) = loop.Edges[i]; var (nj, nfwd) = loop.Edges[(i + 1) % loop.Edges.Count];
                    var e = b.Edges[ei]; var n = b.Edges[nj];
                    var end = fwd ? e.V1 : e.V0; var start = nfwd ? n.V0 : n.V1;
                    t.True(end == start, $"loop of face {f.Name} not closed");
                }
                foreach (var (ei, _) in loop.Edges)
                {
                    var e = b.Edges[ei];
                    t.True(f.Surface.Distance(b.Vertices[e.V0]) < 1e-5, $"vertex off face {f.Name} ({f.Surface.Kind}) by {f.Surface.Distance(b.Vertices[e.V0]):E2}");
                }
            }
    }

    public static void Run(TestRunner t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
        Directory.CreateDirectory(dir);
        void Step(string name, Solid s)
        {
            using var f = File.Create(Path.Combine(dir, name + ".step"));
            StepWriter.Write(f, new[] { ("Body", s) }, name);
        }

        t.Test("Exact: box", () =>
        {
            var box = Builders.Box(Vec3.Zero, new Vec3(100, 50, 20));
            var b = ExactBrep.Build(box);
            t.Eq(b.Faces.Count, 6); t.Eq(b.Edges.Count, 12); t.Eq(b.Vertices.Count, 8);
            t.True(b.Edges.All(e => e.Curve is LineCurve), "all lines");
            Validate(t, b);
            Step("exact_box", box);
        });

        t.Test("Exact: plate with hole -> cylinder + circles", () =>
        {
            var plate = Csg.Subtract(Builders.Box(Vec3.Zero, new Vec3(100, 50, 20)), Builders.Cylinder(new Vec3(50, 25, -5), Vec3.UnitZ, 10, 30, "H", Vec3.UnitX));
            var b = ExactBrep.Build(plate);
            t.Eq(b.Faces.Count, 7);
            t.Eq(b.Count(SurfaceKind.Cylinder), 1);
            var circles = b.Edges.Where(e => e.Curve is CircleCurve).ToList();
            t.Eq(circles.Count, 2);
            t.Near(((CircleCurve)circles[0].Curve).Radius, 10, 1e-9);
            t.True(!b.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder).SameSense, "hole wall faces the axis");
            Validate(t, b);
            Step("exact_plate_hole", plate);
        });

        t.Test("Exact: boss on plate", () =>
        {
            var s = Csg.Union(Builders.Box(Vec3.Zero, new Vec3(60, 60, 10)), Builders.Cylinder(new Vec3(30, 30, 10), Vec3.UnitZ, 12, 15, "C", Vec3.UnitX));
            var b = ExactBrep.Build(s);
            t.Eq(b.Count(SurfaceKind.Cylinder), 1);
            t.True(b.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder).SameSense, "boss faces outward");
            Validate(t, b);
            Step("exact_boss", s);
        });

        t.Test("Exact: crossing cylinders -> B-spline intersection", () =>
        {
            var a = Builders.Cylinder(new Vec3(-40, 0, 0), Vec3.UnitX, 10, 80, "A", Vec3.UnitY);
            var c = Builders.Cylinder(new Vec3(0, -40, 0), Vec3.UnitY, 6, 80, "B", Vec3.UnitZ);
            var s = Csg.Union(a, c);
            t.Eq(s.Topology.OpenHalfEdges, 0);
            var b = ExactBrep.Build(s);
            t.Eq(b.Count(SurfaceKind.Cylinder), 3); // the small pipe exits on both sides: two faces
            t.True(b.Edges.Count(e => e.Curve is BSplineCurve) >= 2, "intersection curves are splines");
            Validate(t, b);
            Step("exact_tee", s);
        });

        t.Test("Exact: hole rim chamfer (cone) and fillet (torus)", () =>
        {
            var plate = Csg.Subtract(Builders.Box(Vec3.Zero, new Vec3(60, 60, 20)), Builders.Cylinder(new Vec3(30, 30, -5), Vec3.UnitZ, 10, 30, "H", Vec3.UnitX));
            var rims = plate.Topology.Edges.Where(e => e.Closed).ToList();
            var errs = new List<string>();
            var ch = EdgeBlend.Apply(plate, new[] { rims[0] }, 2, false, "C", errs);
            var fi = EdgeBlend.Apply(ch, new[] { ch.Topology.Edges.First(e => e.Closed && e.Touches("Box.Start", "H.S.C") || e.Closed && e.Touches("Box.End", "H.S.C")) }, 2, true, "F", errs);
            t.Eq(errs.Count, 0);
            var b = ExactBrep.Build(fi);
            t.Eq(b.Count(SurfaceKind.Cone), 1);
            t.Eq(b.Count(SurfaceKind.Torus), 1);
            Validate(t, b);
            Step("exact_rims", fi);
        });

        t.Test("Exact: revolved sphere", () =>
        {
            var loop = new ProfileLoop();
            loop.AddArc(Vec2.Zero, 10, -Math.PI / 2, Math.PI / 2, true, "Arc");
            loop.Verts.Add(new ProfileVertex(new Vec2(0, 10), "Axis"));
            var s = Builders.Revolve(new[] { new ProfileRegion(loop) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "R");
            if (s.Volume() < 0) s = s.Flipped();
            var b = ExactBrep.Build(s);
            t.Eq(b.Count(SurfaceKind.Sphere), 2); // split into hemispheres at an exact equator
            Validate(t, b);
            Step("exact_sphere", s);
        });

        t.Test("Fillet/chamfer around a rounded corner (partial arc edge)", () =>
        {
            foreach (var fillet in new[] { true, false })
            {
                var box = Builders.Box(Vec3.Zero, new Vec3(60, 40, 20));
                var errs = new List<string>();
                var corner = box.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 60) < 1e-9 && Math.Abs(e.Start.Y - 40) < 1e-9);
                Csg.NativeFallback = 0;
                var rounded = EdgeBlend.Apply(box, new[] { corner }, 8, true, "R", errs);
                Console.WriteLine($"      after corner fillet: native fallbacks {Csg.NativeFallback} {Csg.LastFallbackReason}");
                t.Eq(errs.Count, 0);
                var topEdges = rounded.Topology.Edges.Where(e => e.FaceA == "Box.End" || e.FaceB == "Box.End").ToList();
                t.Eq(topEdges.Count, 5); // 4 lines + the corner arc
                var log = new List<string>();
                if (fillet) FeatherCad.Kernel.Native.NBoolean.Trace = m => log.Add(m);
                var r = EdgeBlend.Apply(rounded, topEdges, 2, fillet, "T", errs);
                FeatherCad.Kernel.Native.NBoolean.Trace = null;
                // print the trace of the failing operation (from its 'edge splits' line to the failure)
                var lastStart = log.FindLastIndex(l => l.Contains("edge splits"));
                if (lastStart >= 0 && Csg.NativeFallback > 0) foreach (var l in log.Skip(lastStart)) Console.WriteLine("        " + l);
                Console.WriteLine($"      after top blend: native fallbacks {Csg.NativeFallback} {Csg.LastFallbackReason}");
                t.Eq(errs.Count, 0);
                t.Eq(r.Topology.OpenHalfEdges, 0);
                var removed = rounded.Volume() - r.Volume();
                var perimeter = 2 * (60 + 40) - 2 * 8 + 2 * Math.PI * 8 / 4;
                var perLength = fillet ? 4 - Math.PI : 2.0;   // cross-section area removed per unit length
                t.True(removed > perimeter * perLength * 0.85 && removed < perimeter * perLength * 1.15, $"removed {removed:0.##} vs ~{perimeter * perLength:0.##}");
                var b = ExactBrep.Build(r);
                Validate(t, b);
                t.Eq(b.Count(fillet ? SurfaceKind.Torus : SurfaceKind.Cone), 1);
                Step(fillet ? "corner_fillet" : "corner_chamfer", r);
            }
        });
    }
}
