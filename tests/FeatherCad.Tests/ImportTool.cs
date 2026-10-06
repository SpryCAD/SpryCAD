using System.Diagnostics;
using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary><c>-- import file.step [out.step]</c>: imports a STEP file, reports the solids, re-exports them.</summary>
static class ImportTool
{
    static string Fmt(NFace f, Vec3 p) { var q = f.Surface.UV(p); return $"({q.X:0.###},{q.Y:0.###})"; }

    public static int Run(string[] args)
    {
        int bad = 0;
        foreach (var file in args.Skip(1).Where(a => !a.StartsWith("--out=")))
        {
            var sw = Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("FC_SPLIT") is { } sf) NSeamSplit.Debug = m => { if (m.Contains(sf) || m.StartsWith("   ")) Console.WriteLine(m); };
            try
            {
                var r = StepImport.Read(File.ReadAllBytes(file), "F1.");
                var ms = sw.ElapsedMilliseconds;
                if (Environment.GetEnvironmentVariable("FC_PROF") != null)
                {
                    foreach (var s in r.Solids)
                    {
                        var t0 = Stopwatch.StartNew();
                        var facets = NTess.Facets(s.Body);
                        Console.WriteLine($"  tessellation {t0.ElapsedMilliseconds} ms ({facets.Count} facets)");
                        foreach (var g in facets.GroupBy(x => x.Tag.Name).OrderByDescending(g => g.Count()).Take(6))
                        {
                            var kind = s.Body.Faces.First(f => f.Tag.Name == g.Key).Surface.Kind;
                            Console.WriteLine($"    {g.Key} {kind}: {g.Count()} facets");
                        }
                        t0.Restart();
                        foreach (var f in s.Body.Faces)
                        {
                            var dm = NDomain.Of(f);
                            if (f.Surface.Kind == SurfaceKind.BSpline && Environment.GetEnvironmentVariable("FC_AREAS") != null)
                            {
                                var nb = f.Surface.Nurbs!;
                                Console.WriteLine($"    {f.Tag.Name} sense={f.Sense} area={dm.SignedArea():0.####} deg {nb.Pu}x{nb.Pv} poles {nb.P.GetLength(0)}x{nb.P.GetLength(1)} closed {nb.ClosedU}/{nb.ClosedV} rational={nb.W != null}");
                            }
                        }
                        Console.WriteLine($"  domains {t0.ElapsedMilliseconds} ms");
                        t0.Restart();
                        if (Environment.GetEnvironmentVariable("FC_NOTOPO") == null) { var top = NTess.ToSolid(s.Body).Topology; }
                        Console.WriteLine($"  topology+tess {t0.ElapsedMilliseconds} ms");
                    }
                }
                foreach (var w in r.Warnings) Console.WriteLine("  warning: " + w);
                foreach (var s in r.Solids)
                {
                    foreach (var f in s.Body.Faces)
                    {
                        try { NDomain.Of(f); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  bad face {f.Tag.Name} anchor={f.Anchor}: {ex.Message}");
                            foreach (var l in f.Loops)
                                Console.WriteLine("    loop: " + string.Join(" ", l.Coedges.Select(c => $"[{c.Edge.Curve.GetType().Name[1..]}{(c.Forward ? "+" : "-")} {Fmt(f, c.Start.P)}->{Fmt(f, c.End.P)}]")));
                        }
                    }
                    var solid = NTess.ToSolid(s.Body);
                    Console.WriteLine($"{Path.GetFileName(file),-28} {ms,5} ms  {s.Name}: faces {s.Body.Faces.Count} ({string.Join(", ", s.Body.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Key} {g.Count()}"))}), " +
                                      $"open={solid.Topology.OpenHalfEdges}, volume {NTess.ToSolid(s.Body, 2e-4).Volume():0.###}");
                }
                if (args.FirstOrDefault(a => a.StartsWith("--out=")) is { } o)
                {
                    var outPath = Path.Combine(o[6..], Path.GetFileNameWithoutExtension(file) + ".reexport.step");
                    using var fs = File.Create(outPath);
                    StepWriter.Write(fs, r.Solids.Select(s => (s.Name, NTess.ToSolid(s.Body))), Path.GetFileNameWithoutExtension(file));
                    if (StepWriter.LastError != null) Console.WriteLine("  re-export failed: " + StepWriter.LastError.Message);
                }
            }
            catch (Exception ex) { bad++; Console.WriteLine($"{Path.GetFileName(file),-28} FAILED: {ex.Message}"); if (ex is not InvalidDataException) Console.WriteLine(ex.StackTrace); }
        }
        return bad;
    }
}
