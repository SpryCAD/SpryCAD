using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary><c>-- unfold file.step [k-factor] [--out=dir]</c>: imports a STEP file, unfolds each solid, writes the flat as DXF.</summary>
static class UnfoldTool
{
    public static int Run(string[] args)
    {
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
}
