using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary><c>-- validate file.fcad out.step</c>: rebuild, report watertightness/volumes, write the exact STEP.</summary>
static class Validate
{
    public static int Run(string file, string step)
    {
        var doc = CadDocument.FromJson(File.ReadAllText(file));
        var r = new Regenerator().Rebuild(doc);
        foreach (var f in doc.Features) if (!r.Status[f.Id].Ok) Console.WriteLine($"  {f.Name}: {r.Status[f.Id].Error}");
        foreach (var b in r.State.Bodies)
            Console.WriteLine($"  body {b.Id}: native={b.Solid.Native != null} open={b.Solid.Topology.OpenHalfEdges} meshVolume={b.Mesh.Volume:0.###} exact={KernelTests.Vex(b.Solid):0.###}");
        // FC_FACE=<face name>: tessellation diagnostics for that face
        foreach (var b in Environment.GetEnvironmentVariable("FC_FACE") != null ? r.State.Bodies : new())
        {
            var fresh = FeatherCad.Kernel.Native.NTess.ToSolid(b.Solid.Native!.Clone());
            var deep = FeatherCad.Kernel.Native.NBody.CopyFresh(b.Solid.Native!);
            Console.WriteLine($"  re-tessellated clone: open={fresh.Topology.OpenHalfEdges}; rebuilt edges: open={FeatherCad.Kernel.Native.NTess.ToSolid(deep).Topology.OpenHalfEdges}");
            if (Environment.GetEnvironmentVariable("FC_FACE") is { } fn2)
            {
                var d2 = FeatherCad.Kernel.Native.NBody.CopyFresh(b.Solid.Native!); d2.Link();
                foreach (var e in d2.Edges) e.Samples(0.02);
                foreach (var f in d2.Faces.Where(x => x.Tag.Name == fn2))
                {
                    var single = new FeatherCad.Kernel.Native.NBody(); single.Faces.Add(f);
                    var fac = FeatherCad.Kernel.Native.NTess.Facets(single, 0.02);
                    foreach (var c in f.Loops[0].Coedges.Where(c => c.Edge.Curve is FeatherCad.Kernel.Native.NCircle))
                    {
                        var smp = c.Edge.Samples(0.02);
                        bool On(Vec3 v) => smp.Any(p => Vec3.Distance(p, v) < 1e-9);
                        var segs = new List<string>();
                        foreach (var t in fac)
                            foreach (var l in t.Loops)
                                for (int i = 0; i < l.Length; i++)
                                    if (On(l[i]) && On(l[(i + 1) % l.Length])) segs.Add($"{l[i].Y:0.###}>{l[(i + 1) % l.Length].Y:0.###}");
                        var dm = FeatherCad.Kernel.Native.NDomain.Of(f, 0.02);
                        var idx = Triangulator.Triangulate(dm.Loops);
                        var L0 = dm.Loops[0];
                        int degen = 0;
                        for (int q = 0; q + 2 < idx.Count; q += 3) { var A = L0[idx[q]]; var B = L0[idx[q + 1]]; var C = L0[idx[q + 2]]; if (Vec2.Cross(B - A, C - A) <= 0) degen++; }
                        Console.WriteLine($"    triangulate: {L0.Length} pts -> {idx.Count / 3} tris ({degen} degenerate/inverted)");
                        Console.WriteLine($"  face edge {c.Start.P}->{c.End.P} samples y: {string.Join(",", smp.Select(p => p.Y.ToString("0.###")))} | facet segs: {string.Join(" ", segs)}");
                    }
                }
            }
        }
        if (Environment.GetEnvironmentVariable("FC_FACE") is { } fname)
            foreach (var b in r.State.Bodies)
            {
                var nb = b.Solid.Native!; nb.Link();
                var ids = new Dictionary<FeatherCad.Kernel.Native.NEdge, int>(ReferenceEqualityComparer.Instance);
                int Id(FeatherCad.Kernel.Native.NEdge e) => ids.TryGetValue(e, out var i) ? i : ids[e] = ids.Count;
                foreach (var f in nb.Faces.Where(x => x.Tag.Name == fname))
                {
                    Console.WriteLine($"face {f.Tag.Name} {f.Surface.Kind} sense={f.Sense} loops={f.Loops.Count}");
                    var dom = FeatherCad.Kernel.Native.NDomain.Of(f, 0.02);
                    Console.WriteLine($"  uv [{dom.UMin:0.####},{dom.UMax:0.####}]x[{dom.VMin:0.####},{dom.VMax:0.####}] area {dom.SignedArea():0.######} spine [{f.Surface.Spine?.S0:0.###},{f.Surface.Spine?.S1:0.###}]");
                    var facets = new List<Face>();
                    var single = new FeatherCad.Kernel.Native.NBody(); single.Faces.Add(f);
                    facets = FeatherCad.Kernel.Native.NTess.Facets(single, 0.02);
                    var verts = facets.SelectMany(x => x.Loops.SelectMany(l => l)).ToList();
                    foreach (var c in f.Loops[0].Coedges.Where(c => c.Edge.Curve is FeatherCad.Kernel.Native.NCircle))
                    {
                        var smp = c.Edge.Samples(0.02);
                        Console.WriteLine($"  circle edge {c.Start.P}->{c.End.P}: {smp.Count} samples, in facets {smp.Count(p => verts.Any(v => Vec3.Distance(v, p) < 1e-9))}, T0={c.Edge.T0:0.####} T1={c.Edge.T1:0.####}");
                    }
                    foreach (var l in f.Loops)
                        Console.WriteLine("  loop: " + string.Join(" ", l.Coedges.Select(c => $"e{Id(c.Edge)}{(c.Forward ? "+" : "-")}[{c.Edge.Curve.GetType().Name[1..]} {c.Start.P}->{c.End.P} uses={string.Join("/", c.Edge.Uses.Select(u => u.Face.Tag.Name))}]")));
                }
            }
        foreach (var b in r.State.Bodies)
        {
            try
            {
                var brep = ExactBrep.Build(b.Solid);
                Console.WriteLine($"  exact B-rep: {brep.Faces.Count} faces, warnings: {string.Join("; ", brep.Warnings)}");
            }
            catch (Exception ex) { Console.WriteLine("  exact B-rep failed: " + ex); }
        }
        using var fs = File.Create(step);
        StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), Path.GetFileNameWithoutExtension(file));
        if (StepWriter.LastError != null) Console.WriteLine("  STEP exact export failed: " + StepWriter.LastError);
        return 0;
    }
}
