using SpryCad.Kernel;

namespace SpryCad.Tests;

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
        t.Test("B-spline derivatives and closest points match brute force", () =>
        {
            // a wavy 3D interpolant (uneven knots) and a degree-5 curve with a double interior knot
            var pts = Enumerable.Range(0, 12).Select(i => new Vec3(i * (1 + 0.3 * Math.Sin(i)), Math.Sin(i * 0.9) * 3, Math.Cos(i * 0.4) * 2)).ToList();
            var a = BSplineCurve.Interpolate(pts, 3);
            var b = new BSplineCurve(5, Enumerable.Range(0, 9).Select(i => new Vec3(i, Math.Sin(i), i % 3)).ToArray(),
                new double[] { 0, 0, 0, 0, 0, 0, 0.3, 0.6, 0.6, 1, 1, 1, 1, 1, 1 });
            foreach (var s in new[] { a, b })
            {
                for (int k = 1; k < 50; k++)
                {
                    double u = k / 50.0, h = 1e-5;
                    s.Derivs(u, out var c, out var d1, out var d2);
                    t.True(Vec3.Distance(c, s.Eval(u)) < 1e-12, $"point at {u}");
                    var fd1 = (s.Eval(u + h) - s.Eval(u - h)) / (2 * h);
                    var fd2 = (s.Eval(u + h) - c * 2 + s.Eval(u - h)) / (h * h);
                    t.True(Vec3.Distance(d1, fd1) < 1e-5 * (1 + d1.Length), $"first derivative at {u}");
                    if (Math.Abs(u - 0.3) > 2 * h && Math.Abs(u - 0.6) > 2 * h)
                        t.True(Vec3.Distance(d2, fd2) < 1e-2 * (1 + d2.Length), $"second derivative at {u}");
                }
                var ns = new SpryCad.Kernel.Native.NSpline(s);
                var rnd = new Random(3);
                for (int k = 0; k < 200; k++)
                {
                    var p = s.Eval(rnd.NextDouble()) + new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5);
                    var d = Vec3.Distance(ns.Eval(ns.Param(p)), p);
                    double best = double.MaxValue;
                    for (int i = 0; i <= 20000; i++) best = Math.Min(best, Vec3.Distance(s.Eval(i / 20000.0), p));
                    t.True(d <= best + 1e-9, $"closest point {d} vs brute force {best}");
                }
            }
        });
        t.Test("B-spline interpolation in band storage matches the dense solve bit for bit", () =>
        {
            // the dense interpolation (Piegl & Tiller A9.1, the band eliminated in a full matrix), as it was
            static BSplineCurve Dense(IReadOnlyList<Vec3> pts, int degree)
            {
                int n = pts.Count - 1, p = Math.Min(degree, n);
                var uk = new double[n + 1];
                double total = 0;
                for (int k = 1; k <= n; k++) total += Vec3.Distance(pts[k], pts[k - 1]);
                if (total < 1e-15) total = 1;
                for (int k = 1; k <= n; k++) uk[k] = uk[k - 1] + Vec3.Distance(pts[k], pts[k - 1]) / total;
                uk[n] = 1;
                var U = new double[n + p + 2];
                for (int i = n + 1; i < U.Length; i++) U[i] = 1;
                for (int j = 1; j <= n - p; j++) { double s = 0; for (int i = j; i <= j + p - 1; i++) s += uk[i]; U[j + p] = s / p; }
                int Span(double u)
                {
                    if (u >= U[n + 1]) return n;
                    if (u <= U[p]) return p;
                    int low = p, high = n + 1, mid = (low + high) / 2;
                    while (u < U[mid] || u >= U[mid + 1]) { if (u < U[mid]) high = mid; else low = mid; mid = (low + high) / 2; }
                    return mid;
                }
                double[] Basis(int i, double u)
                {
                    var N = new double[p + 1]; var left = new double[p + 1]; var right = new double[p + 1];
                    N[0] = 1;
                    for (int j = 1; j <= p; j++)
                    {
                        left[j] = u - U[i + 1 - j]; right[j] = U[i + j] - u;
                        double saved = 0;
                        for (int r = 0; r < j; r++)
                        {
                            var den = right[r + 1] + left[j - r];
                            var temp = den != 0 ? N[r] / den : 0;
                            N[r] = saved + right[r + 1] * temp; saved = left[j - r] * temp;
                        }
                        N[j] = saved;
                    }
                    return N;
                }
                int m = n + 1, w = 2 * p + 1;
                var M = new double[m, m];
                for (int k = 0; k <= n; k++) { int sp = Span(uk[k]); var N = Basis(sp, uk[k]); for (int i = 0; i <= p; i++) M[k, sp - p + i] = N[i]; }
                var x = pts.ToArray();
                for (int c = 0; c < m; c++)
                {
                    var piv = M[c, c];
                    for (int r = c + 1; r < Math.Min(m, c + w); r++)
                    {
                        var f = M[r, c] / piv;
                        if (f == 0) continue;
                        for (int k = c; k < Math.Min(m, c + w); k++) M[r, k] -= f * M[c, k];
                        x[r] -= x[c] * f;
                    }
                }
                for (int r = m - 1; r >= 0; r--) { var s = x[r]; for (int k = r + 1; k < Math.Min(m, r + w); k++) s -= x[k] * M[r, k]; x[r] = s / M[r, r]; }
                return new BSplineCurve(p, x, U);
            }
            static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
            var rnd = new Random(11);
            foreach (var count in new[] { 2, 3, 4, 5, 9, 40, 257, 600 })
                foreach (var degree in new[] { 1, 2, 3, 5 })
                {
                    // a wandering curve with uneven spacing (some points close together)
                    var pts = new List<Vec3>();
                    var q = Vec3.Zero;
                    for (int i = 0; i < count; i++) { q += new Vec3(rnd.NextDouble() * (i % 7 == 0 ? 0.01 : 1), rnd.NextDouble() - 0.5, Math.Sin(i * 0.3)); pts.Add(q); }
                    var a = BSplineCurve.Interpolate(pts, degree); var b = Dense(pts, degree);
                    t.Eq(a.Poles.Length, b.Poles.Length);
                    for (int i = 0; i < a.Poles.Length; i++)
                        t.True(Same(a.Poles[i].X, b.Poles[i].X) && Same(a.Poles[i].Y, b.Poles[i].Y) && Same(a.Poles[i].Z, b.Poles[i].Z), $"pole {i} of {count} points, degree {degree}");
                    t.True(a.Knots.Zip(b.Knots).All(k => Same(k.First, k.Second)), "knots");
                }
        });
        var dir = Path.Combine(Path.GetTempPath(), "sprycad-tests");
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
                if (fillet) SpryCad.Kernel.Native.NBoolean.Trace = m => log.Add(m);
                var r = EdgeBlend.Apply(rounded, topEdges, 2, fillet, "T", errs);
                SpryCad.Kernel.Native.NBoolean.Trace = null;
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
