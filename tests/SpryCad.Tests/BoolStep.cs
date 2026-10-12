using System.Diagnostics;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- boolstep dir [copy]</c>: one copy of an <c>occtprep</c> export's tool cut from its body, on the geometry read
/// from the STEP files, each step timed and the boolean's stages logged as they finish (to find where it stalls).
/// </summary>
static class BoolStep
{
    public static int Run(string dir, int copy)
    {
        var sw = Stopwatch.StartNew();
        NBody Read(string f, string prefix) => StepImport.Read(File.ReadAllBytes(Path.Combine(dir, f)), prefix).Solids.Single().Body;
        var body = Read("body.step", "B."); var tool = Read("tool.step", "T.");
        Console.WriteLine($"read: {sw.ElapsedMilliseconds} ms; body {body.Faces.Count} faces ({string.Join(", ", body.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Count()} {g.Key}"))}), tool {tool.Faces.Count} ({string.Join(", ", tool.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Count()} {g.Key}"))})");
        if (Environment.GetEnvironmentVariable("BOOLSTEP_RECOG") != null)
            foreach (var f in body.Faces.Concat(tool.Faces).Where(f => f.Surface.Kind == SurfaceKind.Screw))
            {
                var sc = f.Surface.Screw!; var mo = sc.Motion;
                Console.WriteLine($"  {f.Tag.Name}: screw O {mo.O.X:R} {mo.O.Y:R} {mo.O.Z:R} A {mo.A.X:R} {mo.A.Y:R} {mo.A.Z:R} lead {mo.Lead:R}, {sc.Gen.GetType().Name} {sc.Gen.Eval(sc.V0)}..{sc.Gen.Eval(sc.V1)}, u {sc.U0:0.###}..{sc.U1:0.###}");
            }
        if (Environment.GetEnvironmentVariable("BOOLSTEP_RECOG") != null)
            foreach (var f in body.Faces.Concat(tool.Faces).Where(f => f.Surface.Kind == SurfaceKind.Cylinder).GroupBy(f => f.Surface).Select(g => g.First()))
                Console.WriteLine($"  {f.Tag.Name}: cylinder axis {f.Surface.Def.Axis.X:R} {f.Surface.Def.Axis.Y:R} {f.Surface.Def.Axis.Z:R} origin {f.Surface.Def.Origin.X:R} {f.Surface.Def.Origin.Y:R} {f.Surface.Def.Origin.Z:R} r {f.Surface.Def.Radius:R}");
        if (Environment.GetEnvironmentVariable("BOOLSTEP_RECOG") != null)
            foreach (var f in body.Faces.Concat(tool.Faces))
                if (f.Surface.Nurbs is { } nb)
                {
                    double lo = double.MaxValue, hi = 0, nz = 0, nr = 1;
                    for (int i = 0; i <= 400; i++) for (int j = 0; j <= 20; j++)
                    {
                        double u = nb.U0 + (nb.U1 - nb.U0) * i / 400, v = nb.V0 + (nb.V1 - nb.V0) * j / 20;
                        var q = nb.Eval(u, v); var n = nb.Normal(u, v);
                        var r = Math.Sqrt(q.X * q.X + q.Y * q.Y); lo = Math.Min(lo, r); hi = Math.Max(hi, r);
                        nz = Math.Max(nz, Math.Abs(n.Z)); nr = Math.Min(nr, Math.Abs(n.X * q.X + n.Y * q.Y) / r);
                    }
                    Console.WriteLine($"  {f.Tag.Name}: {nb.P.GetLength(0)}x{nb.P.GetLength(1)}, r {lo:0.######}..{hi:0.######} (spread {hi - lo:g3}), max |n.z| {nz:g3}, min radial {nr:g6}");
                }
        Console.WriteLine($"volumes: body {NMass.Of(body)?.Volume:R} ({body.Validate() ?? "valid"}), tool {NMass.Of(tool)?.Volume:R} ({tool.Validate() ?? "valid"})");
        var m = File.ReadAllLines(Path.Combine(dir, "transforms.txt"))[copy].Split(' ').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var t = new Transform(m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], new Vec3(m[3], m[7], m[11]));
        sw.Restart();
        var moved = tool.Transformed(t, "P.");
        Console.WriteLine($"moved: {sw.ElapsedMilliseconds} ms");
        if (Environment.GetEnvironmentVariable("BOOLSTEP_PAIR") is { } pair)
        {
            // one face pair traced on its own, profiled: "bodyFace|toolFace"
            var names = pair.Split('|');
            body.Link(); moved.Link();
            var fa = body.Faces.First(f => f.Tag.Name == names[0]); var fb = moved.Faces.First(f => f.Tag.Name == names[1]);
            var scale = Math.Max(10, Math.Min(body.Bounds.Diagonal, moved.Bounds.Diagonal));
            var tol = 2e-6 * scale * 50;
            foreach (var f in new[] { fa, fb })
                if (f.Surface.Nurbs is { } nb)
                {
                    var rs = new List<double>();
                    foreach (var q in nb.P) rs.Add(Math.Sqrt(q.X * q.X + q.Y * q.Y));
                    Console.WriteLine($"{f.Tag.Name}: {nb.P.GetLength(0)}x{nb.P.GetLength(1)} control points, radius from Z {rs.Min():0.####} .. {rs.Max():0.####}");
                    double lo = double.MaxValue, hi = 0;
                    for (int i = 0; i <= 200; i++) for (int j = 0; j <= 20; j++)
                    {
                        var q = nb.Eval(nb.U0 + (nb.U1 - nb.U0) * i / 200, nb.V0 + (nb.V1 - nb.V0) * j / 20);
                        var r = Math.Sqrt(q.X * q.X + q.Y * q.Y); lo = Math.Min(lo, r); hi = Math.Max(hi, r);
                    }
                    Console.WriteLine($"   surface radius from Z {lo:R} .. {hi:R} (spread {hi - lo:g3})");
                }
            Prof.Enabled = true; Prof.Reset();
            sw.Restart();
            var curves = NIntersect.Trace(fa, fb.Surface, tol, NDomain.Of(fa, Math.Max(tol, 1e-3)));
            Console.WriteLine($"trace {names[0]} x {names[1]}: {curves.Count} curves, {curves.Sum(c => c.Pts.Length)} points, {sw.ElapsedMilliseconds} ms (tol {tol:g3})");
            foreach (var (n, ms, calls) in Prof.Results().Take(20)) Console.WriteLine($"  {n,-30} {ms,9:0.0} ms x{calls}");
            return 0;
        }
        NBoolean.Trace = s => { if (Environment.GetEnvironmentVariable("BOOLSTEP_GREP") is { } g ? s.Contains(g) : s.StartsWith("[") || s.StartsWith("  trace") || s.StartsWith("sections") || s.StartsWith("pieces") || s.StartsWith("kept")) Console.WriteLine("  " + s); };
        Prof.Enabled = true;
        sw.Restart();
        var res = NBoolean.Run(body, moved, BoolOp.Subtract);
        Console.WriteLine($"subtract: {sw.ElapsedMilliseconds} ms, {res.Faces.Count} faces, volume {NMass.Of(res)?.Volume:R}, {res.Validate() ?? "valid"}");
        foreach (var (n, ms, calls) in Prof.Results().Take(80)) Console.WriteLine($"  {n,-30} {ms,9:0.0} ms x{calls}");
        return 0;
    }
}
