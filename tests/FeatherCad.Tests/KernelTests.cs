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
        t.Test("Mass properties: box volume, center, inertia at center and origin, principal axes", () =>
        {
            // a 2 × 3 × 4 box (x, y, z) centered at (1, 2, 3), optionally turned about an axis through its center
            MassProps Box(Func<Vec3, Vec3> place, bool inward = false)
            {
                var mp = new MassProps();
                Vec3 C(int i) => place(new Vec3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1.5 : 1.5, (i & 4) == 0 ? -2 : 2));
                // the 6 faces as quads of corner indices, counter-clockwise from outside
                int[][] quads = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
                foreach (var q in quads)
                {
                    if (inward) { mp.AddTriangle(C(q[0]), C(q[2]), C(q[1])); mp.AddTriangle(C(q[0]), C(q[3]), C(q[2])); }
                    else { mp.AddTriangle(C(q[0]), C(q[1]), C(q[2])); mp.AddTriangle(C(q[0]), C(q[2]), C(q[3])); }
                }
                return mp.Finish();
            }
            var c0 = new Vec3(1, 2, 3);
            var b = Box(p => p + c0);
            t.Near(b.Volume, 24, 1e-9); t.Near(b.SurfaceArea, 2 * (6 + 8 + 12), 1e-9);
            t.Near(b.Center.X, 1, 1e-9); t.Near(b.Center.Y, 2, 1e-9); t.Near(b.Center.Z, 3, 1e-9);
            // m(b² + c²)/12 about each axis through the center: 50, 40, 26; no products
            t.Near(b.AtCenter[0, 0], 50, 1e-9); t.Near(b.AtCenter[1, 1], 40, 1e-9); t.Near(b.AtCenter[2, 2], 26, 1e-9);
            t.Near(b.AtCenter[0, 1], 0, 1e-9); t.Near(b.AtCenter[1, 2], 0, 1e-9); t.Near(b.AtCenter[0, 2], 0, 1e-9);
            // about the origin: parallel axis, Ixx + m(y² + z²), product -m x y
            t.Near(b.AtOrigin[0, 0], 50 + 24 * (4 + 9), 1e-9); t.Near(b.AtOrigin[0, 1], -24 * 1 * 2, 1e-9);
            t.Near(b.Principal[0], 26, 1e-9); t.Near(b.Principal[1], 40, 1e-9); t.Near(b.Principal[2], 50, 1e-9);
            t.Near(Math.Abs(b.Axes[0].Z), 1, 1e-9); t.Near(Math.Abs(b.Axes[2].X), 1, 1e-9);
            // turned 30° about Z: the same principal moments, the smallest still along Z, the largest along the turned X
            var (cs, sn) = (Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
            var r = Box(p => new Vec3(p.X * cs - p.Y * sn, p.X * sn + p.Y * cs, p.Z) + c0);
            t.Near(r.Volume, 24, 1e-9); t.Near(r.Center.X, 1, 1e-9);
            t.Near(r.Principal[0], 26, 1e-9); t.Near(r.Principal[1], 40, 1e-9); t.Near(r.Principal[2], 50, 1e-9);
            t.True(Math.Abs(r.AtCenter[0, 1]) > 1, "turned: products of inertia appear");
            t.Near(Math.Abs(Vec3.Dot(r.Axes[2], new Vec3(cs, sn, 0))), 1, 1e-9);
            t.Near(Vec3.Dot(Vec3.Cross(r.Axes[0], r.Axes[1]), r.Axes[2]), 1, 1e-9);
            // an inward-facing mesh gives the same results
            var inv = Box(p => p + c0, inward: true);
            t.Near(inv.Volume, 24, 1e-9); t.Near(inv.Center.Z, 3, 1e-9); t.Near(inv.AtCenter[0, 0], 50, 1e-9);
        });

        t.Test("Measure: closest points between points, segments and triangles", () =>
        {
            MeasureShape Tri(double z) { var s = new MeasureShape(); s.Triangles.Add((new Vec3(0, 0, z), new Vec3(10, 0, z), new Vec3(0, 10, z))); return s; }
            // parallel triangles 5 apart: the distance is the gap, closest points one above the other
            var (d, a, b) = ClosestPoints.Between(Tri(0), Tri(5));
            t.Near(d, 5, 1e-9); t.Near(a.Z, 0, 1e-9); t.Near(b.Z, 5, 1e-9); t.Near(a.X, b.X, 1e-9); t.Near(a.Y, b.Y, 1e-9);
            // a point over the inside of a triangle, and one beside it (nearest the edge)
            var p = new MeasureShape(); p.Points.Add(new Vec3(2, 2, 3));
            t.Near(ClosestPoints.Between(p, Tri(0)).Distance, 3, 1e-9);
            var q = new MeasureShape(); q.Points.Add(new Vec3(-4, 5, 3));
            t.Near(ClosestPoints.Between(q, Tri(0)).Distance, 5, 1e-9);
            // skew segments: 1 apart where they cross over
            var s1 = new MeasureShape(); s1.Segments.Add((new Vec3(-5, 0, 0), new Vec3(5, 0, 0)));
            var s2 = new MeasureShape(); s2.Segments.Add((new Vec3(0, -5, 1), new Vec3(0, 5, 1)));
            var (ds, sa, sb) = ClosestPoints.Between(s1, s2);
            t.Near(ds, 1, 1e-9); t.Near(sa.X, 0, 1e-9); t.Near(sb.Y, 0, 1e-9);
            // a segment through a triangle touches it
            var through = new MeasureShape(); through.Segments.Add((new Vec3(2, 2, -1), new Vec3(2, 2, 1)));
            t.Near(ClosestPoints.Between(through, Tri(0)).Distance, 0, 1e-12);
            // crossing triangles touch; a big mesh against a point uses its nearest triangle
            var cross = new MeasureShape(); cross.Triangles.Add((new Vec3(1, 1, -2), new Vec3(1, 1, 2), new Vec3(1, 8, 0)));
            t.Near(ClosestPoints.Between(cross, Tri(0)).Distance, 0, 1e-12);
            var grid = new MeasureShape();
            for (int i = 0; i < 40; i++)
                for (int j = 0; j < 40; j++)
                {
                    var o = new Vec3(i, j, 0);
                    grid.Triangles.Add((o, o + new Vec3(1, 0, 0), o + new Vec3(0, 1, 0)));
                    grid.Triangles.Add((o + new Vec3(1, 0, 0), o + new Vec3(1, 1, 0), o + new Vec3(0, 1, 0)));
                }
            var above = new MeasureShape(); above.Points.Add(new Vec3(17.3, 22.6, 4));
            var (dg, onGrid, _) = ClosestPoints.Between(grid, above);
            t.Near(dg, 4, 1e-9); t.Near(onGrid.X, 17.3, 1e-9); t.Near(onGrid.Y, 22.6, 1e-9);
        });

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

        t.Test("Revolve orientation: any sweep sign, loop direction and axis side", () =>
        {
            var sq = ProfileLoop.Polygon(new[] { new Vec2(2, 0), new Vec2(5, 0), new Vec2(5, 3), new Vec2(2, 3) }, "L");
            var expect = Math.PI / 2 / 2 * (25 - 4) * 3; // quarter sweep of an annulus
            foreach (var rev in new[] { false, true })
                foreach (var side in new[] { 1.0, -1.0 })
                    foreach (var dir in new[] { new Vec2(0, 1), new Vec2(0, -1) })
                        foreach (var ang in new[] { Math.PI / 2, -Math.PI / 2 })
                        {
                            var loop = side > 0 ? sq : ProfileLoop.Polygon(sq.Points.Select(p => new Vec2(-p.X, p.Y)).ToArray(), "L");
                            if (rev) loop = loop.Reversed();
                            var s = Builders.Revolve(new[] { new ProfileRegion(loop) }, Frame.Front, Vec2.Zero, dir, ang, "R");
                            t.True(s.Native != null, "native revolve");
                            t.Near(Vex(s), expect, 0.05);
                        }
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
