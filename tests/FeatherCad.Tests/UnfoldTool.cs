using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary><c>-- unfold file.step [k-factor] [--out=dir]</c>: imports a STEP file, unfolds each solid, writes the flat as DXF.</summary>
static class UnfoldTool
{
    public static int Run(string[] args)
    {
        // a folder: every STEP file in it, compared with the DXF of the same name when there is one
        if (Directory.Exists(args[1]))
        {
            int failed = 0;
            foreach (var f in Directory.GetFiles(args[1]).Where(f => f.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)).Order())
            {
                Console.WriteLine($"=== {Path.GetFileName(f)}");
                var dxf = Path.ChangeExtension(f, ".dxf");
                var a = new List<string> { args[0], f };
                a.AddRange(args.Skip(2).Where(x => !x.StartsWith("--compare=")));
                if (File.Exists(dxf)) a.Add("--compare=" + dxf);
                try { failed += Run(a.ToArray()) > 0 ? 1 : 0; }
                catch (Exception ex) { Console.WriteLine("  failed: " + ex.Message); failed++; }
            }
            return failed;
        }
        var file = args[1];
        var k = args.Length > 2 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var kk) ? kk : 0.5;
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out="))?[6..] ?? Path.GetTempPath();
        var r = StepImport.Read(File.ReadAllBytes(file), "F1.");
        int bad = 0;
        foreach (var s in r.Solids)
        {
            var b = s.Body;
            Console.WriteLine($"{s.Name}: faces {b.Faces.Count} ({string.Join(", ", b.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Key} {g.Count()}"))})");
            foreach (var f in b.Faces.Where(f => f.Surface.Kind == SurfaceKind.BSpline))
            {
                var nb = f.Surface.Nurbs!;
                var box = f.Bounds();
                Console.WriteLine($"  B-spline {f.Tag.Name}: degree {nb.Pu}x{nb.Pv}, poles {nb.P.GetLength(0)}x{nb.P.GetLength(1)}, rational {nb.W != null}, size {box.Size}");
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var flat = SheetUnfold.Unfold(b, k);
                var size = flat.Body.TightBounds().Size;
                Console.WriteLine($"  flat in {sw.ElapsedMilliseconds} ms: thickness {flat.Thickness:0.####}, {flat.Bends.Count} bends, {flat.Regions.Count} region(s) with {flat.Regions.Sum(x => x.Holes.Count)} holes, size {size}");
                // bend midpoints from the center of the outline's bounds (compares with other systems' DXF whatever the orientation)
                var all = flat.Regions.SelectMany(x => x.Outer.Verts).Select(v => v.P).ToList();
                var mid = new Vec2((all.Min(p => p.X) + all.Max(p => p.X)) / 2, (all.Min(p => p.Y) + all.Max(p => p.Y)) / 2);
                foreach (var bend in flat.Bends)
                {
                    var m = (bend.A + bend.B) * 0.5 - mid;
                    Console.WriteLine($"    bend {bend.Angle * 180 / Math.PI:0.##}° r {bend.InnerRadius:0.####} {(bend.Up ? "up" : "down")}, length {Vec2.Distance(bend.A, bend.B):0.###}, midpoint ±({Math.Abs(m.X):0.###}, {Math.Abs(m.Y):0.###})");
                }
                var loops = flat.Regions.SelectMany(x => x.AllLoops()).ToList();
                bool In(Vec2 p) => loops.Count(l => l.Contains(p)) % 2 == 1;
                int off = flat.Bends.Sum(bd => bd.Tangents.Sum(tg => Enumerable.Range(1, 19).Count(i => !In(tg.A + (tg.B - tg.A) * (i / 20.0)))));
                Console.WriteLine($"  tangent lines: {flat.Bends.Sum(bd => bd.Tangents.Count)} pieces, {off} sample points off the material");
                if (off > 0) bad++;
                if (args.FirstOrDefault(a => a.StartsWith("--compare=")) is { } cmp) bad += Compare(loops, cmp[10..]);
                var err = flat.Body.Validate();
                if (err != null) { Console.WriteLine("  flat body invalid: " + err); bad++; }
                var path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".flat.dxf");
                using (var fs = File.Create(path)) DxfWriter.Write(fs, flat);
                Console.WriteLine("  wrote " + path);
            }
            catch (UnfoldException ex) { Console.WriteLine("  cannot unfold: " + ex.Message); bad++; }
        }
        return bad;
    }

    /// <summary>
    /// Compares the flat outline with another system's DXF of it (outline layers: everything but bend lines; inches
    /// when $INSUNITS is 1): both centered on their bounds, the best of the four mirror images, the largest
    /// distance from each outline's points to the other outline.
    /// </summary>
    static int Compare(List<ProfileLoop> ours, string dxfPath)
    {
        var (refSegs, inches) = ReadDxf(dxfPath);
        var s = inches ? 25.4 : 1.0;
        refSegs = refSegs.Select(x => (x.A * s, x.B * s)).ToList();
        var oursSegs = ours.SelectMany(l => l.Verts.Select((v, i) => (A: v.P, B: l.Verts[(i + 1) % l.Verts.Count].P))).ToList();
        static (Vec2 min, Vec2 max) Bounds(IEnumerable<(Vec2 A, Vec2 B)> segs)
        {
            var pts = segs.SelectMany(x => new[] { x.A, x.B }).ToList();
            return (new Vec2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Vec2(pts.Max(p => p.X), pts.Max(p => p.Y)));
        }
        var (rmin, rmax) = Bounds(refSegs); var (omin, omax) = Bounds(oursSegs);
        var rc = (rmin + rmax) * 0.5; var oc = (omin + omax) * 0.5;
        Console.WriteLine($"  reference {Path.GetFileName(dxfPath)}: {refSegs.Count} segments, size {rmax.X - rmin.X:0.###} x {rmax.Y - rmin.Y:0.###}; ours {omax.X - omin.X:0.###} x {omax.Y - omin.Y:0.###}");
        static double Dist(Vec2 p, List<(Vec2 A, Vec2 B)> segs)
        {
            double best = double.MaxValue;
            foreach (var (a, b) in segs)
            {
                var d = b - a; var l2 = Vec2.Dot(d, d);
                var t = l2 < 1e-24 ? 0 : Math.Clamp(Vec2.Dot(p - a, d) / l2, 0, 1);
                best = Math.Min(best, Vec2.Distance(p, a + d * t));
            }
            return best;
        }
        static IEnumerable<Vec2> Dense(List<(Vec2 A, Vec2 B)> segs) => segs.SelectMany(x => Enumerable.Range(0, 4).Select(i => x.A + (x.B - x.A) * (i / 4.0)));
        // the eight ways the flat may lie in the other DXF: mirrored in x and/or y, and turned 90°
        var placements = new List<Func<Vec2, Vec2>>();
        foreach (var swap in new[] { false, true })
            foreach (var (sx, sy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
                placements.Add(p => { var q = p - oc; if (swap) q = new Vec2(q.Y, q.X); return new Vec2(q.X * sx, q.Y * sy) + rc; });
        (double toRef, double toOurs, Vec2 worst, List<(Vec2 A, Vec2 B)> mapped) best = (double.MaxValue, 0, default, new());
        foreach (var M in placements)
        {
            var mapped = oursSegs.Select(x => (A: M(x.A), B: M(x.B))).ToList();
            double toRef = 0; Vec2 worst = default;
            foreach (var p in Dense(mapped)) { var d = Dist(p, refSegs); if (d > toRef) { toRef = d; worst = p; } if (toRef >= best.toRef) break; }
            if (toRef >= best.toRef) continue;
            double toOurs = 0;
            foreach (var p in Dense(refSegs)) toOurs = Math.Max(toOurs, Dist(p, mapped));
            best = (toRef, toOurs, worst, mapped);
        }
        Console.WriteLine($"  outline deviation: ours to reference {best.toRef:0.####}, reference to ours {best.toOurs:0.####} (worst near {best.worst - rc} from the center)");
        if (Environment.GetEnvironmentVariable("FC_DEV") != null)
        {
            // where ours is off by more than 0.01: grouped in 5 mm cells
            var devs = Dense(best.mapped).Select(p => (p, d: Dist(p, refSegs))).ToList();
            foreach (var g in devs.Where(x => x.d > 0.01).GroupBy(x => (Math.Round((x.p.X - rc.X) / 5), Math.Round((x.p.Y - rc.Y) / 5))).OrderByDescending(g => g.Max(x => x.d)).Take(12))
                Console.WriteLine($"    near ({g.Key.Item1 * 5}, {g.Key.Item2 * 5}): {g.Count()} points, max {g.Max(x => x.d):0.####}");
        }
        return Math.Max(best.toRef, best.toOurs) > 0.05 ? 1 : 0;
    }

    /// <summary>The outline of a DXF as segments (lines, arcs and polylines with bulges, sampled), and whether it is in inches.</summary>
    static (List<(Vec2 A, Vec2 B)> segs, bool inches) ReadDxf(string path)
    {
        var lines = File.ReadAllLines(path);
        var pairs = new List<(int code, string value)>();
        for (int i = 0; i + 1 < lines.Length; i += 2) pairs.Add((int.Parse(lines[i].Trim()), lines[i + 1].Trim()));
        double D(string v) => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        bool inches = false;
        for (int i = 0; i + 1 < pairs.Count; i++) if (pairs[i] is (9, "$INSUNITS")) inches = pairs[i + 1].value == "1";
        var segs = new List<(Vec2, Vec2)>();
        void Arc(Vec2 c, double r, double a0, double a1)
        {
            while (a1 <= a0) a1 += 2 * Math.PI;
            int n = Math.Max(4, (int)Math.Ceiling((a1 - a0) / (Math.PI / 90)));
            for (int k = 0; k < n; k++) segs.Add((c + Vec2.FromAngle(a0 + (a1 - a0) * k / n, r), c + Vec2.FromAngle(a0 + (a1 - a0) * (k + 1) / n, r)));
        }
        int start = pairs.FindIndex(p => p is (2, "ENTITIES"));
        for (int i = start; i >= 0 && i < pairs.Count; i++)
        {
            if (pairs[i].code != 0) continue;
            var type = pairs[i].value;
            if (type == "ENDSEC") break;
            int j = i + 1;
            var ent = new List<(int code, string value)>();
            while (j < pairs.Count && pairs[j].code != 0) ent.Add(pairs[j++]);
            var layer = ent.FirstOrDefault(e => e.code == 8).value ?? "";
            if (layer.Contains("BEND", StringComparison.OrdinalIgnoreCase)) continue;
            double G(int code) => D(ent.First(e => e.code == code).value);
            switch (type)
            {
                case "LINE": segs.Add((new Vec2(G(10), G(20)), new Vec2(G(11), G(21)))); break;
                case "ARC": Arc(new Vec2(G(10), G(20)), G(40), G(50) * Math.PI / 180, G(51) * Math.PI / 180); break;
                case "CIRCLE": Arc(new Vec2(G(10), G(20)), G(40), 0, 2 * Math.PI); break;
                case "LWPOLYLINE":
                    {
                        var pts = new List<(Vec2 p, double bulge)>();
                        foreach (var (code, value) in ent)
                        {
                            if (code == 10) pts.Add((new Vec2(D(value), 0), 0));
                            else if (code == 20) pts[^1] = (new Vec2(pts[^1].p.X, D(value)), 0);
                            else if (code == 42) pts[^1] = (pts[^1].p, D(value));
                        }
                        bool closed = ent.Any(e => e.code == 70 && (int.Parse(e.value) & 1) == 1);
                        int cnt = closed ? pts.Count : pts.Count - 1;
                        for (int k = 0; k < cnt; k++)
                        {
                            var (a, bulge) = pts[k]; var b = pts[(k + 1) % pts.Count].p;
                            if (Math.Abs(bulge) < 1e-12) { segs.Add((a, b)); continue; }
                            // bulge = tan(θ/4), counter-clockwise when positive
                            var th = 4 * Math.Atan(bulge);
                            var chord = Vec2.Distance(a, b); var r = chord / (2 * Math.Sin(Math.Abs(th) / 2));
                            var mid = (a + b) * 0.5; var nrm = (b - a).Perp * (1 / chord);
                            var h = r * Math.Cos(th / 2) * Math.Sign(bulge);
                            var c = mid + nrm * h;
                            var a0 = (a - c).Angle; var a1 = (b - c).Angle;
                            if (th > 0) Arc(c, r, a0, a1); else Arc(c, r, a1, a0);
                        }
                        break;
                    }
            }
            i = j - 1;
        }
        return (segs, inches);
    }
}
