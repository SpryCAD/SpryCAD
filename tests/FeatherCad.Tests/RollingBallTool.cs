using System.Diagnostics;
using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary>
/// <c>-- rb case r [out.step]</c>: rolling-ball fillet on a test junction (tee, hole, torus, cone), with
/// topology, volume and timing report and optional STEP output.
/// </summary>
static class RollingBallTool
{
    public static (Solid body, List<NEdge> edges) Case(string name)
    {
        Solid a, b, s;
        switch (name)
        {
            case "tee":
            case "hole":
                a = Builders.Cylinder(new Vec3(-40, 0, 0), Vec3.UnitX, 15, 80, "Main");
                b = Builders.Cylinder(new Vec3(0, 0, name == "hole" ? -30 : 0), Vec3.UnitZ, 6, name == "hole" ? 60 : 30, "Branch");
                s = name == "hole" ? Csg.Subtract(a, b) : Csg.Union(a, b);
                break;
            case "torus":
                {
                    var prof = ProfileLoop.Circle(new Vec2(60, 0), 8, "Rim");
                    var frame = new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
                    a = Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "Rim");
                    b = Builders.Cylinder(new Vec3(0, 0, 0), Vec3.UnitX, 5, 60, "Spoke");
                    s = Csg.Union(a, b);
                    break;
                }
            case "cone":
                {
                    // conical boss on an inclined plate: plane/cone edges are not perpendicular arcs
                    a = Builders.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "Plate");
                    var prof = ProfileLoop.Polygon(new[] { new Vec2(0, -5), new Vec2(10, -5), new Vec2(6, 20), new Vec2(0, 20) }, "Boss");
                    // revolved about the frame's Y axis, tilted 17° from vertical
                    var up = new Vec3(0.3, 0, 1).Normalized; var x = Vec3.Cross(Vec3.UnitY, up).Normalized;
                    var frame = new Frame(Vec3.Zero, x, up, Vec3.Cross(x, up));
                    b = Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "Boss");
                    s = Csg.Union(a, b);
                    break;
                }
            case "boss":
            case "bore":
                // plane/cylinder edges have exact torus fillets: the rolling ball must reproduce them
                a = Builders.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "Plate");
                b = Builders.Cylinder(new Vec3(0, 0, name == "bore" ? -20 : -5), Vec3.UnitZ, 8, name == "bore" ? 40 : 20, "Boss");
                s = name == "bore" ? Csg.Subtract(a, b) : Csg.Union(a, b);
                break;
            default: throw new ArgumentException("unknown case " + name);
        }
        var nb = s.Native ?? throw new InvalidOperationException("union not native");
        nb.Link();
        string first = name switch { "torus" => "Rim", "cone" or "boss" or "bore" => "Plate", _ => "Main" };
        string second = name switch { "torus" => "Spoke", "cone" or "boss" or "bore" => "Boss", _ => "Branch" };
        var edges = nb.Edges.Where(e => e.Uses.Count == 2 && !NativeBlend.IsSeam(e)
            && e.Uses.Any(u => u.Face.Tag.Name.StartsWith(first)) && e.Uses.Any(u => u.Face.Tag.Name.StartsWith(second))).ToList();
        return (s, edges);
    }

    static Solid Fillet(Solid s, List<NEdge> edges, double r, bool force, List<string> errors)
    {
        var old = NativeBlend.ForceRollingBall;
        NativeBlend.ForceRollingBall = force;
        try { return NativeBlend.Apply(s, NativeBlend.Expand(s.Native!, edges, true), r, true, "Fillet", errors); }
        finally { NativeBlend.ForceRollingBall = old; }
    }

    /// <summary>Largest |f(p)| over points inside the blend (pipe) faces of a body.</summary>
    static double MaxOnBlends(NBody b, Func<Vec3, double> f) => MaxOnBlends(b, (_, p) => f(p));

    static double MaxOnBlends(NBody b, Func<NFace, Vec3, double> f)
    {
        double worst = 0; int n = 0;
        foreach (var face in b.Faces.Where(x => x.Surface.Kind == SurfaceKind.Pipe))
        {
            var dom = NDomain.Of(face, 1e-4);
            foreach (var p in dom.InteriorSamples(12))
                if (dom.ContainsUV(dom.Map(p))) { worst = Math.Max(worst, Math.Abs(f(face, p))); n++; }
        }
        if (n == 0) throw new AssertException("no blend faces");
        return worst;
    }

    public static void Tests(TestRunner t)
    {
        foreach (var name in new[] { "boss", "bore" })
            t.Test($"Rolling-ball fillet reproduces the exact torus fillet ({name})", () =>
            {
                var (s, edges) = Case(name);
                var errors = new List<string>();
                var exact = Fillet(s, edges, 2, false, errors);
                var rb = Fillet(s, edges, 2, true, errors);
                t.True(errors.Count == 0, string.Join("; ", errors));
                t.True(rb.Native != null && rb.Topology.OpenHalfEdges == 0 && rb.Native.Validate() == null, "rolling-ball result not a closed native body");
                t.True(rb.Native!.Faces.Any(f => f.Surface.Kind == SurfaceKind.Pipe), "no pipe faces (exact tool used?)");
                // the exact fillet surfaces: tori about the boss/bore axis, tube radius 2 around a circle of radius 10
                var heights = name == "boss" ? new[] { 2.0 } : new[] { -2.0, -8.0 };
                var tori = heights.Select(h => new NSurface(new SurfaceDef { Kind = SurfaceKind.Torus, Origin = new Vec3(0, 0, h), Axis = Vec3.UnitZ, RefDir = Vec3.UnitX, Radius = 10, Param2 = 2 })).ToList();
                var dev = MaxOnBlends(rb.Native, p => tori.Min(x => Math.Abs(x.F_(p))));
                t.True(dev < 1e-6, $"blend deviates {dev:g3} from the exact torus");
                Console.WriteLine($"        largest deviation from the torus: {dev:g2} mm");
                t.Near(KernelTests.Vex(rb), KernelTests.Vex(exact), 0.05);
            });
        foreach (var (name, sign) in new[] { ("tee", 1), ("hole", -1), ("torus", 1), ("cone", 1) })
            t.Test($"Rolling-ball fillet on {name} junction: closed, exact ball contact, fast", () =>
            {
                var (s, edges) = Case(name);
                var errors = new List<string>();
                var sw = Stopwatch.StartNew();
                var res = Fillet(s, edges, 2, false, errors);
                var ms = sw.ElapsedMilliseconds;
                t.True(errors.Count == 0, string.Join("; ", errors));
                t.True(res.Native != null && res.Topology.OpenHalfEdges == 0 && res.Native.Validate() == null, "not a closed native body");
                var dv = NTess.ToSolid(res.Native!, 1e-3).Volume() - NTess.ToSolid(s.Native!, 1e-3).Volume();
                t.True(dv * sign > 1, $"volume change {dv:0.###} has the wrong sign");
                // every blend point: its ball centre (nearest spine point) is r from both faces it blends
                var worst = MaxOnBlends(res.Native!, (face, p) =>
                {
                    var sp = face.Surface.Spine!;
                    var c = sp.Closest(p).c;
                    return Math.Max(Math.Abs(Math.Abs(sp.SupportA!.F_(c)) - 2), Math.Abs(Math.Abs(sp.SupportB!.F_(c)) - 2));
                });
                t.True(worst < 1e-6, $"ball centre off by {worst:g3}");
                t.True(ms < 3000, $"took {ms} ms");
            });
    }

    public static int Run(string[] args)
    {
        var name = args[1]; var r = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var (s, edges) = Case(name);
        Console.WriteLine($"{name}: {edges.Count} edges ({string.Join(", ", edges.Select(e => e.Curve.GetType().Name))}), volume {KernelTests.Vex(s):0.####}");
        var errors = new List<string>();
        NBoolean.Trace = Environment.GetEnvironmentVariable("FC_TRACE") != null ? Console.WriteLine : null;
        Prof.Enabled = Environment.GetEnvironmentVariable("FC_PROF") != null;
        if (Environment.GetEnvironmentVariable("FC_TOPO") != null) Topology.Debug = Console.WriteLine;
        var sw = Stopwatch.StartNew();
        var res = NativeBlend.Apply(s, NativeBlend.Expand(s.Native!, edges, true), r, true, "Fillet", errors);
        var ms = sw.ElapsedMilliseconds;
        foreach (var e in errors) Console.WriteLine("  error: " + e);
        if (Prof.Enabled) foreach (var (pn, pms, calls) in Prof.Results().OrderByDescending(x => x.ms).Take(15)) Console.WriteLine($"    {pn,-40} {pms,9:0.0} ms {calls,7}");
        Console.WriteLine($"  {ms} ms, native={res.Native != null}, open half-edges={res.Topology.OpenHalfEdges}, volume {KernelTests.Vex(res):0.####} (fallback: {Csg.LastFallbackReason})");
        if (res.Native != null)
        {
            if (Environment.GetEnvironmentVariable("FC_FINEVOL") != null)
                foreach (var tol in new[] { 1e-3, 1e-4, 1e-5 })
                    Console.WriteLine($"  volume at {tol:g}: before {NTess.ToSolid(s.Native!, tol).Volume():0.#####}, after {NTess.ToSolid(res.Native, tol).Volume():0.#####}");
            Console.WriteLine($"  validate: {res.Native.Validate() ?? "ok"}; faces {res.Native.Faces.Count}: {string.Join(", ", res.Native.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
        }
        if (args.Length > 3)
        {
            using var fs = File.Create(args[3]);
            StepWriter.Write(fs, new[] { ("body", res) }, name);
            Console.WriteLine("  STEP " + (StepWriter.LastError?.Message ?? "ok"));
        }
        return errors.Count == 0 ? 0 : 1;
    }
}
