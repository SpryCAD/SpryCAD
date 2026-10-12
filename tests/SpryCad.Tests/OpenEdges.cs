using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- openedges file.sprt [feature]</c>: a part rebuilt feature by feature, the display mesh's open half-edges (cracks)
/// after each; at the first feature with any (or the one named), which faces they are on and where.
/// </summary>
static class OpenEdges
{
    public static int Run(string file, string? feature)
    {
        var doc = CadDocument.Load(file);
        SpryCad.Kernel.Native.NShell.Debug = m => { if (m.StartsWith("folded")) Console.WriteLine("  " + m); };
        if (Environment.GetEnvironmentVariable("SHELL_FIRST") is { } th)
        {
            var shell = doc.Features.OfType<ShellFeature>().Single(); doc.Features.Remove(shell);
            doc.Features.Insert(doc.Features.FindIndex(f => f is FilletFeature), shell); shell.Thickness = th;
        }
        foreach (var f in doc.Features) Console.WriteLine($"  {f.Id} {f.Kind} {f.Name}{(f is FilletFeature ff ? " R" + ff.Radius : f is ShellFeature sf ? " t" + sf.Thickness : "")}");
        if (Environment.GetEnvironmentVariable("FACET_HANG") != null)
        {
            // each display facet polygon triangulated alone, with a time limit: the one that does not finish
            var rr = new Regenerator().Rebuild(doc);
            foreach (var b in rr.State.Bodies)
                for (int i = 0; i < b.Solid.Faces.Count; i++)
                {
                    var fc = b.Solid.Faces[i];
                    var task = Task.Run(() => SpryCad.Kernel.Triangulator.TriangulateFace(fc));
                    if (task.Wait(3000)) continue;
                    Console.WriteLine($"facet {i} of {fc.Tag.Name}: no answer; normal {fc.Plane.Normal}");
                    foreach (var l in fc.Loops) Console.WriteLine("  loop " + string.Join(" ", l.Select(p => $"({p.X:R},{p.Y:R},{p.Z:R})")));
                    return 1;
                }
            Console.WriteLine("every facet triangulates");
            return 0;
        }
        int prev = 0;
        for (int i = 1; i <= doc.Features.Count; i++)
        {
            var r = new Regenerator().Rebuild(doc, i);
            var name = doc.Features[i - 1].Name;
            int open = r.State.Bodies.Sum(b => b.Solid.Topology.OpenHalfEdges);
            Console.WriteLine($"after {name}: open={open}{(r.Status[doc.Features[i - 1].Id].Ok ? "" : " FAILED " + SpryCad.Kernel.Csg.LastFallbackReason)}");
            if (open > prev && (feature == null || feature == name) || feature == name)
            {
                foreach (var b in r.State.Bodies.Where(b => b.Solid.Topology.OpenHalfEdges > 0))
                {
                    var topo = b.Solid.Topology;
                    var owner = new HashSet<(int, int)>();
                    foreach (var fl in topo.FaceLoops) foreach (var l in fl) for (int j = 0; j < l.Length; j++) owner.Add((l[j], l[(j + 1) % l.Length]));
                    var counts = new Dictionary<string, int>();
                    for (int fi = 0; fi < topo.FaceLoops.Count; fi++)
                        foreach (var l in topo.FaceLoops[fi])
                            for (int j = 0; j < l.Length; j++)
                                if (!owner.Contains((l[(j + 1) % l.Length], l[j])))
                                {
                                    var n = b.Solid.Faces[fi].Tag.Name;
                                    counts[n] = counts.GetValueOrDefault(n) + 1;
                                    if (counts[n] <= 2) Console.WriteLine($"   open {n} ({b.Solid.Faces[fi].Tag.Surface.Kind}): {topo.Vertices[l[j]]} -> {topo.Vertices[l[(j + 1) % l.Length]]}");
                                }
                    Console.WriteLine("   by face: " + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
                    // each such face alone: its boundary (the shared edge samples) covered by its own triangles?
                    var nb = b.Solid.Native!;
                    nb.Link();
                    foreach (var nf in nb.Faces.Where(x => counts.ContainsKey(x.Tag.Name)))
                    {
                        var one = new SpryCad.Kernel.Native.NBody(); one.Faces.Add(nf);
                        var facets = SpryCad.Kernel.Native.NTess.Facets(one);
                        var dom = SpryCad.Kernel.Native.NDomain.Of(nf, SpryCad.Kernel.Native.NTess.DefaultTol);
                        var segs = new HashSet<(Vec3, Vec3)>();
                        foreach (var fc in facets) foreach (var l in fc.Loops) for (int j = 0; j < l.Length; j++) segs.Add((l[j], l[(j + 1) % l.Length]));
                        int missing = 0, total = 0;
                        for (int li = 0; li < dom.Loops3.Count; li++)
                        {
                            var l = dom.Loops3[li]; var lu = dom.Loops[li];
                            for (int j = 0; j < l.Length; j++)
                            {
                                total++;
                                if (segs.Contains((l[j], l[(j + 1) % l.Length]))) continue;
                                missing++;
                                if (missing <= 3 && nf.Surface.Kind == SurfaceKind.Cylinder)
                                    Console.WriteLine($"      missing {l[j]} -> {l[(j + 1) % l.Length]} (3d {Vec3.Distance(l[j], l[(j + 1) % l.Length]):g3}) uv {lu[j]} -> {lu[(j + 1) % lu.Length]} reverse there {segs.Contains((l[(j + 1) % l.Length], l[j]))}");
                            }
                        }
                        double area = 0;
                        foreach (var lp in dom.Loops) for (int q = 0, w = lp.Length - 1; q < lp.Length; w = q++) area += lp[w].X * lp[q].Y - lp[q].X * lp[w].Y;
                        // the outline crossing itself in (u, v)
                        int crossings = 0;
                        foreach (var lp in dom.Loops)
                            for (int ii = 0; ii < lp.Length; ii++)
                                for (int jj = ii + 2; jj < lp.Length; jj++)
                                {
                                    if (ii == 0 && jj == lp.Length - 1) continue;
                                    Vec2 a0 = lp[ii], a1 = lp[(ii + 1) % lp.Length], b0 = lp[jj], b1 = lp[(jj + 1) % lp.Length];
                                    double O(Vec2 p, Vec2 q, Vec2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
                                    if (O(a0, a1, b0) * O(a0, a1, b1) < 0 && O(b0, b1, a0) * O(b0, b1, a1) < 0)
                                    {
                                        crossings++;
                                        if (crossings <= 2) Console.WriteLine($"      crosses: seg {ii} {a0}->{a1} x seg {jj} {b0}->{b1} (of {lp.Length})");
                                    }
                                }
                        Console.WriteLine($"      {crossings} self-crossings");
                        Console.WriteLine($"   face {nf.Tag.Name} {nf.Surface.Kind}{(nf.Surface.Base is { } bs ? "(" + bs.Kind + ")" : "")}: {facets.Count} facets, boundary {total - missing}/{total} covered, loops {dom.Loops.Count}, uv area {area / 2:g4}");
                    }
                }
                if (feature == null || feature == name) return 0;
            }
            prev = open;
        }
        return 0;
    }
}
