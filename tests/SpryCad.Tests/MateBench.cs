using System.Diagnostics;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- matebench [sizes]</c>: how long the mates take to solve as assemblies grow. A stack of the demo spur gear
/// (hundreds of faces, as real parts have): each gear concentric with the one below and its start face on that
/// one's end, the first fixed. Timed: every gear knocked out of place and solved back (opening, a big edit), and
/// the top gear dragged a little (what each mouse move while dragging costs).
/// </summary>
static class MateBench
{
    public static int Run(string[] args)
    {
        var sizes = args.Length > 0 ? args.Select(int.Parse).ToArray() : new[] { 10, 25, 50, 100, 200 };
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "samples"))) root = Path.GetDirectoryName(root)!;
        var path = Path.Combine(root, "samples", "demos", "gears.sasm");
        var gears = AssemblyModel.Open(AssemblyDocument.Load(path), path);
        var part = gears.Parts.Values.First(p => p.Doc != null).Doc!;
        Console.WriteLine($"{"comps",6} {"mates",6} {"faces",6} {"solve ms",9} {"iters",6} {"drag ms",8} {"iters",6} {"ok",3}");
        foreach (var n in sizes)
        {
            var m = AssemblyModel.New();
            var comps = new List<Component>();
            for (int i = 0; i < n; i++) comps.Add(m.InsertEmbedded(part, "Gear", Transform.Translation(new Vec3(0, 0, i * 10))));
            comps[0].Fixed = true;
            for (int i = 1; i < n; i++) comps[i].Fixed = false;
            var faces = m.DisplayOf(comps[0])!.Mesh.Faces;
            string bore = faces.First(f => f.Name == LegacyIds.Name(part.Id!, "F2.S.C3")).Name;
            // the gear's two ends: its lowest face looking down (-Z), its highest looking up
            var planes = faces.Where(f => f.Surface.Kind == SurfaceKind.Plane).ToList();
            string start = planes.Where(f => f.FirstPlane.Normal.Z < -0.9).MinBy(f => f.FirstPlane.Origin.Z)!.Name;
            string end = planes.Where(f => f.FirstPlane.Normal.Z > 0.9).MaxBy(f => f.FirstPlane.Origin.Z)!.Name;
            for (int i = 1; i < n; i++)
            {
                m.Doc.AddMate(Mate(m, MateType.Concentric, comps[i], bore, comps[i - 1], bore));
                m.Doc.AddMate(Mate(m, MateType.Coincident, comps[i], start, comps[i - 1], end));
            }
            m.SolveMates();
            // every floating gear knocked a little out of place, then solved back
            var rnd = new Random(1);
            foreach (var c in comps.Skip(1))
                c.Placement = Placement.From(Transform.Translation(new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5))
                    * Transform.Rotation(c.Placement.ToTransform().T, new Vec3(rnd.NextDouble(), rnd.NextDouble(), 1), 0.05 * rnd.NextDouble()) * c.Placement.ToTransform());
            var sw = Stopwatch.StartNew();
            var full = Environment.GetEnvironmentVariable("MATE_ITERS") is { } mi ? MateSolver.Solve(m.Doc, m.Local, null, int.Parse(mi)) : m.SolveMates();
            var solveMs = sw.Elapsed.TotalMilliseconds;
            // the top gear dragged 2 mm sideways: everything solved again (as each mouse move does)
            var top = comps[^1];
            top.Placement = Placement.From(Transform.Translation(new Vec3(2, 0, 0)) * top.Placement.ToTransform());
            sw.Restart();
            var drag = m.SolveMates();
            var dragMs = sw.Elapsed.TotalMilliseconds;
            if (!(full.Satisfied && drag.Satisfied)) Console.WriteLine($"   worst error left: {full.Errors.Values.Max():E2} (solve), {drag.Errors.Values.Max():E2} (drag)");
            Console.WriteLine($"{n,6} {m.Doc.Mates.Count,6} {faces.Count,6} {solveMs,9:0.0} {full.Iterations,6} {dragMs,8:0.0} {drag.Iterations,6} {(full.Satisfied && drag.Satisfied ? "yes" : "NO"),3}");
        }
        return 0;
    }

    static Mate Mate(AssemblyModel m, MateType t, Component a, string fa, Component b, string fb)
    {
        var mate = new Mate { Type = t, A = m.RefTo(a.Id, fa)!, B = m.RefTo(b.Id, fb)! };
        mate.Capture(m.World(mate.A), m.World(mate.B), m.PlacementOf(mate.A), m.PlacementOf(mate.B));
        return mate;
    }
}
