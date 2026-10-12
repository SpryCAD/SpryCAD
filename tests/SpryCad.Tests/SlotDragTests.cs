using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Dragging keeps a sketch's shape: tangent lines and arcs do not flip or collapse (a straight slot).</summary>
static class SlotDragTests
{
    /// <summary>A straight slot as the Slot tool makes it: centers A and B, half-width r.</summary>
    static (Sketch sk, long ca, long cb, long[] ends, long top, long bot) Slot(Vec2 a, Vec2 b, double r)
    {
        var sk = new Sketch();
        var u = (b - a).Normalized; var n = u.Perp;
        long ca = sk.AddPoint(a).Id, cb = sk.AddPoint(b).Id;
        var axis = sk.Add(new SkLine { P1 = ca, P2 = cb, Construction = true });
        long p1 = sk.AddPoint(a + n * r).Id, p2 = sk.AddPoint(b + n * r).Id, p3 = sk.AddPoint(b - n * r).Id, p4 = sk.AddPoint(a - n * r).Id;
        var top = sk.Add(new SkLine { P1 = p1, P2 = p2 });
        var bot = sk.Add(new SkLine { P1 = p3, P2 = p4 });
        var arcB = sk.Add(new SkArc { Center = cb, Start = p3, End = p2 });
        var arcA = sk.Add(new SkArc { Center = ca, Start = p1, End = p4 });
        foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
            SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, arc.Id));
        sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
        return (sk, ca, cb, new[] { p1, p2, p3, p4 }, top.Id, bot.Id);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Sketch drag: a slot keeps its shape whatever is dragged (its sides never collapse or flip)", () =>
        {
            int refused = 0;
            // each point dragged in small steps along a path that crosses the slot and swings round it
            for (int which = 0; which < 6; which++)
            {
                var (sk, ca, cb, ends, top, bot) = Slot(new Vec2(0, 0), new Vec2(60, 10), 15);
                SketchSolver.Solve(sk);
                var id = which < 4 ? ends[which] : which == 4 ? ca : cb;
                var start = sk.P(id);
                for (int step = 1; step <= 120; step++)
                {
                    var ang = step * 0.06;
                    var target = start + new Vec2(Math.Sin(ang) * 40, -Math.Sin(ang * 0.7) * 45);
                    var before = sk.P(id);
                    SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [id] = target }, analyze: false);
                    if (Vec2.Distance(sk.P(id), before) < 1e-12 && Vec2.Distance(target, before) > 1e-6) refused++;
                    double Len(long line) { var l = (SkLine)sk.Entity(line)!; return Vec2.Distance(sk.P(l.P1), sk.P(l.P2)); }
                    t.True(Len(top) > 1e-3 && Len(bot) > 1e-3, $"point {which}, step {step}: a side collapsed ({Len(top):0.###}, {Len(bot):0.###})");
                    // both arcs keep their ends on opposite sides of the centerline (the slot not turned inside out)
                    var a = sk.P(ca); var b = sk.P(cb); var d = b - a;
                    double Side(long p) => Vec2.Cross(d, sk.P(p) - a);
                    t.True(Side(ends[0]) * Side(ends[3]) < 0 && Side(ends[1]) * Side(ends[2]) < 0, $"point {which}, step {step}: an arc flipped");
                }
            }
            Console.WriteLine($"      ({refused} drag steps refused to keep the shape)");
        });

        t.Test("Sketch drag: a horizontal slot from the origin keeps its shape, and solves when the drag ends", () =>
        {
            for (int which = 0; which < 5; which++)
            {
                // as drawn: the first center on the origin, the centerline snapped horizontal
                var sk = new Sketch();
                var b0 = new Vec2(130, 0); const double r = 20;
                long ca = Sketch.OriginId, cb = sk.AddPoint(b0).Id;
                var axis = sk.Add(new SkLine { P1 = ca, P2 = cb, Construction = true });
                sk.AddConstraint(CType.Horizontal, axis.Id);
                var n = new Vec2(0, 1);
                long p1 = sk.AddPoint(n * r).Id, p2 = sk.AddPoint(b0 + n * r).Id, p3 = sk.AddPoint(b0 - n * r).Id, p4 = sk.AddPoint(-n * r).Id;
                var top = sk.Add(new SkLine { P1 = p1, P2 = p2 });
                var bot = sk.Add(new SkLine { P1 = p3, P2 = p4 });
                var arcB = sk.Add(new SkArc { Center = cb, Start = p3, End = p2 });
                var arcA = sk.Add(new SkArc { Center = ca, Start = p1, End = p4 });
                foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
                    SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, arc.Id));
                sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
                t.True(SketchSolver.Solve(sk).Converged, "as drawn");
                var ends = new[] { p1, p2, p3, p4, cb };
                var id = ends[which];
                var start = sk.P(id);
                for (int drag = 0; drag < 6; drag++)
                {
                    // a drag: many small moves, then the solve at its end
                    var to = start + new Vec2(Math.Cos(drag * 1.1) * 90, Math.Sin(drag * 1.7) * 70);
                    var from = sk.P(id);
                    for (int step = 1; step <= 25; step++)
                        SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [id] = from + (to - from) * (step / 25.0) }, analyze: false);
                    var end = SketchSolver.Solve(sk);
                    t.True(end.Converged, $"point {which}, drag {drag}: not solved after the drag ({end.Status})");
                    double Len(long line) { var l = (SkLine)sk.Entity(line)!; return Vec2.Distance(sk.P(l.P1), sk.P(l.P2)); }
                    t.True(Len(top.Id) > 1e-3 && Len(bot.Id) > 1e-3, $"point {which}, drag {drag}: a side collapsed");
                    t.Near(sk.P(cb).Y, 0, 1e-6);
                    t.True(sk.P(p1).Y > 0 && sk.P(p2).Y > 0 && sk.P(p3).Y < 0 && sk.P(p4).Y < 0, $"point {which}, drag {drag}: turned inside out");
                }
            }
        });

        t.Test("Sketch drag: dragging a line from the origin leaves the origin where it is (the rest solved about it)", () =>
        {
            var sk = new Sketch();
            var b0 = new Vec2(130, 25);
            long cb = sk.AddPoint(b0).Id;
            var axis = sk.Add(new SkLine { P1 = Sketch.OriginId, P2 = cb, Construction = true });
            var u = b0.Normalized; var n = u.Perp; const double r = 20;
            long p1 = sk.AddPoint(n * r).Id, p2 = sk.AddPoint(b0 + n * r).Id, p3 = sk.AddPoint(b0 - n * r).Id, p4 = sk.AddPoint(-n * r).Id;
            var top = sk.Add(new SkLine { P1 = p1, P2 = p2 });
            var bot = sk.Add(new SkLine { P1 = p3, P2 = p4 });
            var arcB = sk.Add(new SkArc { Center = cb, Start = p3, End = p2 });
            var arcA = sk.Add(new SkArc { Center = Sketch.OriginId, Start = p1, End = p4 });
            foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
                SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, arc.Id));
            sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
            t.True(SketchSolver.Solve(sk).Converged, "as drawn");
            // the centerline dragged as the editor drags a line: both its ends moved by the pointer's travel
            var origins = new Dictionary<long, Vec2> { [Sketch.OriginId] = sk.P(Sketch.OriginId), [cb] = sk.P(cb) };
            for (int step = 1; step <= 30; step++)
            {
                var delta = new Vec2(0, -1.6 * step);
                SketchSolver.Solve(sk, origins.ToDictionary(kv => kv.Key, kv => kv.Value + delta), analyze: false);
            }
            t.True(SketchSolver.Solve(sk).Converged, "solved when the drag ends");
            t.Near(sk.P(Sketch.OriginId).Length, 0, 1e-12);
            // arc A still about the origin: its ends at its radius from it
            t.Near(Vec2.Distance(sk.P(p1), Vec2.Zero), Vec2.Distance(sk.P(p4), Vec2.Zero), 1e-6);
            // (its radius is not dimensioned: a drag may change it) and the slot still whole, its sides either side of the centerline
            var d = sk.P(cb);
            t.True(Vec2.Cross(d, sk.P(p1)) > 0 && Vec2.Cross(d, sk.P(p2)) > 0 && Vec2.Cross(d, sk.P(p3)) < 0 && Vec2.Cross(d, sk.P(p4)) < 0, "not turned inside out");
        });

        t.Test("Sketch drag: what the drag does not need stays put (a slot keeps its length while its side is dragged up)", () =>
        {
            var sk = new Sketch();
            long cb = sk.AddPoint(new Vec2(90, 0)).Id;
            var axis = sk.Add(new SkLine { P1 = Sketch.OriginId, P2 = cb, Construction = true });
            sk.AddConstraint(CType.Horizontal, axis.Id);
            long p1 = sk.AddPoint(new Vec2(0, 15)).Id, p2 = sk.AddPoint(new Vec2(90, 15)).Id, p3 = sk.AddPoint(new Vec2(90, -15)).Id, p4 = sk.AddPoint(new Vec2(0, -15)).Id;
            var top = sk.Add(new SkLine { P1 = p1, P2 = p2 }); var bot = sk.Add(new SkLine { P1 = p3, P2 = p4 });
            var arcB = sk.Add(new SkArc { Center = cb, Start = p3, End = p2 }); var arcA = sk.Add(new SkArc { Center = Sketch.OriginId, Start = p1, End = p4 });
            foreach (var (l, a) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) }) SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, a.Id));
            sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
            t.True(SketchSolver.Solve(sk).Converged, "as drawn");
            // the pointer up and a little to the side (the point can only go up: it stays over the origin)
            for (int k = 1; k <= 20; k++)
                SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [p1] = new Vec2(0.3 * k, 15 + 1.0 * k) }, analyze: false);
            t.True(SketchSolver.Solve(sk).Converged, "solved");
            t.Near(sk.P(p1).Y, 35, 1);            // followed the pointer up (a little behind: it cannot go sideways)
            t.Near(sk.P(cb).X, 90, 0.5);          // the length kept (it used to shrink to nothing)
        });

        t.Test("Sketch solve: dimensions change a slot without flipping its tangencies (each arc runs on past its sides)", () =>
        {
            var (sk, ca, cb, ends, top, bot) = Slot(new Vec2(0, 0), new Vec2(60, 10), 15);
            var axis = sk.Entities.OfType<SkLine>().First(l => l.Construction);
            var len = sk.AddConstraint(CType.Distance, axis.Id); len.Expr = "60"; len.Value = Vec2.Distance(sk.P(ca), sk.P(cb));
            var arc = sk.Entities.OfType<SkArc>().First();
            var rad = sk.AddConstraint(CType.Radius, arc.Id); rad.Expr = "15"; rad.Value = 15;
            t.True(SketchSolver.Solve(sk).Converged, "as drawn");
            int solved = 0;
            foreach (var (l, r) in new[] { (20.0, 15.0), (5.0, 15.0), (120.0, 4.0), (40.0, 30.0), (60.0, 15.0) })
            {
                len.Value = l; rad.Value = r;
                var res = SketchSolver.Solve(sk);
                var a = sk.P(ca); var b = sk.P(cb); var d = b - a;
                double Side(long p) => Vec2.Cross(d, sk.P(p) - a);
                if (!res.Converged) continue; // (refused: the sketch as it was)
                t.Near(d.Length, l, 1e-6);
                t.True(Side(ends[0]) > 0 && Side(ends[1]) > 0 && Side(ends[2]) < 0 && Side(ends[3]) < 0, $"length {l}, radius {r}: the sides where they were");
                double Len(long line) { var e = (SkLine)sk.Entity(line)!; return Vec2.Distance(sk.P(e.P1), sk.P(e.P2)); }
                t.Near(Len(top), l, 1e-6); t.Near(Len(bot), l, 1e-6);
                solved++;
            }
            t.Eq(solved, 5); // (none of these needs a flip: all solve)
        });
    }
}
