using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Drags every point and line of typical sketches (as the drawing tools make them) along mouse-like paths and
/// reports what a user would see as odd: relations left unsatisfied, fixed geometry moving, lines or radii shrinking
/// to nothing, arcs turning into their complement, a circle's radius going negative, geometry jumping far for a small
/// move, drags that do not follow the pointer, and sketches no longer solved when the drag ends.
/// <c>-- sketchfuzz [seed] [verbose]</c>; <see cref="Check"/> runs it as a test.
/// </summary>
static class SketchFuzz
{
    sealed record Scenario(string Name, Func<Sketch> Build);

    static int P(Sketch sk, double x, double y) => sk.AddPoint(new Vec2(x, y)).Id;
    static SkLine L(Sketch sk, int a, int b, bool construction = false) => sk.Add(new SkLine { P1 = a, P2 = b, Construction = construction });
    static void Tan(Sketch sk, SkEntity a, SkEntity b) => SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, a.Id, b.Id));

    static SkConstraint Dim(Sketch sk, CType type, double value, params int[] refs)
    {
        var c = sk.AddConstraint(type, refs);
        c.Value = value; c.Expr = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SketchSolver.CaptureSign(sk, c);
        return c;
    }

    static Sketch Rect(double x0, double y0, double x1, double y1, out SkLine[] sides, out int[] corners, int first = -1)
    {
        var sk = new Sketch();
        int a = first >= 0 ? first : P(sk, x0, y0), b = P(sk, x1, y0), c = P(sk, x1, y1), d = P(sk, x0, y1);
        var l1 = L(sk, a, b); var l2 = L(sk, b, c); var l3 = L(sk, c, d); var l4 = L(sk, d, a);
        sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
        sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
        sides = new[] { l1, l2, l3, l4 }; corners = new[] { a, b, c, d };
        return sk;
    }

    static Sketch Slot(Vec2 a, Vec2 b, double r, bool fromOrigin, bool horizontal)
    {
        var sk = new Sketch();
        var u = (b - a).Normalized; var n = u.Perp;
        int ca = fromOrigin ? Sketch.OriginId : P(sk, a.X, a.Y), cb = P(sk, b.X, b.Y);
        var axis = L(sk, ca, cb, true);
        if (horizontal) sk.AddConstraint(CType.Horizontal, axis.Id);
        var A = fromOrigin ? Vec2.Zero : a;
        int p1 = P(sk, (A + n * r).X, (A + n * r).Y), p2 = P(sk, (b + n * r).X, (b + n * r).Y), p3 = P(sk, (b - n * r).X, (b - n * r).Y), p4 = P(sk, (A - n * r).X, (A - n * r).Y);
        var top = L(sk, p1, p2); var bot = L(sk, p3, p4);
        var arcB = sk.Add(new SkArc { Center = cb, Start = p3, End = p2 });
        var arcA = sk.Add(new SkArc { Center = ca, Start = p1, End = p4 });
        Tan(sk, top, arcB); Tan(sk, bot, arcA); Tan(sk, top, arcA); Tan(sk, bot, arcB);
        sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
        return sk;
    }

    static IEnumerable<Scenario> Scenarios()
    {
        yield return new("rectangle", () => Rect(10, 10, 80, 50, out _, out _));
        yield return new("rectangle from origin", () => Rect(0, 0, 80, 50, out _, out _, Sketch.OriginId));
        yield return new("rectangle, width and height", () =>
        {
            var sk = Rect(10, 10, 80, 50, out var s, out _);
            Dim(sk, CType.Distance, 70, s[0].Id); Dim(sk, CType.Distance, 40, s[1].Id);
            return sk;
        });
        yield return new("rectangle, fully defined", () =>
        {
            var sk = Rect(0, 0, 80, 50, out var s, out _, Sketch.OriginId);
            Dim(sk, CType.Distance, 80, s[0].Id); Dim(sk, CType.Distance, 50, s[1].Id);
            return sk;
        });
        yield return new("center rectangle", () =>
        {
            var sk = new Sketch();
            int c = P(sk, 40, 30), a = P(sk, 10, 10), b = P(sk, 70, 10), cc = P(sk, 70, 50), d = P(sk, 10, 50);
            var l1 = L(sk, a, b); var l2 = L(sk, b, cc); var l3 = L(sk, cc, d); var l4 = L(sk, d, a);
            sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
            sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
            var d1 = L(sk, a, cc, true); var d2 = L(sk, b, d, true);
            sk.AddConstraint(CType.Midpoint, c, d1.Id); sk.AddConstraint(CType.Midpoint, c, d2.Id);
            return sk;
        });
        yield return new("slot", () => Slot(new Vec2(10, 10), new Vec2(80, 30), 12, false, false));
        yield return new("slot from origin, horizontal", () => Slot(Vec2.Zero, new Vec2(90, 0), 15, true, true));
        yield return new("filleted corner", () =>
        {
            // two lines meeting at a corner, rounded: the arc tangent to both at its ends
            var sk = new Sketch();
            int a = P(sk, 0, 60), t1 = P(sk, 0, 10), t2 = P(sk, 10, 0), b = P(sk, 70, 0), c = P(sk, 10, 10);
            var l1 = L(sk, a, t1); var l2 = L(sk, t2, b);
            var arc = sk.Add(new SkArc { Center = c, Start = t1, End = t2 });
            sk.AddConstraint(CType.Vertical, l1.Id); sk.AddConstraint(CType.Horizontal, l2.Id);
            Tan(sk, l1, arc); Tan(sk, l2, arc);
            return sk;
        });
        yield return new("line and tangent arc", () =>
        {
            var sk = new Sketch();
            int a = P(sk, 0, 0), b = P(sk, 50, 0), c = P(sk, 50, 20), e = P(sk, 70, 20);
            var l = L(sk, a, b);
            var arc = sk.Add(new SkArc { Center = c, Start = b, End = e });
            Tan(sk, l, arc);
            return sk;
        });
        yield return new("line tangent to a circle", () =>
        {
            var sk = new Sketch();
            int c = P(sk, 40, 20), a = P(sk, 0, 0), b = P(sk, 80, 0);
            var ci = sk.Add(new SkCircle { Center = c, R = 20 });
            var l = L(sk, a, b);
            Tan(sk, l, ci);
            return sk;
        });
        yield return new("two tangent circles", () =>
        {
            var sk = new Sketch();
            int c1 = P(sk, 0, 0), c2 = P(sk, 50, 0);
            var a = sk.Add(new SkCircle { Center = c1, R = 20 }); var b = sk.Add(new SkCircle { Center = c2, R = 30 });
            Tan(sk, a, b);
            return sk;
        });
        yield return new("hexagon", () =>
        {
            // the Polygon tool: corners on a construction circle, sides equal
            var sk = new Sketch();
            int c = P(sk, 0, 0);
            var circle = sk.Add(new SkCircle { Center = c, R = 30, Construction = true });
            var pts = Enumerable.Range(0, 6).Select(i => P(sk, 30 * Math.Cos(i * Math.PI / 3), 30 * Math.Sin(i * Math.PI / 3))).ToArray();
            var sides = Enumerable.Range(0, 6).Select(i => L(sk, pts[i], pts[(i + 1) % 6])).ToArray();
            foreach (var p in pts) sk.AddConstraint(CType.PointOnCircle, p, circle.Id);
            for (int i = 1; i < 6; i++) sk.AddConstraint(CType.Equal, sides[0].Id, sides[i].Id);
            return sk;
        });
        yield return new("triangle with an angle", () =>
        {
            var sk = new Sketch();
            int a = P(sk, 0, 0), b = P(sk, 60, 0), c = P(sk, 20, 40);
            var l1 = L(sk, a, b); var l2 = L(sk, b, c); var l3 = L(sk, c, a);
            sk.AddConstraint(CType.Horizontal, l1.Id);
            Dim(sk, CType.Angle, SketchSolver.Measure(sk, new SkConstraint { Type = CType.Angle, Refs = new[] { l1.Id, l3.Id } }), l1.Id, l3.Id);
            return sk;
        });
        yield return new("center arc", () =>
        {
            var sk = new Sketch();
            int c = P(sk, 0, 0), s = P(sk, 30, 0), e = P(sk, 0, 30);
            sk.Add(new SkArc { Center = c, Start = s, End = e });
            return sk;
        });
        yield return new("arc with a radius", () =>
        {
            var sk = new Sketch();
            int c = P(sk, 0, 0), s = P(sk, 30, 0), e = P(sk, -21.2, 21.2);
            var arc = sk.Add(new SkArc { Center = c, Start = s, End = e });
            Dim(sk, CType.Radius, 30, arc.Id);
            return sk;
        });
        yield return new("circle with a diameter, on the origin", () =>
        {
            var sk = new Sketch();
            var ci = sk.Add(new SkCircle { Center = Sketch.OriginId, R = 20 });
            Dim(sk, CType.Diameter, 40, ci.Id);
            return sk;
        });
        yield return new("free circle", () =>
        {
            var sk = new Sketch();
            sk.Add(new SkCircle { Center = P(sk, 10, 10), R = 15 });
            return sk;
        });
    }

    /// <summary>One problem seen: what, in which scenario, dragging what.</summary>
    public sealed record Finding(string Scenario, string Dragged, string Kind, string Detail);

    static double Residual(Sketch sk) => SketchSolver.Analyze(sk).Residual;

    /// <summary>An arc's sweep (counterclockwise from start to end), 0..2π.</summary>
    static double Sweep(Sketch sk, SkArc a)
    {
        var c = sk.P(a.Center);
        double s = Math.Atan2(sk.P(a.Start).Y - c.Y, sk.P(a.Start).X - c.X), e = Math.Atan2(sk.P(a.End).Y - c.Y, sk.P(a.End).X - c.X);
        var w = e - s; while (w < 0) w += 2 * Math.PI; while (w >= 2 * Math.PI) w -= 2 * Math.PI;
        return w;
    }

    /// <summary>Each line's length, circle's and arc's radius, and arc's sweep, by entity.</summary>
    static Dictionary<int, (double size, double sweep)> Sizes(Sketch sk) => sk.Entities.ToDictionary(e => e.Id, e => e switch
    {
        SkLine l => (Vec2.Distance(sk.P(l.P1), sk.P(l.P2)), 1.0),
        SkCircle c => (c.R, 1.0),
        SkArc a => (Vec2.Distance(sk.P(a.Start), sk.P(a.Center)), Sweep(sk, a)),
        _ => (1.0, 1.0),
    });

    /// <summary>
    /// Whether the sketch is pressed against a limit of its shape: a line or radius down to a twentieth of what it was,
    /// or an arc nearly closed or nearly gone. A drag held there is right to stop.
    /// </summary>
    static bool NearLimit(Sketch sk, Dictionary<int, (double size, double sweep)> start)
    {
        foreach (var (id, now) in Sizes(sk))
        {
            if (!start.TryGetValue(id, out var was)) continue;
            if (was.size > 1e-9 && now.size < was.size * 0.05) return true;
            if (sk.Entity(id) is SkArc && (now.sweep < 5 * Math.PI / 180 || now.sweep > 2 * Math.PI - 5 * Math.PI / 180)) return true;
        }
        return false;
    }

    public static List<Finding> Run(int seed, Action<string>? log = null)
    {
        var found = new List<Finding>();
        var rng = new Random(seed);
        foreach (var sc in Scenarios())
        {
            var probe = sc.Build();
            SketchSolver.Solve(probe);
            // what is dragged: every free point, then every line (both its ends with the pointer)
            var targets = new List<(string name, int[] pts)>();
            foreach (var p in probe.Points) targets.Add(($"point {p.Id}", new[] { p.Id }));
            foreach (var l in probe.Entities.OfType<SkLine>()) targets.Add(($"line {l.Id}", new[] { l.P1, l.P2 }));
            foreach (var (name, pts) in targets)
            {
                var sk = sc.Build();
                var first = SketchSolver.Solve(sk);
                if (!first.Converged) { found.Add(new(sc.Name, "", "unsolved as drawn", $"residual {first.Residual:g3}")); break; }
                bool fullyDefined = first.Status == SketchStatus.FullyDefined;
                var fixedAt = sk.Points.Where(p => p.Fixed).ToDictionary(p => p.Id, p => p.P);
                var sweeps = sk.Entities.OfType<SkArc>().ToDictionary(a => a.Id, a => Sweep(sk, a));
                var grab = pts.ToDictionary(id => id, id => sk.P(id));
                var scale = Math.Max(10, sk.Points.Max(p => p.P.Length));
                var start = Sizes(sk);
                int stalled = 0, steps = 0;
                void Note(string kind, string detail) { if (!found.Any(f => f.Scenario == sc.Name && f.Dragged == name && f.Kind == kind)) found.Add(new(sc.Name, name, kind, detail)); }
                // three drags: a smooth swing, a jittery path, and quick large moves; each ends with the full solve
                for (int drag = 0; drag < 3; drag++)
                {
                    var origin = grab.ToDictionary(kv => kv.Key, kv => sk.P(kv.Key));
                    var cursor = Vec2.Zero;
                    int n = drag == 2 ? 8 : 60;
                    for (int k = 1; k <= n; k++)
                    {
                        var stepTo = drag switch
                        {
                            0 => new Vec2(Math.Sin(k * 0.08) * scale * 0.9, (1 - Math.Cos(k * 0.11)) * scale * 0.6),
                            1 => cursor + new Vec2(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * scale * 0.08,
                            _ => new Vec2(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * scale * 1.5,
                        };
                        var move = (stepTo - cursor).Length;
                        cursor = stepTo;
                        var before = sk.Points.ToDictionary(p => p.Id, p => p.P);
                        var beforeR = sk.Entities.OfType<SkCircle>().ToDictionary(c => c.Id, c => c.R);
                        SketchSolver.Solve(sk, origin.ToDictionary(kv => kv.Key, kv => kv.Value + cursor), analyze: false);
                        steps++;
                        // relations hold after every step (a refused step leaves the sketch as it was: still holding)
                        var res = Residual(sk);
                        if (res > 1e-6) Note("relations broken during the drag", $"drag {drag}, step {k}: residual {res:g3}");
                        foreach (var (id, at) in fixedAt)
                            if (Vec2.Distance(sk.P(id), at) > 1e-9) Note("fixed point moved", $"point {id} moved {Vec2.Distance(sk.P(id), at):g3}");
                        if (fullyDefined && sk.Points.Any(p => Vec2.Distance(p.P, before[p.Id]) > 1e-7)) Note("fully defined sketch moved", $"drag {drag}, step {k}");
                        foreach (var l in sk.Entities.OfType<SkLine>())
                            if (Vec2.Distance(sk.P(l.P1), sk.P(l.P2)) < 1e-6 && Vec2.Distance(before[l.P1], before[l.P2]) > 1e-6) Note("line shrunk to nothing", $"line {l.Id}, drag {drag}, step {k}");
                        foreach (var c in sk.Entities.OfType<SkCircle>())
                            if (c.R < 1e-6 || (beforeR[c.Id] > 0 && c.R < beforeR[c.Id] * 1e-3)) Note("circle shrunk to nothing", $"circle {c.Id}: {beforeR[c.Id]:g3} -> {c.R:g3}");
                        foreach (var a in sk.Entities.OfType<SkArc>())
                        {
                            var w = Sweep(sk, a);
                            if (Math.Abs(w - sweeps[a.Id]) > Math.PI) Note("arc turned into its complement", $"arc {a.Id}: sweep {sweeps[a.Id] * 180 / Math.PI:0}° -> {w * 180 / Math.PI:0}°, drag {drag}, step {k}");
                            if (Vec2.Distance(sk.P(a.Start), sk.P(a.Center)) < 1e-6) Note("arc shrunk to nothing", $"arc {a.Id}");
                            sweeps[a.Id] = w;
                        }
                        // jumps: a point moving much farther than the pointer did, and than the dragged point had to go to reach it
                        // (a point held at a limit catches up in long moves once it is free: not a jump) (small steps only)
                        if (drag < 2)
                        {
                            var far = sk.Points.Max(p => Vec2.Distance(p.P, before[p.Id]));
                            var gap = pts.Max(id => Vec2.Distance(before[id], origin[id] + cursor));
                            if (far > Math.Max(Math.Max(move * 25, gap * 1.5 + move), scale * 0.25)) Note("geometry jumped", $"drag {drag}, step {k}: a point moved {far:0.#} for a pointer move of {move:0.##}");
                        }
                        // stalls: the dragged point left where it was although the pointer moved, while a short move toward the
                        // pointer would have been taken (at a real limit even that is refused: no stall)
                        var dragged = pts[0];
                        if (!fixedAt.ContainsKey(dragged) && !fullyDefined && move > 1e-6 && Vec2.Distance(sk.P(dragged), before[dragged]) < 1e-9)
                        {
                            var trial = sk.Clone();
                            SketchSolver.Solve(trial, origin.ToDictionary(kv => kv.Key, kv => before[kv.Key] + (kv.Value + cursor - before[kv.Key]) * 0.01), analyze: false);
                            if (Vec2.Distance(trial.P(dragged), before[dragged]) > 1e-9 && !NearLimit(sk, start)) stalled++;
                        }
                    }
                    var end = SketchSolver.Solve(sk);
                    if (!end.Converged) Note("not solved when the drag ends", $"drag {drag}: residual {end.Residual:g3}, {end.Status}");
                }
                if (steps > 0 && stalled > steps / 3) Note("drag does not follow the pointer", $"{stalled} of {steps} moves left the dragged point where it was");
            }
        }
        foreach (var f in found) log?.Invoke($"  {f.Scenario,-38} {f.Dragged,-10} {f.Kind}: {f.Detail}");
        return found;
    }

    /// <summary>The fuzz over a few seeds, reported as a test (one failure listing every finding).</summary>
    public static void Check(TestRunner t)
    {
        t.Test("Sketch drag fuzz: typical sketches dragged every way keep their relations, shape and fixed points", () =>
        {
            var all = new List<Finding>();
            foreach (var seed in new[] { 1, 2, 3 }) all.AddRange(Run(seed));
            var distinct = all.DistinctBy(f => (f.Scenario, f.Dragged, f.Kind)).ToList();
            t.True(distinct.Count == 0, $"{distinct.Count} findings:\n" + string.Join("\n", distinct.Select(f => $"    {f.Scenario} / {f.Dragged}: {f.Kind} ({f.Detail})")));
        });
    }

    public static int Cli(string[] args)
    {
        var seed = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 1;
        Console.WriteLine($"sketch drag fuzz, seed {seed}");
        var found = Run(seed, Console.WriteLine);
        Console.WriteLine($"{found.Count} findings");
        foreach (var g in found.GroupBy(f => f.Kind)) Console.WriteLine($"  {g.Key}: {g.Count()}");
        return found.Count == 0 ? 0 : 1;
    }
}
