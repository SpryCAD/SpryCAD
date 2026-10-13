using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- shelldiag part.sprt thickness</c>: the body just before the part's shell offset inward by the thickness (its
/// open faces outward), topology kept; every face that turns inside out or folds listed with its shape and neighbours.
/// </summary>
static class ShellDiag
{
    public static int Run(string file, string thickness)
    {
        var doc = CadDocument.Load(file);
        int si = doc.Features.FindIndex(f => f is ShellFeature);
        var shell = (ShellFeature)doc.Features[si];
        var r = new Regenerator().Rebuild(doc, si);
        var t = double.Parse(thickness);
        var open = shell.Faces.Select(fr => Resolver.ResolveFaceName(fr, r.State).name).ToHashSet();
        var body = r.State.Bodies.Single().Solid.Native!;
        body.Link();
        Console.WriteLine($"{body.Faces.Count} faces, open: {string.Join(", ", open)}");
        NShell.Debug = m => { if (m.StartsWith("split")) Console.WriteLine("  " + m); };
        NBody inner;
        try { inner = NShell.Offset(body, f => open.Contains(f.Tag.Name) ? t : -t, "I."); }
        catch (Exception ex)
        {
            Console.WriteLine("offset failed: " + ex.Message);
            return 1;
        }
        Console.WriteLine("inner valid: " + (inner.Validate() ?? "ok"));
        for (int i = 0; i < body.Faces.Count; i++)
        {
            var f = body.Faces[i];
            NDomain a, b;
            try { a = NDomain.Of(f); b = NDomain.Of(inner.Faces[i]); }
            catch (Exception ex) { Console.WriteLine($"  {f.Tag.Name}: no domain ({ex.Message})"); continue; }
            double sa = a.SignedArea(), sb = b.SignedArea();
            int ca = Crossings(a), cb = Crossings(b);
            if (Math.Sign(sa) == Math.Sign(sb) && cb <= ca) continue;
            if (Environment.GetEnvironmentVariable("CROSS") is { } who && f.Tag.Name.Contains(who)) { Print = true; Crossings(b); Print = false; Console.WriteLine($"       (of {b.Loops.Sum(x => x.Length)} points; original {a.Loops.Sum(x => x.Length)})"); }
            var bx = f.Bounds();
            var nb = f.Coedges.SelectMany(c => c.Edge.Uses.Select(u => u.Face)).Where(g => !ReferenceEquals(g, f)).Select(g => g.Tag.Name).Distinct();
            Console.WriteLine($"  {f.Tag.Name} {f.Surface.Kind} R={f.Surface.R:0.###}: area {sa:g3} -> {sb:g3}, crossings {ca} -> {cb}, size {bx.Max - bx.Min}");
            foreach (var l in f.Loops)
                Console.WriteLine("     loop " + string.Join(" ", l.Coedges.Select(c => $"{c.Edge.Curve.GetType().Name[1..]}:{Vec3.Distance(c.Edge.V0.P, c.Edge.V1.P):0.###}")));
            Console.WriteLine("     next to " + string.Join(", ", nb));
        }
        return 0;
    }

    static bool Print;

    static int Crossings(NDomain d)
    {
        int n = 0;
        foreach (var l in d.Loops)
            for (int i = 0; i < l.Length; i++)
                for (int j = i + 2; j < l.Length; j++)
                {
                    if (i == 0 && j == l.Length - 1) continue;
                    Vec2 a0 = l[i], a1 = l[(i + 1) % l.Length], b0 = l[j], b1 = l[(j + 1) % l.Length];
                    static double O(Vec2 p, Vec2 q, Vec2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
                    if (O(a0, a1, b0) * O(a0, a1, b1) < 0 && O(b0, b1, a0) * O(b0, b1, a1) < 0)
                    {
                        n++;
                        if (Print) Console.WriteLine($"       crossing: seg {i} {a0}->{a1} x seg {j} {b0}->{b1} of {l.Length}");
                    }
                }
        return n;
    }
}
