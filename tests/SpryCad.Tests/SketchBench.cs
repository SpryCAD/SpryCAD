using System.Diagnostics;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Sketch solver timing: what a user waits for in a sketch (a solve with its DOF analysis, a drag step, the
/// over-define check of a new relation), on the sample parts' sketches and on synthetic grids of dimensioned
/// rectangles and slots of growing size. <c>-- sketchbench [samples dir] [rounds]</c>.
/// </summary>
static class SketchBench
{
    static int P(Sketch sk, double x, double y) => sk.AddPoint(new Vec2(x, y)).Id;

    static void Dim(Sketch sk, CType type, double value, params int[] refs)
    {
        var c = sk.AddConstraint(type, refs);
        c.Value = value; c.Expr = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SketchSolver.CaptureSign(sk, c);
    }

    /// <summary>k × k rectangles, each horizontal/vertical with its width and height, placed from the one before.</summary>
    internal static Sketch Grid(int k)
    {
        var sk = new Sketch();
        int prev = Sketch.OriginId;
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
            {
                double x0 = 10 + i * 40, y0 = 10 + j * 30;
                int a = P(sk, x0 + 0.3, y0), b = P(sk, x0 + 25, y0 + 0.2), c = P(sk, x0 + 25.4, y0 + 15), d = P(sk, x0, y0 + 15.3);
                var l1 = sk.Add(new SkLine { P1 = a, P2 = b }); var l2 = sk.Add(new SkLine { P1 = b, P2 = c });
                var l3 = sk.Add(new SkLine { P1 = c, P2 = d }); var l4 = sk.Add(new SkLine { P1 = d, P2 = a });
                sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
                sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
                Dim(sk, CType.HDistance, 25, l1.Id);
                Dim(sk, CType.VDistance, 15, l2.Id);
                Dim(sk, CType.HDistance, j == 0 ? 10 + i * 40 : 0, prev, a);
                Dim(sk, CType.VDistance, j == 0 ? 10 : 30, prev, a);
                prev = a;
                if (j == k - 1) prev = Sketch.OriginId;
            }
        return sk;
    }

    static List<(string name, Sketch sk)> SampleSketches(string dir)
    {
        var res = new List<(string, Sketch)>();
        foreach (var file in Directory.GetFiles(dir, "*.sprt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = CadDocument.Load(file);
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features.OfType<SketchFeature>())
                if (r.State.Sketches.GetValueOrDefault(f.Id)?.Sketch is { } sk)
                    res.Add(($"{Path.GetFileNameWithoutExtension(file)}/{f.Name}", sk));
        }
        return res;
    }

    // a hash of every result: the points' and radii's bits after each action (compare builds)
    static ulong _hash = 14695981039346656037;
    static void Mix(double x) => _hash = (_hash ^ (ulong)BitConverter.DoubleToInt64Bits(x)) * 1099511628211;

    /// <summary>Best of the rounds, in ms, of an action on a fresh copy of the sketch each time.</summary>
    static double Time(Sketch sk, int rounds, Action<Sketch> act)
    {
        double best = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            var copy = sk.Clone();
            var sw = Stopwatch.StartNew();
            act(copy);
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            if (r == 0)
            {
                foreach (var p in copy.Points) { Mix(p.X); Mix(p.Y); }
                foreach (var c in copy.Entities.OfType<SkCircle>()) Mix(c.R);
            }
        }
        return best;
    }

    static (double solve, double analyze, double drag, double over) Measure(Sketch sk, int rounds)
    {
        var solve = Time(sk, rounds, s => SketchSolver.Solve(s));
        var analyze = Time(sk, rounds, s => SketchSolver.Analyze(s));
        // a drag: ten small moves of a free point (as the pointer sends them), no analysis between
        var free = SketchSolver.Analyze(sk).FreePoints.FirstOrDefault(-1);
        if (free < 0) free = sk.Points.FirstOrDefault(p => !p.Fixed)?.Id ?? -1;
        var drag = free < 0 ? 0 : Time(sk, rounds, s =>
        {
            var at = s.P(free);
            for (int i = 1; i <= 10; i++) SketchSolver.Solve(s, new Dictionary<int, Vec2> { [free] = at + new Vec2(0.3 * i, 0.2 * i) }, analyze: false);
        });
        // the over-define check of a new relation (a horizontal on the first line)
        var line = sk.Entities.OfType<SkLine>().FirstOrDefault();
        var over = line == null ? 0 : Time(sk, rounds, s => Mix(SketchSolver.WouldOverDefine(s, new SkConstraint { Id = 999999, Type = CType.Horizontal, Refs = new[] { line.Id } }) ? 1 : 0));
        var an = SketchSolver.Analyze(sk);
        Mix(an.Dof); Mix(an.Residual); foreach (var p in an.FreePoints.Order()) Mix(p);
        return (solve, analyze, drag, over);
    }

    /// <summary>
    /// Ten frames of the lookups drawing does: each entity's points, and what each relation refers to (an entity,
    /// else a point).
    /// </summary>
    static double Frames(Sketch sk, int rounds) => Time(sk, rounds, s =>
    {
        double acc = 0;
        for (int f = 0; f < 10; f++)
        {
            foreach (var e in s.Entities) foreach (var p in e.PointIds) acc += s.P(p).X;
            foreach (var c in s.Constraints) foreach (var r in c.Refs) acc += s.Entity(r) != null ? 1 : s.Point(r) != null ? 2 : 0;
        }
        Mix(acc);
    });

    public static int Run(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : Path.Combine("..", "..", "samples");
        int rounds = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 3;
        Console.WriteLine($"{"sketch",-44} {"cons",5} {"solve",9} {"analyze",9} {"drag x10",9} {"overdef",9} {"frames",9}  (ms, best of {rounds})");
        double ts = 0, ta = 0, td = 0, to = 0, tf = 0;
        foreach (var (name, sk) in SampleSketches(dir))
        {
            var (s, a, d, o) = Measure(sk, rounds);
            var fr = Frames(sk, rounds);
            ts += s; ta += a; td += d; to += o; tf += fr;
            if (sk.Constraints.Count >= 20) Console.WriteLine($"{name,-44} {sk.Constraints.Count,5} {s,9:0.00} {a,9:0.00} {d,9:0.00} {o,9:0.00} {fr,9:0.00}");
        }
        Console.WriteLine($"{"all sample sketches",-44} {"",5} {ts,9:0.00} {ta,9:0.00} {td,9:0.00} {to,9:0.00} {tf,9:0.00}");
        var sizes = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : new[] { 3, 6, 10 };
        foreach (var k in sizes)
        {
            var sk = Grid(k);
            var (s, a, d, o) = Measure(sk, rounds);
            var fr = Frames(sk, rounds);
            Console.WriteLine($"{$"grid {k}x{k} rectangles",-44} {sk.Constraints.Count,5} {s,9:0.00} {a,9:0.00} {d,9:0.00} {o,9:0.00} {fr,9:0.00}");
        }
        Console.WriteLine($"results hash {_hash:X16}");
        return 0;
    }
}
