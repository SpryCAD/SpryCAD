using System.Diagnostics;
using SpryCad.Core.Assembly;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- engine-drag [file|-] [only]</c>: the hit and miss engine dragged by hand, as the viewport's <c>AssemblyMode</c> does it
/// (the grabbed component moved with the cursor in the view's plane, then the mates solved with the grabbed point
/// pulled toward the cursor's ray): the flywheel turned round at 2 to 90 degrees a mouse move, from a view along the
/// crank's axis and from an oblique one, both ways; the cursor swept straight across the flywheel; the piston pulled
/// along its bore past both ends of its stroke; the connecting rod swung round; the flywheel jerked to random places.
/// Checked after each move: every mate holds, the crankshaft stays on its axis, the piston where the kinematics put
/// it for the crank's angle (on its side of the crank); timed.
/// </summary>
static class EngineDrag
{
    const double R = 30, Rod = 130; // the demo's crank throw and rod length
    /// <summary>Only the drags whose description holds this.</summary>
    static string? _only;

    public static int Run(string[] args)
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "samples"))) root = Path.GetDirectoryName(root)!;
        var path = args.Length > 0 && args[0] != "-" ? args[0] : Path.Combine(root, "samples", "demos", "hit-and-miss.sasm");
        _only = args.Length > 1 ? args[1] : null;
        int bad = 0;
        var obliqueView = Transform.Rotation(Vec3.Zero, Vec3.UnitY, 35 * Math.PI / 180) * Transform.Rotation(Vec3.Zero, Vec3.UnitX, -20 * Math.PI / 180);
        foreach (var deg in new[] { 2.0, 6, 15, 30, 60, 90 })
            foreach (var oblique in new[] { false, true })
                foreach (var sense in new[] { 1, -1 })
                {
                    int n = (int)Math.Round(720 / deg);
                    bad += Drag(path, $"flywheel {deg,2} deg/move {(oblique ? "oblique" : "on axis")} {(sense > 0 ? "ccw" : "cw ")}", "Crankshaft assembly",
                        new Vec3(180, 0, 100), oblique ? obliqueView : Transform.Identity, n,
                        (i, g0) => Transform.Rotation(Vec3.Zero, Vec3.UnitZ, sense * deg * i * Math.PI / 180).Point(g0), turns: 2);
                }
        // straight across the wheel's face, over the axis and near it
        foreach (var y in new[] { 150.0, 20 })
            bad += Drag(path, $"flywheel swept across at y {y}", "Crankshaft assembly", new Vec3(180, 0, 100), Transform.Identity, 120,
                (i, g0) => new Vec3(300 * Math.Cos(i * Math.PI / 30), y, g0.Z));
        // the flywheel jerked about: the cursor anywhere within 400 of the axis each move
        var rnd = new Random(7);
        var jumps = Enumerable.Range(0, 200).Select(_ => new Vec3(800 * rnd.NextDouble() - 400, 800 * rnd.NextDouble() - 400, 0)).ToArray();
        bad += Drag(path, "flywheel jerked about", "Crankshaft assembly", new Vec3(180, 0, 100), Transform.Identity, jumps.Length,
            (i, g0) => jumps[i - 1] + Vec3.UnitZ * g0.Z);
        // the piston pulled along its bore, past either end of its stroke (-160 .. -100) and back, slowly and fast
        foreach (var stepMm in new[] { 2.0, 25 })
        {
            int n = (int)(4 * 160 / stepMm);
            bad += Drag(path, $"piston along its bore, {stepMm} mm/move", "Piston assembly", Vec3.Zero, Transform.Rotation(Vec3.Zero, Vec3.UnitX, -0.3), n,
                (i, g0) => new Vec3(-130 + 80 * Math.Sin(2 * Math.PI * i * stepMm / 320), g0.Y, g0.Z));
        }
        // the rod's middle swung round a circle about the crank
        bad += Drag(path, "rod swung round", "Connecting rod assembly", null, Transform.Identity, 180,
            (i, g0) => Transform.Rotation(Vec3.Zero, Vec3.UnitZ, i * 4 * Math.PI / 180).Point(g0));
        return bad;
    }

    /// <summary>
    /// One drag: the component whose name starts with <paramref name="name"/> grabbed at <paramref name="grabLocal"/> (in
    /// it; null: halfway along the rod), seen through <paramref name="view"/> (its Z toward the eye), the cursor put
    /// over <paramref name="cursor"/>(move, grabbed point at the start) for each of <paramref name="n"/> moves.
    /// </summary>
    static int Drag(string path, string what, string name, Vec3? grabLocal, Transform view, int n, Func<int, Vec3, Vec3> cursor, int turns = 0)
    {
        if (_only != null && !what.Contains(_only)) return 0;
        var m = AssemblyModel.Open(AssemblyDocument.Load(path), path);
        m.SolveMates();
        string Id(string s) => m.Doc.Components.First(c => c.Name.StartsWith(s)).Id;
        var crank = Id("Crankshaft assembly"); var piston = Id("Piston assembly"); var dragged = Id(name);
        double Angle() { var x = m.WorldOf(crank).Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
        double PistonX(double th) => R * Math.Cos(th) - Math.Sqrt(Rod * Rod - R * R * Math.Sin(th) * Math.Sin(th));
        var crank0 = m.WorldOf(crank);
        // the rod's middle: between the crank pin and the wrist pin
        var pinAt = new Vec3(R * Math.Cos(Angle()), R * Math.Sin(Angle()), 0);
        var g = grabLocal is { } gl ? m.WorldOf(dragged).Point(gl) : (pinAt + m.WorldOf(piston).T) / 2 + new Vec3(0, 0, crank0.T.Z);
        var g0 = g;
        var back = view.Vector(Vec3.UnitZ).Normalized; // toward the eye
        var normal = -back;
        double lag = 0, worstLag = 0, worstMate = 0, worstAxis = 0, worstPiston = 0, worstMs = 0, totalMs = 0, turned = 0, a0 = Angle();
        int flips = 0, firstFlip = -1, failed = 0, iters = 0;
        var sw = new Stopwatch();
        for (int i = 1; i <= n; i++)
        {
            var target = cursor(i, g0);
            var o = target + back * 2000; var d = -back;
            sw.Restart();
            // AssemblyMode.PointerMove
            var t = m.WorldOf(dragged);
            var hit = o + d * (Vec3.Dot(g - o, normal) / Vec3.Dot(d, normal));
            var pull = new MateSolver.Pull(dragged, t.RigidInverse().Point(g), o, d, hit - g);
            var before = t;
            var res = m.SolveDrag(pull);
            iters += res.Iterations;
            g = m.WorldOf(dragged).Point(before.RigidInverse().Point(g));
            // how far the grabbed point is left off the cursor's ray
            var off2 = g - o; off2 -= d * Vec3.Dot(off2, d);
            lag += off2.Length; worstLag = Math.Max(worstLag, off2.Length);
            var ms = sw.Elapsed.TotalMilliseconds;
            totalMs += ms; worstMs = Math.Max(worstMs, ms);
            var err = res.Errors.Values.DefaultIfEmpty(0).Max();
            worstMate = Math.Max(worstMate, err);
            if (err > 1e-6) failed++;
            var tc = m.WorldOf(crank);
            var off = tc.T - crank0.T; off -= Vec3.UnitZ * Vec3.Dot(off, Vec3.UnitZ);
            worstAxis = Math.Max(worstAxis, Math.Max(off.Length, Vec3.Cross(tc.Vector(Vec3.UnitZ), Vec3.UnitZ).Length * 100));
            var e = Math.Abs(m.WorldOf(piston).T.X - PistonX(Angle()));
            worstPiston = Math.Max(worstPiston, e);
            if (e > 1) { flips++; if (firstFlip < 0) firstFlip = i; }
            turned += Math.IEEERemainder(Angle() - a0, 2 * Math.PI); a0 = Angle();
        }
        bool ok = worstMate < 1e-6 && worstAxis < 1e-4 && flips == 0 && (turns == 0 || Math.Abs(Math.Abs(turned) - turns * 2 * Math.PI) < 0.01);
        Console.WriteLine($"{what,-38} {n,4} moves, turned {turned * 180 / Math.PI,7:0.0}; worst mate {worstMate:E1}{(failed > 0 ? $" ({failed} failed)" : "")}, " +
                          $"off axis {worstAxis:E1}, piston off {worstPiston:0.###}{(flips > 0 ? $" ({flips} flipped, first at {firstFlip})" : "")}; " +
                          $"lag {lag / n:0.##} (worst {worstLag:0.#}); {(double)iters / n:0.0} it/move, {totalMs / n:0.0} ms/move, worst {worstMs:0.0}  {(ok ? "ok" : "BAD")}");
        return ok ? 0 : 1;
    }
}
