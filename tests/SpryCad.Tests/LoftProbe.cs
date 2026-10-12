using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// Harder lofts, each checked for a valid closed body, a positive volume, a closed display mesh and a boolean with a
/// box crossing it: `loftprobe [filter]` prints one line per case.
/// </summary>
static class LoftProbe
{
    static Frame At(double z) => new(new Vec3(0, 0, z), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
    static Frame Tilted(Vec3 o, double angleAboutY)
    {
        var n = new Vec3(Math.Sin(angleAboutY), 0, Math.Cos(angleAboutY));
        var x = new Vec3(Math.Cos(angleAboutY), 0, -Math.Sin(angleAboutY));
        return new Frame(o, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY));
    }
    public static ProfileRegion Rect(double w, double h) => new(ProfileLoop.Polygon(new[] { new Vec2(-w / 2, -h / 2), new Vec2(w / 2, -h / 2), new Vec2(w / 2, h / 2), new Vec2(-w / 2, h / 2) }, "L"));
    public static ProfileRegion Circle(double r, double cx = 0, double cy = 0) => new(ProfileLoop.Circle(new Vec2(cx, cy), r, "C"));
    public static ProfileLoop Poly(IEnumerable<Vec2> p, string n) => ProfileLoop.Polygon(p, n);
    public static ProfileRegion Ngon(int n, double r, double rot = 0) => new(Poly(Enumerable.Range(0, n).Select(i => Vec2.FromAngle(rot + 2 * Math.PI * i / n, r)), "N"));
    public static ProfileRegion Star(int n, double r0, double r1) => new(Poly(Enumerable.Range(0, 2 * n).Select(i => Vec2.FromAngle(Math.PI * i / n, i % 2 == 0 ? r0 : r1)), "S"));

    /// <summary>A rounded rectangle w × h, corners r (counter-clockwise).</summary>
    public static ProfileLoop RoundRect(double w, double h, double r, string name = "R")
    {
        var l = new ProfileLoop();
        double x = w / 2 - r, y = h / 2 - r;
        l.Verts.Add(new ProfileVertex(new Vec2(-x, -h / 2), name + "b"));
        l.AddArc(new Vec2(x, -y), r, -Math.PI / 2, 0, true, name + "a0");
        l.Verts.Add(new ProfileVertex(new Vec2(w / 2, -y), name + "r"));
        l.AddArc(new Vec2(x, y), r, 0, Math.PI / 2, true, name + "a1");
        l.Verts.Add(new ProfileVertex(new Vec2(x, h / 2), name + "t"));
        l.AddArc(new Vec2(-x, y), r, Math.PI / 2, Math.PI, true, name + "a2");
        l.Verts.Add(new ProfileVertex(new Vec2(-w / 2, y), name + "l"));
        l.AddArc(new Vec2(-x, -y), r, Math.PI, 1.5 * Math.PI, true, name + "a3");
        return l;
    }

    /// <summary>A slot: two half circles r joined by lines, length L between centres.</summary>
    public static ProfileLoop Slot(double len, double r, string name = "T")
    {
        var l = new ProfileLoop();
        l.Verts.Add(new ProfileVertex(new Vec2(-len / 2, -r), name + "b"));
        l.AddArc(new Vec2(len / 2, 0), r, -Math.PI / 2, Math.PI / 2, true, name + "a");
        l.Verts.Add(new ProfileVertex(new Vec2(len / 2, r), name + "t"));
        l.AddArc(new Vec2(-len / 2, 0), r, Math.PI / 2, 1.5 * Math.PI, true, name + "c");
        return l;
    }

    static List<NSweep.Seg> Arc3(Vec3 a, Vec3 m, Vec3 e)
    {
        // circle through three points
        var ab = m - a; var ac = e - a; var n = Vec3.Cross(ab, ac).Normalized;
        var num = Vec3.Cross(n, ab) * ac.LengthSq - Vec3.Cross(n, ac) * ab.LengthSq;
        var c = a + num * (1 / (2 * Vec3.Dot(Vec3.Cross(ab, ac), n)));
        var r = a - c;
        var axis = Vec3.Cross(r, m - c).Normalized;
        double ang = Math.Acos(Math.Clamp(Vec3.Dot(r.Normalized, (e - c).Normalized), -1, 1));
        // the long way round when m is not on the short arc
        var mid = c + (r * Math.Cos(ang / 2) + Vec3.Cross(axis, r) * Math.Sin(ang / 2));
        if (Vec3.Dot(mid - c, m - c) < 0) ang = 2 * Math.PI - ang;
        return new List<NSweep.Seg> { new(a, e, c, axis, ang) };
    }

    static NLoft.Section S(double z, params ProfileRegion[] r) => new(At(z), r);

    public static IEnumerable<(string name, Func<NBody> make)> Cases()
    {
        yield return ("slot-circle-roundrect", () => NLoft.Loft(new[] { S(0, new ProfileRegion(Slot(30, 8))), S(20, Circle(10)), S(40, new ProfileRegion(RoundRect(36, 20, 4))) }, null, null, "X"));
        yield return ("shank-roundrects", () => NLoft.Loft(new[] { S(0, new ProfileRegion(RoundRect(16, 11, 5))), S(40, new ProfileRegion(RoundRect(24, 15, 7))) }, null, null, "X"));
        yield return ("roundrect-triangle", () => NLoft.Loft(new[] { S(0, new ProfileRegion(RoundRect(30, 30, 5))), S(30, Ngon(3, 12)) }, null, null, "X"));
        yield return ("star-circle", () => NLoft.Loft(new[] { S(0, Star(5, 15, 7)), S(25, Circle(8)) }, null, null, "X"));
        yield return ("six-sections-tangent", () => NLoft.Loft(Enumerable.Range(0, 6).Select(i => S(i * 12, i % 2 == 0 ? Rect(20, 20) : Circle(9))).ToArray(), null, new NLoft.Options(NLoft.Tangency.Normal, 1, NLoft.Tangency.Normal, 0.5), "X"));
        yield return ("holes-differ", () => NLoft.Loft(new[] { S(0, new ProfileRegion(Poly(new[] { new Vec2(-15, -15), new Vec2(15, -15), new Vec2(15, 15), new Vec2(-15, 15) }, "O"), new[] { ProfileLoop.Circle(Vec2.Zero, 6, "I") })),
                                                           S(25, new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 14, "O"), new[] { Poly(new[] { new Vec2(-5, -5), new Vec2(-5, 5), new Vec2(5, 5), new Vec2(5, -5) }, "I") })) }, null, null, "X"));
        yield return ("circle-2guides-3sections", () => NLoft.Loft(new[] { S(0, Circle(10)), S(20, Circle(15)), S(40, Circle(10)) },
            new[] { Arc3(new Vec3(10, 0, 0), new Vec3(15, 0, 20), new Vec3(10, 0, 40)), Arc3(new Vec3(-10, 0, 0), new Vec3(-15, 0, 20), new Vec3(-10, 0, 40)) }, null, "X"));
        yield return ("rect-4guides", () => NLoft.Loft(new[] { S(0, Rect(20, 20)), S(30, Rect(20, 20)) },
            new[] { (1, 1), (-1, 1), (-1, -1), (1, -1) }.Select(s => Arc3(new Vec3(10 * s.Item1, 10 * s.Item2, 0), new Vec3(13 * s.Item1, 13 * s.Item2, 15), new Vec3(10 * s.Item1, 10 * s.Item2, 30))).ToArray(), null, "X"));
        yield return ("guide-plus-tangency", () => NLoft.Loft(new[] { S(0, Circle(10)), S(40, Circle(10)) }, new[] { Arc3(new Vec3(10, 0, 0), new Vec3(15, 0, 20), new Vec3(10, 0, 40)) },
            new NLoft.Options(NLoft.Tangency.Normal, 1, NLoft.Tangency.Normal, 1), "X"));
        yield return ("guide-mid-edge", () => NLoft.Loft(new[] { S(0, Rect(20, 20)), S(30, Rect(20, 20)) }, new[] { Arc3(new Vec3(10, 0, 0), new Vec3(16, 0, 15), new Vec3(10, 0, 30)) }, null, "X"));
        yield return ("twist-90-in-4", () => NLoft.Loft(Enumerable.Range(0, 4).Select(i => S(i * 10, Ngon(4, 14, i * Math.PI / 6))).ToArray(), null, null, "X"));
        yield return ("tiny", () => NLoft.Loft(new[] { S(0, Rect(0.02, 0.02)), S(0.03, Circle(0.008)) }, null, null, "X"));
        yield return ("huge-cylinder-extrude", () => NBuild.Extrude(new[] { Circle(8000) }, At(0), 0, 30000, "X"));
        yield return ("huge", () => NLoft.Loft(new[] { S(0, Rect(20000, 20000)), S(30000, Circle(8000)) }, null, null, "X"));
        yield return ("keyhole", () =>
        {
            // a circle r 10 with a slot 6 wide out to y = -20 (concave corners)
            var l = new ProfileLoop();
            double a = Math.Asin(3 / 10.0);
            l.Verts.Add(new ProfileVertex(new Vec2(3, -20), "k0"));
            l.AddArc(Vec2.Zero, 10, -Math.PI / 2 + a, 1.5 * Math.PI - a, true, "k2");
            l.Verts.Add(new ProfileVertex(new Vec2(-3, -10 * Math.Cos(a)), "k3"));
            l.Verts.Add(new ProfileVertex(new Vec2(-3, -20), "k4"));
            return NLoft.Loft(new[] { S(0, new ProfileRegion(l)), S(25, Circle(12, 0, -4)) }, null, null, "X");
        });
        yield return ("lens-point-profile-point", () => NLoft.Loft(new[] { new NLoft.Section(At(0), Array.Empty<ProfileRegion>(), Vec3.Zero), S(15, Circle(10)), new NLoft.Section(At(30), Array.Empty<ProfileRegion>(), new Vec3(0, 0, 30)) }, null, null, "X"));
        yield return ("wedge-60", () => NLoft.Loft(new[] { new NLoft.Section(Tilted(Vec3.Zero, 0), new[] { Rect(16, 10) }), new NLoft.Section(Tilted(new Vec3(30 - 30 * Math.Cos(Math.PI / 3), 0, 30 * Math.Sin(Math.PI / 3)) - new Vec3(30, 0, 0) + new Vec3(30, 0, 0), Math.PI / 3), new[] { Rect(16, 10) }) }, null, null, "X"));
        yield return ("fan-out", () => NLoft.Loft(new[] { S(0, Circle(2)), S(10, Rect(80, 40)) }, null, null, "X"));
        yield return ("offset-sections", () => NLoft.Loft(new[] { S(0, Circle(8)), new NLoft.Section(new Frame(new Vec3(25, 10, 20), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ), new[] { Rect(12, 12) }) }, null, null, "X"));
        yield return ("many-sections-12", () => NLoft.Loft(Enumerable.Range(0, 12).Select(i => S(i * 5, Circle(8 + 3 * Math.Sin(i * 0.8)))).ToArray(), null, null, "X"));
        yield return ("centerline-s", () =>
        {
            Frame Tilt(double a) { var o = new Vec3(40 - 40 * Math.Cos(a), 0, 40 * Math.Sin(a)); var x = new Vec3(Math.Cos(a), 0, -Math.Sin(a)); return new Frame(o, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY)); }
            var arc = new NSweep.Seg(Vec3.Zero, new Vec3(40, 0, 40), new Vec3(40, 0, 0), new Vec3(0, 1, 0), Math.PI / 2);
            var arc2 = new NSweep.Seg(new Vec3(40, 0, 40), new Vec3(80, 0, 80), new Vec3(40, 0, 80), new Vec3(0, -1, 0), Math.PI / 2);
            return NLoft.Loft(new[] { new NLoft.Section(Tilt(0), new[] { Rect(10, 10) }), new NLoft.Section(Tilt(Math.PI / 2), new[] { Circle(6) }),
                new NLoft.Section(new Frame(new Vec3(80, 0, 80), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ), new[] { Rect(10, 10) }) }, null, new NLoft.Options { Centerline = new[] { arc, arc2 } }, "S");
        });
        yield return ("centerline-s-sketch", () =>
        {
            // the feature test's S: square 10 at the origin on Top, up a quarter right (r 10), a quarter back up, straight to y 40
            var top = new Frame(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var top40 = new Frame(new Vec3(0, 40, 0), Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var cl = new List<NSweep.Seg> {
                new(Vec3.Zero, new Vec3(10, 10, 0), new Vec3(10, 0, 0), new Vec3(0, 0, -1), Math.PI / 2),
                new(new Vec3(10, 10, 0), new Vec3(20, 20, 0), new Vec3(10, 20, 0), new Vec3(0, 0, 1), Math.PI / 2),
                new(new Vec3(20, 20, 0), new Vec3(20, 40, 0)) };
            return NLoft.Loft(new[] { new NLoft.Section(top, new[] { Rect(10, 10) }), new NLoft.Section(top40 with { Origin = new Vec3(20, 40, 0) }, new[] { Rect(10, 10) }) }, null, new NLoft.Options { Centerline = cl }, "S");
        });
        yield return ("torus-sweep-reference", () =>
        {
            var path = new List<NSweep.Seg> { new(new Vec3(40, 0, 0), new Vec3(40, 0, 0), Vec3.Zero, new Vec3(0, -1, 0), 2 * Math.PI) };
            return NSweep.Sweep(new[] { Circle(6) }, new Frame(new Vec3(40, 0, 0), Vec3.UnitX, Vec3.UnitY, new Vec3(0, 0, 1)), path, "T");
        });
        yield return ("closed-ring-guide", () =>
        {
            NLoft.Section Ring(double a, ProfileRegion r) { var x = new Vec3(Math.Cos(a), 0, Math.Sin(a)); return new NLoft.Section(new Frame(x * 40, x, Vec3.UnitY, Vec3.Cross(x, Vec3.UnitY)), new[] { r }); }
            var round = new List<NSweep.Seg> { new(new Vec3(46, 0, 0), new Vec3(-46, 0, 0), Vec3.Zero, new Vec3(0, -1, 0), Math.PI), new(new Vec3(-46, 0, 0), new Vec3(46, 0, 0), Vec3.Zero, new Vec3(0, -1, 0), Math.PI) };
            return NLoft.Loft(Enumerable.Range(0, 6).Select(i => Ring(i * Math.PI / 3, Circle(6))).ToArray(), new[] { round }, new NLoft.Options(Closed: true), "R");
        });
        yield return ("arc-guides-on-slot", () => NLoft.Loft(new[] { S(0, new ProfileRegion(Slot(20, 6))), S(30, new ProfileRegion(Slot(20, 6))) },
            new[] { Arc3(new Vec3(16, 0, 0), new Vec3(22, 0, 15), new Vec3(16, 0, 30)) }, null, "X"));
    }

    /// <summary>The STEP test models imported as B-splines (no analytic recognition), meshed: triangles and time.</summary>
    public static int Step()
    {
        StepImport.RecognizeAnalytic = false;
        foreach (var f in Directory.GetFiles("tests/SpryCad.Tests/models/step", "*.step"))
        {
            var r = StepImport.Read(File.ReadAllBytes(f), "S.");
            foreach (var x in r.Solids) _ = NTess.Facets(x.Body.Clone()).Count; // warm
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tris = 0;
            for (int i = 0; i < 5; i++) foreach (var x in r.Solids) tris = NTess.Facets(x.Body.Clone()).Count;
            Console.WriteLine($"{Path.GetFileName(f)}: {tris} triangles, {sw.Elapsed.TotalMilliseconds / 5:0.0} ms");
        }
        return 0;
    }

    /// <summary>`loftshell [filter]`: each probe loft shelled 1.5 thick with its end face open (else its start).</summary>
    public static int Shell(string? filter)
    {
        foreach (var (name, make) in Cases())
        {
            if (filter != null && !name.Contains(filter)) continue;
            NBody b;
            try { b = make(); } catch (Exception) { continue; }
            var open = b.Faces.Any(f => f.Tag.Name.EndsWith(".End")) ? b.Faces.First(f => f.Tag.Name.EndsWith(".End")).Tag.Name
                : b.Faces.Any(f => f.Tag.Name.EndsWith(".Start")) ? b.Faces.First(f => f.Tag.Name.EndsWith(".Start")).Tag.Name : null;
            if (open == null) { Console.WriteLine($"--   {name}: no end face"); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("SHELL_CORNERS") != null) NShell.Debug = m => { if (m.StartsWith("vertex") || m.StartsWith("edge")) Console.WriteLine("   " + m); };
            try
            {
                var t = Math.Min(1.5, 0.02 * b.Bounds.Diagonal);
                if (Environment.GetEnvironmentVariable("SHELL_DBG") != null)
                {
                    var inner = NShell.Offset(b, f => f.Tag.Name == open ? t : -t, "SH.I.");
                    var iv = NMass.Of(inner)?.Volume;
                    var ib = inner.Bounds; var bb = b.Bounds;
                    Console.WriteLine($"   inner {inner.Validate() ?? "valid"} volume {iv:0.#}, box {ib.Min}..{ib.Max} vs {bb.Min}..{bb.Max}");
                    foreach (var f in inner.Faces) Console.WriteLine($"     {f.Tag.Name} {f.Surface.Kind} sense {f.Sense} R {f.Surface.R}");
                }
                if (Environment.GetEnvironmentVariable("SHELL_TIME") != null)
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    Prof.Reset(); Prof.Enabled = true;
                    var inner = NShell.Offset(b, f => f.Tag.Name == open ? t : -t, "SH.I.");
                    Prof.Enabled = false;
                    foreach (var (pn, ms, calls) in Prof.Results().Where(x => x.name.StartsWith("shell"))) Console.WriteLine($"     {pn}: {ms:0} ms ({calls})");
                    var tInner = w.Elapsed.TotalMilliseconds;
                    var r = NBoolean.Run(b, inner, BoolOp.Subtract, (x, y) => x == open || y == "SH.I." + open);
                    var tBool = w.Elapsed.TotalMilliseconds - tInner;
                    _ = NTess.ToSolid(r).Topology;
                    Console.WriteLine($"   inner {tInner:0} ms, boolean {tBool:0} ms, mesh {w.Elapsed.TotalMilliseconds - tInner - tBool:0} ms");
                }
                Prof.Reset(); Prof.Enabled = Environment.GetEnvironmentVariable("SHELL_DBG") != null;
                if (Prof.Enabled) NBoolean.Trace = m => Console.WriteLine("   T " + (m.Length > 220 ? m[..220] : m));
                var sh = NShell.Apply(b, new HashSet<string> { open }, t, "SH");
                if (Prof.Enabled) { foreach (var (pn, ms, calls) in Prof.Results().Where(x => x.ms >= 20)) Console.WriteLine($"     {pn}: {ms:0} ms ({calls})"); Prof.Enabled = false; }
                if (Environment.GetEnvironmentVariable("SHELL_DBG") != null) foreach (var f in sh.Faces) Console.WriteLine($"   result {f.Tag.Name} {f.Surface.Kind} loops {f.Loops.Count} edges {f.Coedges.Count()} sense {f.Sense}");
                var err = sh.Validate();
                var v0 = NMass.Of(b)?.Volume ?? 0; var v1 = NMass.Of(sh)?.Volume ?? double.NaN;
                if (Environment.GetEnvironmentVariable("SHELL_OPEN") != null) Topology.Debug = m => Console.WriteLine("   " + m);
                var mesh = NTess.ToSolid(sh).Topology.OpenHalfEdges;
                Topology.Debug = null;
                Console.WriteLine($"{(err == null && mesh == 0 && v1 > 0 && v1 < v0 ? "ok  " : "BAD ")} {name}: {err ?? "valid"}, volume {v0:0.#} -> {v1:0.#}, open mesh edges {mesh}, {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message} ({sw.ElapsedMilliseconds} ms)"); }
        }
        return 0;
    }

    public static int Run(string? filter)
    {
        int bad = 0;
        foreach (var (name, make) in Cases())
        {
            if (filter != null && !name.Contains(filter)) continue;
            // warm (the first run of each path pays for compiling it)
            try { var w = make(); _ = NTess.ToSolid(w).Topology; } catch (Exception) { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res;
            try
            {
                var b = make();
                var tLoft = sw.Elapsed.TotalMilliseconds;
                var err = b.Validate();
                var mp = NMass.Of(b);
                Prof.Enabled = Environment.GetEnvironmentVariable("LOFT_PROF") != null; Prof.Reset();
                var sol = NTess.ToSolid(b);
                var open = sol.Topology.OpenHalfEdges;
                if (Prof.Enabled) { foreach (var (pn, ms, calls) in Prof.Results()) Console.WriteLine($"    {pn}: {ms:0.0} ms ({calls})"); Prof.Enabled = false; }
                var tMesh = sw.Elapsed.TotalMilliseconds - tLoft;
                // a box through the middle
                var bx = b.Bounds;
                var c = bx.Center; var sz = bx.Size;
                var box = Builders.Box(c - new Vec3(sz.X * 0.25, sz.Y * 0.8, sz.Z * 0.2), c + new Vec3(sz.X * 0.8, sz.Y * 0.25, sz.Z * 0.2));
                var u = Csg.Union(sol, box);
                var uv = u.Native != null ? NMass.Of(u.Native)?.Volume : null;
                var tBool = sw.Elapsed.TotalMilliseconds - tLoft - tMesh;
                if (Environment.GetEnvironmentVariable("LOFT_KINK") != null)
                {
                    // the largest angle between the normals of two faces along each edge they share (B-splines)
                    foreach (var e in b.Edges.Where(e => e.Uses.Count == 2 && e.Uses.All(u => u.Face.Surface.Kind != SurfaceKind.Plane)))
                    {
                        var f0 = e.Uses[0].Face; var f1 = e.Uses[1].Face;
                        if (f0.Tag.Name.EndsWith("Start") || f0.Tag.Name.EndsWith("End") || f1.Tag.Name.EndsWith("Start") || f1.Tag.Name.EndsWith("End")) continue;
                        double worst = 0;
                        for (int i = 1; i < 16; i++)
                        {
                            var q = e.At(i / 16.0);
                            var n0 = f0.Surface.Normal(f0.Surface.Project(q)); var n1 = f1.Surface.Normal(f1.Surface.Project(q));
                            worst = Math.Max(worst, Math.Acos(Math.Clamp(Math.Abs(Vec3.Dot(n0, n1)), 0, 1)) * 180 / Math.PI);
                        }
                        Console.WriteLine($"   {f0.Tag.Name} | {f1.Tag.Name}: {worst:0.####}°");
                    }
                }
                var kinds = string.Join(",", b.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Key}:{g.Count()}"));
                var spans = b.Faces.Where(f => f.Surface.Nurbs != null).Select(f => f.Surface.Nurbs!).Select(n => $"{n.SpansU}x{n.SpansV}" + (Prof.Enabled || Environment.GetEnvironmentVariable("LOFT_PROF") != null ? $"(rho {n.DirectionalCurvatureRadius().u:0.##},{n.DirectionalCurvatureRadius().v:0.##})" : "")).Distinct();
                kinds += " spans " + string.Join(" ", spans);
                bool ok = err == null && mp != null && mp.Volume > 0 && open == 0 && u.Native != null && uv > mp.Volume;
                if (!ok) bad++;
                var fine = Environment.GetEnvironmentVariable("LOFT_FINE") != null ? NTess.ToSolid(b, 2e-5).Volume() : 0;
                res = $"{(ok ? "ok  " : "BAD ")} {name}: {(err ?? "valid")}, vol {mp?.Volume:0.####} (mesh {fine:0.####}), open {open}, union {(u.Native != null ? $"native {uv:0.###}" : "FACETED")} | faces {kinds} | loft {tLoft:0} ms, mesh {tMesh:0}, bool {tBool:0}";
            }
            catch (Exception ex)
            {
                bad++;
                res = $"FAIL {name}: {ex.GetType().Name}: {ex.Message}";
            }
            Console.WriteLine(res);
        }
        return bad;
    }
}

/// <summary>`loftmirror`: a loft mirrored (alone, and merged on a block), each feature's rebuild time.</summary>
static class LoftMirrorBench
{
    static SpryCad.Core.Sketching.Sketch RectAt(Vec2 c, double w, double h)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var p = new[] { sk.AddPoint(c + new Vec2(-w / 2, -h / 2)), sk.AddPoint(c + new Vec2(w / 2, -h / 2)), sk.AddPoint(c + new Vec2(w / 2, h / 2)), sk.AddPoint(c + new Vec2(-w / 2, h / 2)) };
        for (int i = 0; i < 4; i++) sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = p[i].Id, P2 = p[(i + 1) % 4].Id });
        return sk;
    }
    static SpryCad.Core.Sketching.Sketch CircleAt(Vec2 c, double r)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = sk.AddPoint(c).Id, R = r });
        return sk;
    }
    static SpryCad.Core.Model.SketchFeature OnPlane(SpryCad.Core.Model.CadDocument doc, double offset, SpryCad.Core.Sketching.Sketch sk)
    {
        if (offset == 0) return doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = sk });
        var pl = doc.AddFeature(new SpryCad.Core.Model.PlaneFeature { Base = SpryCad.Core.Model.PlaneRef.Top, Offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = new SpryCad.Core.Model.PlaneRef { Feature = pl.Id }, Sketch = sk });
    }

    // half a circle r (x ≥ 0) closed by its diameter on x = 0, centred at (0, cy)
    static SpryCad.Core.Sketching.Sketch HalfCircle(double r)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var c = sk.AddPoint(Vec2.Zero); var a = sk.AddPoint(new Vec2(0, -r)); var b = sk.AddPoint(new Vec2(0, r));
        sk.Add(new SpryCad.Core.Sketching.SkArc { Center = c.Id, Start = a.Id, End = b.Id });
        sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = b.Id, P2 = a.Id });
        return sk;
    }

    /// <summary>`loftmirror file.sprt`: the model rebuilt, then its bodies meshed for display, each step timed.</summary>
    public static int File(string path)
    {
        var doc = SpryCad.Core.Model.CadDocument.Load(path);
        if (Environment.GetEnvironmentVariable("LOFT_WARM") != null)
        {
            var ws = System.Diagnostics.Stopwatch.StartNew();
            SpryCad.Core.Regen.Warmup.Run();
            Console.WriteLine($"warm-up {ws.Elapsed.TotalMilliseconds:0} ms");
        }
        for (int it = 0; it < 3; it++)
        {
            SpryCad.Kernel.Csg.ClearCache();
            SpryCad.Kernel.Prof.Reset(); SpryCad.Kernel.Prof.Enabled = it == 2;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            var t1 = sw.Elapsed.TotalMilliseconds;
            int tris = 0;
            foreach (var b in r.State.Bodies) { var native = b.Solid.Native!; var sol = SpryCad.Kernel.Native.NTess.ToSolid(native.Clone()); tris += sol.Faces.Count; _ = sol.Topology; }
            var t2 = sw.Elapsed.TotalMilliseconds - t1;
            SpryCad.Kernel.Prof.Enabled = false;
            Console.WriteLine($"rebuild {t1:0} ms, display mesh {t2:0} ms ({tris} triangles); " + string.Join(", ", doc.Features.Select(f => $"{f.Name} {r.Status[f.Id].Milliseconds:0}")));
            if (it == 2) foreach (var (pn, ms, calls) in SpryCad.Kernel.Prof.Results().Where(x => x.ms > 5)) Console.WriteLine($"    {pn}: {ms:0} ms ({calls})");
        }
        return 0;
    }

    public static int Run()
    {
        foreach (var rad in new[] { 12.0, 13.0, 11.0 })
        {
            // the usual symmetric part: half a square lofted to half a circle, its flat side on the mirror plane
            var doc = new SpryCad.Core.Model.CadDocument();
            var a = OnPlane(doc, 0, RectAt(new Vec2(10, 0), 20, 30));
            var c = OnPlane(doc, 40, HalfCircle(rad));
            var lf = doc.AddFeature(new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { a.Id, c.Id } });
            var m = doc.AddFeature(new SpryCad.Core.Model.MirrorFeature { Seeds = { lf.Id }, Plane = SpryCad.Core.Model.PlaneRef.Right });
            for (int it = 0; it < 2; it++)
            {
                SpryCad.Kernel.Prof.Reset(); SpryCad.Kernel.Prof.Enabled = it == 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
                if (it == 0) foreach (var (pn, ms, calls) in SpryCad.Kernel.Prof.Results().Where(x => x.ms > 20 || x.name.StartsWith("closest") || x.name.StartsWith("project"))) Console.WriteLine($"    {pn}: {ms:0} ms ({calls})");
                SpryCad.Kernel.Prof.Enabled = false;
                Console.WriteLine($"half on the plane (r {rad}): {sw.Elapsed.TotalMilliseconds:0} ms; loft {(r.Status[lf.Id].Ok ? "ok" : r.Status[lf.Id].Error)}, mirror {(r.Status[m.Id].Ok ? "ok" : r.Status[m.Id].Error)}, bodies {r.State.Bodies.Count}, native {r.State.Bodies.All(x => x.Solid.Native != null)}");
            }
        }
        foreach (var xc in new[] { 15.0, 8.0 })
        foreach (var block in new[] { false })
            foreach (var guided in new[] { false })
            {
                var doc = new SpryCad.Core.Model.CadDocument();
                double z0 = 0;
                if (block)
                {
                    var b = OnPlane(doc, 0, RectAt(Vec2.Zero, 120, 60));
                    doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = b.Id, Depth = "20" });
                    z0 = 20;
                }
                var a = OnPlane(doc, z0, RectAt(new Vec2(xc, 0), 30, 30));
                var c = OnPlane(doc, z0 + 40, CircleAt(new Vec2(xc, 0), 10));
                var lf = new SpryCad.Core.Model.LoftFeature { SectionSketchIds = { a.Id, c.Id } };
                if (guided)
                {
                    // on Front: from the square's corner (45, z0) out through (50, z0 + 20) to the circle's (40, z0 + 40)
                    var g = new SpryCad.Core.Sketching.Sketch();
                    Vec2 p0 = new(xc + 15, z0), p1 = new(xc + 20, z0 + 20), p2 = new(xc + 10, z0 + 40);
                    // the circle through three points
                    double ax = p0.X, ay = p0.Y, bx = p1.X, by = p1.Y, cx = p2.X, cy = p2.Y;
                    double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
                    var cc = new Vec2(((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d,
                        ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d);
                    var ce = g.AddPoint(cc); var s = g.AddPoint(p0); var e = g.AddPoint(p2);
                    g.Add(new SpryCad.Core.Sketching.SkArc { Center = ce.Id, Start = s.Id, End = e.Id });
                    var gf = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = g });
                    lf.GuideSketchIds.Add(gf.Id);
                }
                doc.AddFeature(lf);
                var m = doc.AddFeature(new SpryCad.Core.Model.MirrorFeature { Seeds = { lf.Id }, Plane = SpryCad.Core.Model.PlaneRef.Right });
                for (int it = 0; it < 2; it++)
                {
                    SpryCad.Kernel.Prof.Reset(); SpryCad.Kernel.Prof.Enabled = true;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r1 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc, doc.Features.IndexOf(m));
                    var t1 = sw.Elapsed.TotalMilliseconds;
                    var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
                    var t2 = sw.Elapsed.TotalMilliseconds - t1;
                    if (it == 0) { Console.WriteLine($"x {xc} block {block}, guided {guided}: cold: up to the loft {t1:0} ms, with the mirror {t2:0} ms"); foreach (var (pn, ms, calls) in SpryCad.Kernel.Prof.Results().Take(200)) Console.WriteLine($"    {pn}: {ms:0} ms ({calls})"); }
                    if (it == 1)
                    {
                        Console.WriteLine($"   warm: block {block}, guided {guided}: up to the loft {t1:0} ms, with the mirror {t2:0} ms; loft {(r.Status[lf.Id].Ok ? "ok" : r.Status[lf.Id].Error)}, mirror {(r.Status[m.Id].Ok ? "ok" : r.Status[m.Id].Error)}, bodies {r.State.Bodies.Count}, native {r.State.Bodies.All(x => x.Solid.Native != null)}");
                        foreach (var (pn, ms, calls) in SpryCad.Kernel.Prof.Results().Take(8)) Console.WriteLine($"    {pn}: {ms:0} ms ({calls})");
                    }
                    SpryCad.Kernel.Prof.Enabled = false;
                }
            }
        return 0;
    }
}
