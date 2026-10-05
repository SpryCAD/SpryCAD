using System.Diagnostics;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

static class KernelTests
{
    static double PolyCircleArea(double r, int n) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    /// <summary>Volume of the exact geometry (fine tessellation of the native B-rep when present).</summary>
    internal static double Vex(Solid s) => s.Native != null ? FeatherCad.Kernel.Native.NTess.ToSolid(s.Native, 2e-4).Volume() : s.Volume();

    public static void Run(TestRunner t)
    {
        t.Test("Box volume & watertight", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            t.Near(b.Volume(), 100000, 1e-6);
            t.Eq(b.Topology.OpenHalfEdges, 0);
            t.Eq(b.Topology.Edges.Count, 12);
        });

        t.Test("Box minus through hole", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var n = SurfaceDef.SegmentsFor(10);
            var c = Builders.Cylinder(new Vec3(50, 25, -5), Vec3.UnitZ, 10, 30, "H");
            var r = Csg.Subtract(b, c);
            t.Near(Vex(r), 100000 - Math.PI * 100 * 20, 1.5);
            t.Eq(r.Topology.OpenHalfEdges, 0);
            // top face should be a single face with one hole
            var top = r.Faces.Where(f => f.Tag.Name == "Box.End").ToList();
            t.Eq(top.Count, 1);
            t.Eq(top[0].Loops.Count, 2);
            // edges: 12 box edges + 2 rims
            t.Eq(r.Topology.Edges.Count, 14);
        });

        t.Test("Union overlapping boxes", () =>
        {
            var a = Builders.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), "A");
            var b = Builders.Box(new Vec3(5, 5, 5), new Vec3(15, 15, 15), "B");
            var u = Csg.Union(a, b);
            t.Near(u.Volume(), 2000 - 125, 1e-6);
            t.Eq(u.Topology.OpenHalfEdges, 0);
            var i = Csg.Intersect(a, b);
            t.Near(i.Volume(), 125, 1e-6);
            var s = Csg.Subtract(a, b);
            t.Near(s.Volume(), 1000 - 125, 1e-6);
        });

        t.Test("Union stacked boxes merges coplanar faces", () =>
        {
            var a = Builders.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), "A");
            var b = Builders.Box(new Vec3(0, 0, 10), new Vec3(10, 10, 20), "B");
            var u = Csg.Union(a, b);
            t.Near(u.Volume(), 2000, 1e-6);
            t.Eq(u.Topology.OpenHalfEdges, 0);
            t.Eq(u.Faces.Count, 6);
        });

        t.Test("Boss on top face (flush coplanar side)", () =>
        {
            var a = Builders.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 10), "A");
            var b = Builders.Box(new Vec3(0, 5, 10), new Vec3(10, 15, 15), "B");
            var u = Csg.Union(a, b);
            t.Near(u.Volume(), 4000 + 500, 1e-6);
            t.Eq(u.Topology.OpenHalfEdges, 0);
        });

        t.Test("Chamfer straight edge", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var e = b.Topology.Edges.First(e => e.Touches("Box.End", "Box.S.E0"));
            var errs = new List<string>();
            var r = EdgeBlend.Apply(b, new[] { e }, 5, false, "C", errs);
            t.Eq(errs.Count, 0);
            t.Near(r.Volume(), 100000 - 12.5 * 100, 1e-4);
            t.Eq(r.Topology.OpenHalfEdges, 0);
        });

        t.Test("Fillet straight edge", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var e = b.Topology.Edges.First(e => e.Touches("Box.End", "Box.S.E0"));
            var errs = new List<string>();
            var r = EdgeBlend.Apply(b, new[] { e }, 5, true, "F", errs);
            t.Eq(errs.Count, 0);
            var removed = 100000 - Vex(r);
            t.Near(removed, (25 - Math.PI * 25 / 4) * 100, 1.0);
            t.Eq(r.Topology.OpenHalfEdges, 0);
        });

        t.Test("Chamfer all top edges (mitered corners)", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var es = b.Topology.Edges.Where(e => e.FaceA == "Box.End" || e.FaceB == "Box.End").ToList();
            t.Eq(es.Count, 4);
            var errs = new List<string>();
            var r = EdgeBlend.Apply(b, es, 2, false, "C", errs);
            t.Eq(errs.Count, 0);
            t.Eq(r.Topology.OpenHalfEdges, 0);
            // frustum: volume removed = 2*(L+W)*2 (prisms) - corner overlaps; just sanity check range
            var removed = 100000 - r.Volume();
            t.True(removed > 550 && removed < 600, $"removed {removed}");
        });

        t.Test("Hole rim chamfer (revolved tool)", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var c = Builders.Cylinder(new Vec3(50, 25, -5), Vec3.UnitZ, 10, 30, "H", Vec3.UnitX);
            var r = Csg.Subtract(b, c);
            var rim = r.Topology.Edges.First(e => e.Touches("Box.End", "H.S.C"));
            t.True(rim.Closed, "rim closed");
            var errs = new List<string>();
            var r2 = EdgeBlend.Apply(r, new[] { rim }, 2, false, "C", errs);
            t.Eq(errs.Count, 0);
            t.Eq(r2.Topology.OpenHalfEdges, 0);
            var removed = Vex(r) - Vex(r2);
            // countersink ring (Pappus): 2*pi*(R + d/3) * d^2/2
            t.Near(removed, 2 * Math.PI * (10 + 2.0 / 3) * 2, 0.1);
            var r3 = EdgeBlend.Apply(r, new[] { rim }, 2, true, "F", errs);
            t.Eq(errs.Count, 0);
            t.Eq(r3.Topology.OpenHalfEdges, 0);
        });

        t.Test("Revolve rectangle = cylinder", () =>
        {
            var loop = ProfileLoop.Polygon(new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 20), new Vec2(0, 20) }, "L");
            var s = Builders.Revolve(new[] { new ProfileRegion(loop) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "R");
            var n = SurfaceDef.SegmentsFor(10);
            t.Near(Vex(s), Math.PI * 100 * 20, 1.5);
            t.Eq(s.Topology.OpenHalfEdges, 0);
            var half = Builders.Revolve(new[] { new ProfileRegion(loop) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), Math.PI, "R");
            t.Near(Vex(half), Math.PI * 100 * 10, 1.0);
            t.Eq(half.Topology.OpenHalfEdges, 0);
            // ring (not touching axis), reversed axis direction
            var ring = ProfileLoop.Polygon(new[] { new Vec2(-5, 0), new Vec2(-5, 10), new Vec2(-10, 10), new Vec2(-10, 0) }, "Q");
            var s2 = Builders.Revolve(new[] { new ProfileRegion(ring) }, Frame.Front, Vec2.Zero, new Vec2(0, -1), 2 * Math.PI, "R");
            t.True(s2.Volume() > 0, "positive volume " + s2.Volume());
            t.Eq(s2.Topology.OpenHalfEdges, 0);
        });

        t.Test("Shell box (open top)", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var s = ShellOp.Apply(b, new HashSet<string> { "Box.End" }, 2, "Sh");
            t.Near(s.Volume(), 100000 - 96 * 46 * 18, 1e-3);
            t.Eq(s.Topology.OpenHalfEdges, 0);
        });

        t.Test("Shell variants", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 60, 20));
            var hole = Csg.Subtract(b, Builders.Cylinder(new Vec3(30, 30, -5), Vec3.UnitZ, 8, 30, "H", Vec3.UnitX));
            var e = b.Topology.Edges.First(e => e.Touches("Box.S.E1", "Box.S.E2"));
            var errs = new List<string>();
            var fil = EdgeBlend.Apply(b, new[] { e }, 5, true, "F", errs);
            var open = new HashSet<string> { "Box.Start" };
            var s1 = ShellOp.Apply(hole, open, 2, "Sh");
            Console.WriteLine($"      hole: open={s1.Topology.OpenHalfEdges}");
            var s2 = ShellOp.Apply(fil, open, 2, "Sh");
            Console.WriteLine($"      fillet: open={s2.Topology.OpenHalfEdges}");
            var s3 = ShellOp.Apply(b, open, 2, "Sh");
            Console.WriteLine($"      box: open={s3.Topology.OpenHalfEdges}");
        });

        t.Test("Many holes performance", () =>
        {
            var sw = Stopwatch.StartNew();
            Solid b = Builders.Box(new Vec3(0, 0, 0), new Vec3(200, 200, 10));
            var tools = new List<Solid>();
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                    tools.Add(Builders.Cylinder(new Vec3(15 + i * 24, 15 + j * 24, -1), Vec3.UnitZ, 5, 12, $"H{i}_{j}", Vec3.UnitX));
            var tool = Csg.UnionAll(tools);
            var msU = sw.ElapsedMilliseconds; sw.Restart();
            var r = Csg.Subtract(b, tool);
            FeatherCad.Kernel.Native.NBoolean.Trace = null;
            var ms = sw.ElapsedMilliseconds; sw.Restart();
            var tessMs = r.Faces.Count;
            Console.WriteLine($"      union {msU} ms, subtract {ms} ms (first run, includes JIT)");
            var sw2 = Stopwatch.StartNew();
            var tool2 = Csg.UnionAll(tools);
            var r2 = Csg.Subtract(b, tool2);
            Console.WriteLine($"      warm: union+subtract {sw2.ElapsedMilliseconds} ms (native ok {Csg.NativeOk}, fallbacks {Csg.NativeFallback})");
            t.Eq(r.Topology.OpenHalfEdges, 0);
            var n = SurfaceDef.SegmentsFor(5);
            t.Near(Vex(r), 200 * 200 * 10 - 64 * Math.PI * 25 * 10, 25);
            Console.WriteLine($"      64 holes: {ms} ms, {r.Faces.Count} faces");
        });

        t.Test("Export STL/STEP", () =>
        {
            var b = Builders.Box(new Vec3(0, 0, 0), new Vec3(100, 50, 20));
            var c = Builders.Cylinder(new Vec3(50, 25, -5), Vec3.UnitZ, 10, 30, "H");
            var r = Csg.Subtract(b, c);
            var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
            Directory.CreateDirectory(dir);
            using (var f = File.Create(Path.Combine(dir, "plate.stl"))) StlWriter.Write(f, new[] { r });
            using (var f = File.Create(Path.Combine(dir, "plate.step"))) StepWriter.Write(f, new[] { ("Body", r) }, "plate");
            t.True(new FileInfo(Path.Combine(dir, "plate.stl")).Length > 84, "stl size");
            var step = File.ReadAllText(Path.Combine(dir, "plate.step"));
            t.True(step.Contains("CLOSED_SHELL") && step.Contains("END-ISO-10303-21;"), "step content");
            Console.WriteLine($"      wrote {dir}");
        });

        t.Test("Triangulator with holes", () =>
        {
            var outer = new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10) };
            var h1 = new[] { new Vec2(2, 2), new Vec2(2, 4), new Vec2(4, 4), new Vec2(4, 2) };
            var h2 = new[] { new Vec2(6, 6), new Vec2(6, 8), new Vec2(8, 8), new Vec2(8, 6) };
            var idx = Triangulator.Triangulate(new[] { outer, h1, h2 });
            var all = outer.Concat(h1).Concat(h2).ToArray();
            double area = 0;
            for (int i = 0; i < idx.Count; i += 3)
                area += Vec2.Cross(all[idx[i + 1]] - all[idx[i]], all[idx[i + 2]] - all[idx[i]]) / 2;
            t.Near(area, 100 - 8, 1e-9);
        });
    }
}
