using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

static class NativeTests
{
    public static ProfileRegion RectRegion(double x0, double y0, double x1, double y1) =>
        new(ProfileLoop.Polygon(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) }, "E"));

    public static NBody Box(Vec3 min, Vec3 max, string prefix = "Box")
    {
        var frame = new Frame(new Vec3(0, 0, min.Z), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        return NBuild.Extrude(new[] { RectRegion(min.X, min.Y, max.X, max.Y) }, frame, 0, max.Z - min.Z, prefix);
    }

    public static NBody Cyl(Vec3 baseCenter, Vec3 axis, double r, double h, string prefix)
    {
        var n = axis.Normalized; var x = n.AnyPerpendicular();
        if (Math.Abs(n.Z) > 0.9) x = Vec3.UnitX;
        var frame = new Frame(baseCenter, x, Vec3.Cross(n, x), n);
        return NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, r, "C")) }, frame, 0, h, prefix);
    }

    public static void Check(TestRunner t, NBody b, double? volume = null, double volTol = 0.5)
    {
        t.Eq(b.Validate(), null);
        var s = NTess.ToSolid(b, 0.005);
        t.Eq(s.Topology.OpenHalfEdges, 0);
        if (volume is { } v) t.Near(s.Volume(), v, volTol);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Native: tessellation speed", () =>
        {
            var cyl = Cyl(new Vec3(10, 10, 0), Vec3.UnitZ, 10, 20, "C");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = NTess.ToSolid(cyl, 0.005);
            var tTess = sw.ElapsedMilliseconds; sw.Restart();
            var open = s.Topology.OpenHalfEdges;
            var tTopo = sw.ElapsedMilliseconds; sw.Restart();
            var v = cyl.Validate();
            Console.WriteLine($"      tess {tTess} ms ({s.Faces.Count} facets), topology {tTopo} ms, validate {sw.ElapsedMilliseconds} ms");
        });

        t.Test("Native: extruded box and cylinder", () =>
        {
            var box = Box(Vec3.Zero, new Vec3(100, 50, 20));
            t.Eq(box.Faces.Count, 6);
            Check(t, box, 100000, 1e-6);
            var cyl = Cyl(new Vec3(10, 10, 0), Vec3.UnitZ, 10, 20, "C");
            t.Eq(cyl.Faces.Count, 4); // seam-split side + 2 caps
            Check(t, cyl, Math.PI * 100 * 20, 6.0);
            // region with a round hole
            var outer = ProfileLoop.Polygon(new[] { new Vec2(0, 0), new Vec2(60, 0), new Vec2(60, 40), new Vec2(0, 40) }, "E");
            var hole = ProfileLoop.Circle(new Vec2(30, 20), 8, "H");
            var plate = NBuild.Extrude(new[] { new ProfileRegion(outer, new[] { hole }) }, Frame.Front, 0, 10, "P");
            Check(t, plate, 60 * 40 * 10 - Math.PI * 64 * 10, 6.0);
            // transformed copy stays valid
            Check(t, plate.Transformed(Transform.Rotation(Vec3.Zero, new Vec3(1, 2, 3), 0.7)), 60 * 40 * 10 - Math.PI * 64 * 10, 6.0);
            Check(t, plate.Transformed(Transform.Mirror(Plane.FromPointNormal(Vec3.Zero, Vec3.UnitX))), 60 * 40 * 10 - Math.PI * 64 * 10, 6.0);
        });
    }
}

static class NativeBoolTests
{
    static double Vol(NBody b) => NTess.ToSolid(b, 0.002).Volume();

    public static void Run(TestRunner t)
    {
        t.Test("Native bool: local booleans give the whole-body result", () =>
        {
            // a plate with a hole pattern, then cuts and bosses touching few faces: faces far from each tool are
            // skipped by every stage, which must not change the result
            NBody Plate()
            {
                var plate = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(70, 70, 8), "P");
                var holes = new NBody();
                for (int i = 0; i < 6; i++)
                    for (int j = 0; j < 6; j++)
                        holes.Faces.AddRange(NativeTests.Cyl(new Vec3(10 + 10 * i, 10 + 10 * j, -1), Vec3.UnitZ, 2.5, 10, $"G{i}{j}").Faces);
                holes.Link();
                return NBoolean.Run(plate, holes, BoolOp.Subtract);
            }
            var tools = new (NBody tool, BoolOp op)[]
            {
                (NativeTests.Cyl(new Vec3(15, 15, 4), Vec3.UnitZ, 1.5, 10, "T1"), BoolOp.Subtract),             // blind hole between holes
                (NativeTests.Cyl(new Vec3(35, 35, 8), Vec3.UnitZ, 4, 6, "T2"), BoolOp.Union),                   // boss on the top face
                (NativeTests.Box(new Vec3(28, -1, 3), new Vec3(42, 12, 9), "T3"), BoolOp.Subtract),             // slot through a hole and the edge
                (NativeTests.Cyl(new Vec3(-1, 50, 4), Vec3.UnitX, 1.5, 6, "T4"), BoolOp.Subtract),              // hole in a side wall
                (NativeTests.Box(new Vec3(60, 60, 8), new Vec3(75, 75, 12), "T5"), BoolOp.Union),               // block over a corner, overhanging
            };
            var saved = NBoolean.Local;
            try
            {
                var bodies = new List<NBody>();
                foreach (var local in new[] { false, true })
                {
                    NBoolean.Local = local;
                    var body = Plate();
                    foreach (var (tool, op) in tools) body = NBoolean.Run(body, tool, op);
                    t.True(body.Validate() == null, body.Validate() ?? "");
                    bodies.Add(body);
                }
                t.Eq(bodies[1].Faces.Count, bodies[0].Faces.Count);
                t.Eq(bodies[1].Edges.Count(), bodies[0].Edges.Count());
                t.Near(Vol(bodies[1]), Vol(bodies[0]), 1e-6 * Vol(bodies[0]));
            }
            finally { NBoolean.Local = saved; }
        });

        t.Test("Native bool: fillets crossing the seam of a revolved face", () =>
        {
            // spoke patterns at angles that put a spoke and its fillets across the rim's and the hub's revolve seams:
            // the fillet contact lines cross the seam edges inside the surface (an edge/surface test only grazes)
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var file = Path.Combine(dir!, "samples", "demos", "spoked-handwheel.fcprt");
            foreach (var angle in new[] { "236", "270" })
            {
                var doc = CadDocument.Load(file);
                // grips are cut before the spokes: an off-grid spoke may land on a scallop, which is another test
                foreach (var gf in doc.Features.Where(f => f.Name is "Grip" or "Grip edges" or "Grips")) gf.Suppressed = true;
                var spokes = doc.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes");
                spokes.Angle = angle;
                var r = new Regenerator().Rebuild(doc);
                foreach (var f in doc.Features.Where(f => !f.Suppressed)) t.True(r.Status[f.Id].Ok, $"angle {angle}: {f.Name}: {r.Status[f.Id].Error}");
            }
            // past a full turn the last instance would land on the seed
            var over = CadDocument.Load(file);
            over.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes").Angle = "360.72";
            var ro = new Regenerator().Rebuild(over);
            var st = ro.Status[over.Features.First(f => f.Name == "Spokes").Id];
            t.True(!st.Ok && st.Error!.Contains("360"), st.Error ?? "accepted");
        });

        t.Test("Native blend: a groove's rim fillet running onto another fillet", () =>
        {
            // a grip groove moved onto a spoke's fillet: its rim edges run from the rim onto the spoke fillet (and the
            // spoke) across tangent face boundaries; the ball rolls from face to face in one tool. 3 degrees: the edge
            // dips into the fillet while the ball stays on the rim (an island); 9: the ball crosses onto the fillet
            // after the edge has passed further vertices
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            // the earlier handwheel, grips cut after the spokes (and through them)
            var file = Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "spoked-handwheel-grips-last.fcprt");
            foreach (var a in new[] { "3", "9" })
            {
                var doc = CadDocument.Load(file);
                doc.Equations.First(e => e.Name == "GripA").Expr = a;
                foreach (var f in doc.Features.Where(f => f.Name is "Grips" or "Keyway")) f.Suppressed = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = new Regenerator().Rebuild(doc);
                foreach (var f in doc.Features.Where(f => !f.Suppressed))
                    t.True(r.Status[f.Id].Ok && r.Status[f.Id].Warnings.Count == 0, $"GripA {a}: {f.Name}: {r.Status[f.Id].Error} {string.Join(";", r.Status[f.Id].Warnings)}");
                Console.WriteLine($"      GripA {a}: {sw.ElapsedMilliseconds} ms");
            }
        });

        t.Test("Pattern: instances to skip", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var file = Path.Combine(dir!, "samples", "demos", "spoked-handwheel.fcprt");
            double Vol(CadDocument d, out RegenResult r)
            {
                r = new Regenerator().Rebuild(d);
                foreach (var f in d.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
                return r.State.Bodies[0].Solid.Volume();
            }
            var all = Vol(CadDocument.Load(file), out var r0);
            var doc = CadDocument.Load(file);
            doc.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes").Skip = new() { 3 };
            var skipped = Vol(doc, out var r1);
            var names = r1.State.Bodies[0].Solid.Native!.Faces.Select(f => f.Tag.Name).ToHashSet();
            var spokes = doc.Features.First(f => f.Name == "Spokes").Id;
            // instance 3 is gone, the others keep their names
            t.True(!names.Any(n => n.StartsWith($"{spokes}.2.")), "instance 3 left out");
            t.True(names.Any(n => n.StartsWith($"{spokes}.1.")) && names.Any(n => n.StartsWith($"{spokes}.3.")), "instances 2 and 4 named as before");
            // one spoke (with its fillets) less: about a fifth of the spokes' material
            t.True(skipped < all - 1000, $"volume {skipped} vs {all}");
        });

        t.Test("Rebuild: progress, cancellation, pattern instance positions", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var doc = CadDocument.Load(Path.Combine(dir!, "samples", "demos", "spoked-handwheel.fcprt"));
            // progress: every feature, and the instances inside a pattern
            var seen = new List<RegenProgress>();
            var regen = new Regenerator();
            var full = regen.Rebuild(doc, null, default, p => { lock (seen) seen.Add(p); });
            t.Eq(seen.Select(p => p.FeatureId).Distinct().Count(), doc.Features.Count);
            t.True(seen.Any(p => p.FeatureName == "Grips" && p.Detail != null && p.Detail.Contains("instance")), "pattern instances reported");
            // cancellation: stops between features; the completed ones stay cached and the next rebuild finishes the job
            var r2 = new Regenerator();
            var cts = new CancellationTokenSource();
            bool threw = false;
            try { r2.Rebuild(doc, null, cts.Token, p => { if (p.FeatureName == "Spokes") cts.Cancel(); }); }
            catch (OperationCanceledException) { threw = true; }
            t.True(threw, "cancelled");
            var after = r2.Rebuild(doc);
            t.True(after.Rebuilt < doc.Features.Count, $"features before the cancel come from the cache ({after.Rebuilt} rebuilt)");
            t.Near(after.State.Bodies[0].Solid.Volume(), full.State.Bodies[0].Solid.Volume(), 1e-6 * full.State.Bodies[0].Solid.Volume());
            // instance positions: the seed's tool centre moved by each transform (as the instance markers place them)
            int gi = doc.Features.FindIndex(f => f.Name == "Grips");
            var grips = (CircularPatternFeature)doc.Features[gi];
            var before = full.States[gi - 1];
            var lookup = new Func<string, double?>(n => full.EquationValues.TryGetValue(n, out var v) ? v : null);
            var ts = Regenerator.PatternTransforms(grips, before, lookup)!;
            t.Eq(ts.Count, (int)Math.Round(lookup("NS")!.Value * lookup("NG")!.Value) - 1);
            var c = before.Tools[grips.Seeds[0]].Tool.Bounds.Center;
            foreach (var tr in ts) t.Near(new Vec3(tr.Point(c).X, 0, tr.Point(c).Z).Length, new Vec3(c.X, 0, c.Z).Length, 1e-9);
        });

        t.Test("Pattern: instance blends solved in parallel give the same result every time", () =>
        {
            // spokes off the grip grid: several instances' spoke fillets are solved again (in parallel, on copies of
            // the body); the outcome must not depend on scheduling
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var file = Path.Combine(dir!, "samples", "demos", "spoked-handwheel.fcprt");
            string? first = null;
            for (int run = 0; run < 3; run++)
            {
                var doc = CadDocument.Load(file);
                doc.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes").Angle = "150";
                Csg.ClearCache();
                var r = new Regenerator().Rebuild(doc);
                var body = r.State.Bodies[0].Solid;
                var sig = $"{body.Volume():0.###} {body.Native!.Faces.Count} | " + string.Join(" | ", doc.Features.SelectMany(f => r.Status[f.Id].Warnings));
                t.True(doc.Features.All(f => r.Status[f.Id].Ok), "rebuilds");
                if (first == null) first = sig; else t.Eq(sig, first);
            }
        });

        t.Test("Native bool: overlapping boxes", () =>
        {
            var a = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), "A");
            var b = NativeTests.Box(new Vec3(5, 5, 5), new Vec3(15, 15, 15), "B");
            var u = NBoolean.Run(a, b, BoolOp.Union); NativeTests.Check(t, u, 1875, 1e-6);
            var i = NBoolean.Run(a, b, BoolOp.Intersect); NativeTests.Check(t, i, 125, 1e-6);
            var s = NBoolean.Run(a, b, BoolOp.Subtract); NativeTests.Check(t, s, 875, 1e-6);
            t.Eq(i.Faces.Count, 6);
        });

        t.Test("Native bool: stacked and flush boxes (coincident faces)", () =>
        {
            var a = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), "A");
            var b = NativeTests.Box(new Vec3(0, 0, 10), new Vec3(10, 10, 20), "B");
            var u = NBoolean.Run(a, b, BoolOp.Union);
            NativeTests.Check(t, u, 2000, 1e-6);
            t.Eq(u.Faces.Count, 6);
            var c = NativeTests.Box(new Vec3(0, 5, 10), new Vec3(10, 15, 15), "C"); // flush side, overhanging
            var big = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 10), "Big");
            var u2 = NBoolean.Run(big, c, BoolOp.Union);
            NativeTests.Check(t, u2, 4000 + 500, 1e-6);
        });

        t.Test("Native bool: plate minus cylinder (hole)", () =>
        {
            var plate = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20), "P");
            var cyl = NativeTests.Cyl(new Vec3(50, 25, -5), Vec3.UnitZ, 10, 30, "H");
            NBody r;
            try { r = NBoolean.Run(plate, cyl, BoolOp.Subtract); } finally { NBoolean.Trace = null; }
            NativeTests.Check(t, r, 100000 - Math.PI * 100 * 20, 8.0);
            t.Eq(r.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cylinder), 2); // seam-split hole wall
        });

        t.Test("Native bool: boss and oblique cut (ellipse)", () =>
        {
            var plate = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(60, 60, 10), "P");
            var boss = NativeTests.Cyl(new Vec3(30, 30, 5), Vec3.UnitZ, 12, 20, "C");
            var u = NBoolean.Run(plate, boss, BoolOp.Union);
            NativeTests.Check(t, u, 36000 + Math.PI * 144 * 15, 8.0);
            // tilted cylinder through the plate: elliptical section edges on both planar faces
            var tilt = NativeTests.Cyl(new Vec3(30, 0, -30), new Vec3(0, 1, 1), 6, 100, "T");
            var r = NBoolean.Run(plate, tilt, BoolOp.Subtract);
            t.Eq(r.Validate(), null);
            t.True(r.Edges.Any(e => e.Curve is NEllipse), "elliptical edges");
            NativeTests.Check(t, r, 36000 - Math.PI * 36 * 10 * Math.Sqrt(2), 8.0);
        });

        t.Test("Native bool: crossing pipes (traced intersection)", () =>
        {
            var a = NativeTests.Cyl(new Vec3(-40, 0, 0), Vec3.UnitX, 10, 80, "A");
            var b = NativeTests.Cyl(new Vec3(0, -40, 0), Vec3.UnitY, 6, 80, "B");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var u = NBoolean.Run(a, b, BoolOp.Union);
            NBoolean.Trace = null;
            Console.WriteLine($"        union {sw.ElapsedMilliseconds} ms");
            t.Eq(u.Validate(), null);
            t.True(u.Edges.Any(e => e.Curve is NSpline), "spline section edges");
            // exact volume of the union: V(A) + V(B) - V(A∩B); the intersection volume of r=6 inside r=10 crossing
            sw.Restart();
            var inter = NBoolean.Run(a, b, BoolOp.Intersect);
            Console.WriteLine($"        intersect {sw.ElapsedMilliseconds} ms"); sw.Restart();
            var ts = NTess.ToSolid(inter, 0.002);
            Console.WriteLine($"        tess {sw.ElapsedMilliseconds} ms, {ts.Faces.Count} facets"); sw.Restart();
            var vi = ts.Volume();
            Console.WriteLine($"        volume {sw.ElapsedMilliseconds} ms"); sw.Restart();
            var us = NTess.ToSolid(u, 0.005); var open = us.Topology.OpenHalfEdges;
            Console.WriteLine($"        union tess+topo {sw.ElapsedMilliseconds} ms, {us.Faces.Count} facets, open {open}");
            foreach (var f in u.Faces)
            {
                var one = new NBody(); one.Faces.Add(f);
                var cnt = NTess.ToSolid(one, 0.005).Faces.Count;
                var dom = NDomain.Of(f, 0.005);
                Console.WriteLine($"          {f.Tag.Name} {f.Surface.Kind} facets={cnt} boundaryPts={dom.Loops.Sum(l => l.Length)} edges={string.Join(",", f.Coedges.Select(c => c.Edge.Curve.GetType().Name[1..] + c.Edge.Samples(0.005).Count))}");
            }
            NativeTests.Check(t, u, Math.PI * 100 * 80 + Math.PI * 36 * 80 - vi, 30.0);
        });
    }
}

static class NativeRevolveTests
{
    static ProfileRegion Poly(params (double x, double y)[] p) => new(ProfileLoop.Polygon(p.Select(q => new Vec2(q.x, q.y)), "L"));

    public static NBody Sphere(double r, string prefix = "S")
    {
        var loop = new ProfileLoop();
        loop.AddArc(Vec2.Zero, r, -Math.PI / 2, Math.PI / 2, true, "Arc");
        loop.Verts.Add(new ProfileVertex(new Vec2(0, r), "Axis"));
        return NBuild.Revolve(new[] { new ProfileRegion(loop) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, prefix);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Native revolve: cylinder, cone, sphere, torus, partial", () =>
        {
            var cyl = NBuild.Revolve(new[] { Poly((0, 0), (10, 0), (10, 20), (0, 20)) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "R");
            NativeTests.Check(t, cyl, Math.PI * 100 * 20, 6);
            var cone = NBuild.Revolve(new[] { Poly((0, 0), (10, 0), (0, 15)) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "K");
            NativeTests.Check(t, cone, Math.PI * 100 * 15 / 3, 6);
            t.True(cone.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cone), "cone face");
            var sph = Sphere(10);
            NativeTests.Check(t, sph, 4.0 / 3 * Math.PI * 1000, 8);
            t.Eq(sph.Faces.Count, 2);
            var ring = NBuild.Revolve(new[] { new ProfileRegion(ProfileLoop.Circle(new Vec2(20, 0), 5, "T")) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "T");
            NativeTests.Check(t, ring, 2 * Math.PI * 20 * Math.PI * 25, 10);
            var quarter = NBuild.Revolve(new[] { Poly((5, 0), (10, 0), (10, 20), (5, 20)) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), Math.PI / 2, "Q");
            NativeTests.Check(t, quarter, Math.PI * (100 - 25) * 20 / 4, 3);
            var big = NBuild.Revolve(new[] { Poly((5, 0), (10, 0), (10, 20), (5, 20)) }, Frame.Front, Vec2.Zero, new Vec2(0, -1), 1.5 * Math.PI, "Q3");
            NativeTests.Check(t, big, Math.PI * (100 - 25) * 20 * 0.75, 6);
        });

        t.Test("Native bool: tool axis lying in a face (degenerate)", () =>
        {
            var plate = NativeTests.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "P");
            var prof = Poly((0, 0.001), (6.5, 0.001), (3.3, -3.2), (3.3, -20), (0, -20));
            var tool = NBuild.Revolve(new[] { prof }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "H");
            try { var r = NBoolean.Run(plate, tool, BoolOp.Subtract); NativeTests.Check(t, r, null); }
            finally { NBoolean.Trace = null; }
        });

        t.Test("Native bool: countersunk hole, sphere cut, torus union", () =>
        {
            var plate = NativeTests.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "P");
            // countersink drill profile revolved about -Z through the origin: (r, depth)
            // Frame.Top: sketch y = world -Z, so positive y is depth below the top face
            var prof = Poly((0, -0.001), (6.5, -0.001), (3.3, 3.2), (3.3, 20), (0, 20));
            var tool = NBuild.Revolve(new[] { prof }, Frame.Top, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "H");
            NativeTests.Check(t, tool, null);
            NBody r;
            try { r = NBoolean.Run(plate, tool, BoolOp.Subtract); } finally { NBoolean.Trace = null; }
            t.Eq(r.Validate(), null);
            t.True(r.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cone), "countersink cone");
            NativeTests.Check(t, r, null);
        });

        t.Test("Native bool: sphere dimple (seams in the cut plane)", () =>
        {
            var plate = NativeTests.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "P");
            var ball = Sphere(8, "B").Transformed(Transform.Translation(new Vec3(10, 10, 0)));
            NBody dimple;
            try { dimple = NBoolean.Run(plate, ball, BoolOp.Subtract); } finally { NBoolean.Trace = null; }
            NativeTests.Check(t, dimple, 36000 - 2.0 / 3 * Math.PI * 512, 8);
        });

        t.Test("Native bool: torus ring union", () =>
        {
            var plate = NativeTests.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "P");
            var ring = NBuild.Revolve(new[] { new ProfileRegion(ProfileLoop.Circle(new Vec2(15, 0), 4, "T")) }, Frame.Top, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "T");
            NBody u;
            try { u = NBoolean.Run(plate, ring, BoolOp.Union); } finally { NBoolean.Trace = null; }
            t.Eq(u.Validate(), null);
            NativeTests.Check(t, u, 36000 + 2 * Math.PI * 15 * Math.PI * 16 / 2, 20);
        });
    }
}
