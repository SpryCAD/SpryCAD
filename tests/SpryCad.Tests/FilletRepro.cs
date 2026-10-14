using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- filletrepro brake.sprt case [radius]</c>: the brake duct's fillet failures (docs/brake-duct-modeling-notes.md),
/// rebuilt from the sample (up to its shell) the way they first went: 10, the ramp's top edge filleted across the
/// plenum's rounds (no sketch fillets in the drop); 11, the plenum's edge rounds after the bend fillets; 12, the
/// plenum's end 3.25 wide. Prints each feature's status.
/// </summary>
static class FilletRepro
{
    public static CadDocument Make(string file, string kase, string? radius)
    {
        var doc = CadDocument.Load(file);
        int si = doc.Features.FindIndex(f => f is ShellFeature);
        // (fillet: the part as it is, a fillet added on the edges nearest the points given (x,y,z;x,y,z…) after the
        // feature named in FILLET_AFTER, of FILLET_R, chained or not by FILLET_CHAIN, added to the mirror's seeds by
        // FILLET_MIRROR)
        if (kase == "mirrorfillet")
        {
            // the last fillet (the second tab's) dropped; the first tab's fillet mirrored across Front instead
            if (Environment.GetEnvironmentVariable("BEND_CHAIN") == "0") doc.Features.OfType<FilletFeature>().First(f => f.Name == "Tube bend").TangentPropagation = false;
            var f3 = doc.Features.OfType<FilletFeature>().Last(); doc.Features.Remove(f3);
            var f2 = doc.Features.OfType<FilletFeature>().Last();
            if (Environment.GetEnvironmentVariable("IN_TAB_MIRROR") != null)
            {
                // Fillet2 moved before the tab mirror, and mirrored with the tab and its slot
                var tm = doc.Features.OfType<MirrorFeature>().First(m => m.Name == "Tab mirror");
                doc.Features.Remove(f2); doc.Features.Insert(doc.Features.IndexOf(tm), f2); tm.Seeds.Add(f2.Id);
            }
            else doc.AddFeature(new MirrorFeature { Name = "Fillet mirror", Seeds = { f2.Id }, Plane = PlaneRef.Front });
            return doc;
        }
        if (kase == "dump")
        {
            foreach (var f in doc.Features.Where(f => f is FilletFeature or MirrorFeature or ShellFeature))
                Console.WriteLine($"  {f.Name}: {CadDocument.JsonOf(f)}");
            Environment.Exit(0);
        }
        if (kase == "fillet")
        {
            if (Environment.GetEnvironmentVariable("BEND_CHAIN") == "0") doc.Features.OfType<FilletFeature>().First(f => f.Name == "Tube bend").TangentPropagation = false;
            var after = Environment.GetEnvironmentVariable("FILLET_AFTER")!;
            int k = doc.Features.FindIndex(f => f.Name == after);
            var st = new Regenerator().Rebuild(doc, k + 1).State;
            var body = st.Bodies.Single().Solid.Native!;
            var fi = new FilletFeature { Name = "Repro fillet", Radius = Environment.GetEnvironmentVariable("FILLET_R") ?? "0.06", TangentPropagation = Environment.GetEnvironmentVariable("FILLET_CHAIN") != "0" };
            if (Environment.GetEnvironmentVariable("FILLET_FACE") is { } ff)
                foreach (var x in body.Edges.Where(x => x.Uses.Count == 2 && x.Uses.Any(u => u.Face.Tag.Name == ff)))
                {
                    var (fa, fb) = (x.Uses[0].Face, x.Uses[1].Face);
                    var angs = Enumerable.Range(0, 5).Select(i => x.At(i / 4.0)).Select(q => Math.Acos(Math.Clamp(Vec3.Dot(fa.Surface.Normal(q) * (fa.Sense ? 1 : -1), fb.Surface.Normal(q) * (fb.Sense ? 1 : -1)), -1, 1)) * 180 / Math.PI);
                    Console.WriteLine($"  face edge {x.Curve.GetType().Name} {x.V0.P}->{x.V1.P} [{fa.Tag.Name}|{fb.Tag.Name}] convex {NativeBlend.IsConvex(x)} angles {string.Join(" ", angs.Select(v => v.ToString("0.##")))}");
                }
            foreach (var pt in radius!.Split(';'))
            {
                var c = pt.Split(',').Select(double.Parse).ToArray(); var p = new Vec3(c[0], c[1], c[2]);
                if (Environment.GetEnvironmentVariable("FILLET_LIST") != null)
                    foreach (var x in body.Edges.Where(x => x.Uses.Count == 2).OrderBy(x => Enumerable.Range(0, 9).Min(i => Vec3.Distance(x.At(i / 8.0), p))).Take(8))
                        Console.WriteLine($"  near: {x.Curve.GetType().Name} {x.V0.P}->{x.V1.P} [{x.Uses[0].Face.Tag.Name}|{x.Uses[1].Face.Tag.Name}] convex {NativeBlend.IsConvex(x)}");
                var e = body.Edges.Where(x => x.Uses.Count == 2).MinBy(x => Enumerable.Range(0, 9).Min(i => Vec3.Distance(x.At(i / 8.0), p)))!;
                Console.WriteLine($"  edge {e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} [{e.Uses[0].Face.Tag.Name}|{e.Uses[1].Face.Tag.Name}]");
                fi.Edges.Add(new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid });
            }
            doc.AddFeature(fi, k + 1);
            if (Environment.GetEnvironmentVariable("FILLET_MIRROR") is { } mname)
                doc.Features.OfType<MirrorFeature>().First(m => m.Name == mname).Seeds.Add(fi.Id);
            return doc;
        }
        // (shell: the part as it is, its wall the thickness given)
        if (kase == "shell")
        {
            if (si >= 0) ((ShellFeature)doc.Features[si]).Thickness = radius ?? "0.125";
            // (RIM_R: the tube rim fillet another size)
            // (DROP: the drop's flat at another height)
            if (Environment.GetEnvironmentVariable("DROP") is { } dy)
            {
                var dsk = doc.Features.OfType<SketchFeature>().First(f => f.Name == "Top drop").Sketch;
                var dc = dsk.Constraints.First(x => x.Type == CType.VDistance && Math.Abs(x.Value - 0.765) < 1e-9);
                dc.Value = double.Parse(dy); dc.Expr = dy;
                SketchSolver.Solve(dsk);
            }
            // (RAMP_R: the drop's sketch fillets another radius)
            if (Environment.GetEnvironmentVariable("RAMP_R") is { } rr0)
            {
                var dsk = doc.Features.OfType<SketchFeature>().First(f => f.Name == "Top drop").Sketch;
                foreach (var dc in dsk.Constraints.Where(x => x.Type == CType.Radius)) { dc.Value = double.Parse(rr0); dc.Expr = rr0; }
                SketchSolver.Solve(dsk);
            }
            // (PROPAGATE: the tube fillets run on along tangent edges)
            if (Environment.GetEnvironmentVariable("PROPAGATE") != null) foreach (var tf in doc.Features.OfType<FilletFeature>().Where(f => f.Name.StartsWith("Tube"))) tf.TangentPropagation = true;
            if (Environment.GetEnvironmentVariable("RIM_R") is { } rr) doc.Features.OfType<FilletFeature>().First(f => f.Name == "Tube rim").Radius = rr;
            return doc;
        }
        if (si >= 0) doc.Features.RemoveRange(si, doc.Features.Count - si);
        var dropSk = doc.Features.OfType<SketchFeature>().First(f => f.Name == "Top drop");
        var drop = doc.Features.OfType<ExtrudeFeature>().First(f => f.Name == "Top drop");
        var plenum = doc.Features.OfType<ExtrudeFeature>().First(f => f.Name == "Plenum");
        switch (kase)
        {
            case "10":
                {
                    var sk = dropSk.Sketch;
                    foreach (var fl in (sk.Fillets ?? new()).ToList()) sk.DeleteEntity(fl.Arc);
                    SketchSolver.Solve(sk);
                    // the ramp: the solid line from the top (y 1) down to the drop (y .765)
                    var ramp = sk.Entities.OfType<SkLine>().Where(l => !l.Construction)
                        .First(l => { var a = sk.P(l.P1); var b = sk.P(l.P2); return Math.Abs(a.Y - b.Y) > 0.1 && Math.Abs(a.X - b.X) > 0.1; });
                    var (p, q) = (sk.P(ramp.P1), sk.P(ramp.P2));
                    var topEnd = p.Y > q.Y ? p : q;
                    var fi = new FilletFeature
                    {
                        Name = "Ramp top", Radius = radius ?? "0.5",
                        Edges = { new EdgeRef { A = plenum.Id + ".End", B = drop.Id + ".S." + ProfileExtractor.FaceName(ramp), Hint = new Vec3(topEnd.X, topEnd.Y, 0) } },
                    };
                    doc.AddFeature(fi, doc.Features.IndexOf(drop) + 1);
                    break;
                }
            case "11":
                {
                    var rounds = doc.Features.OfType<FilletFeature>().First(f => f.Name == "Plenum edges");
                    doc.Features.Remove(rounds);
                    doc.Features.Add(rounds);
                    break;
                }
            case "12":
                {
                    var sk = doc.Features.OfType<SketchFeature>().First(f => f.Name == "Plenum").Sketch;
                    var c = sk.Constraints.First(x => x.Type == CType.Distance && Math.Abs(x.Value - 3) < 1e-9);
                    c.Value = double.Parse(radius ?? "3.25"); c.Expr = radius ?? "3.25";
                    SketchSolver.Solve(sk);
                    break;
                }
        }
        return doc;
    }

    public static int Run(string file, string kase, string? radius)
    {
        var doc = Make(file, kase, radius);
        if (Environment.GetEnvironmentVariable("REPRO_SAVE") is { } path) doc.Save(path);
        Regenerator.ShellTrace = m => Console.WriteLine("  shell from history: " + m);
        if (Environment.GetEnvironmentVariable("BLEND_DEBUG") != null)
        {
            SpryCad.Kernel.NativeBlend.Debug = m => Console.WriteLine("NB " + m);
            SpryCad.Kernel.RollingBall.Debug = m => Console.WriteLine("RB " + m);
            SpryCad.Kernel.DirectBlend.Debug = m => Console.WriteLine("DB " + m);
        }
        if (Environment.GetEnvironmentVariable("SEAMS") is { } upto)
        {
            // the edges between two faces named (substrings), the angle between their normals along each
            var parts = upto.Split('|');
            int k = doc.Features.FindIndex(f => f.Name == parts[0]);
            var st = new Regenerator().Rebuild(doc, k + 1).State;
            foreach (var b in st.Bodies)
                foreach (var e in b.Solid.Native!.Edges)
                {
                    if (e.Uses.Count != 2) continue;
                    var (fa, fb) = (e.Uses[0].Face, e.Uses[1].Face);
                    bool Has(string x) => fa.Tag.Name.Contains(x) || fb.Tag.Name.Contains(x);
                    if (!Has(parts[1]) || !Has(parts[2])) continue;
                    var angs = Enumerable.Range(0, 5).Select(i => e.At(i / 4.0)).Select(p =>
                        Math.Acos(Math.Clamp(Vec3.Dot(fa.Surface.Normal(p) * (fa.Sense ? 1 : -1), fb.Surface.Normal(p) * (fb.Sense ? 1 : -1)), -1, 1)) * 180 / Math.PI);
                    Console.WriteLine($"  {e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} [{fa.Tag.Name}|{fb.Tag.Name}] angles {string.Join(" ", angs.Select(x => x.ToString("0.###")))}");
                }
            return 0;
        }
        if (Environment.GetEnvironmentVariable("REPRO_STOP") is { } stop)
        {
            // the part up to the feature named, then that feature alone with the boolean traced
            int k = doc.Features.FindIndex(f => f.Name == stop);
            var reg = new Regenerator(); reg.Rebuild(doc, k);
            SpryCad.Kernel.Native.NBoolean.Trace = Console.WriteLine;
            reg.Rebuild(doc, k + 1);
            SpryCad.Kernel.Native.NBoolean.Trace = null;
            return 0;
        }
        bool prof = Environment.GetEnvironmentVariable("PROF") != null;
        if (prof) { Prof.Reset(); Prof.Enabled = true; }
        var r = new Regenerator().Rebuild(doc);
        if (prof)
        {
            Prof.Enabled = false;
            foreach (var (name, pms, calls) in Prof.Results().OrderByDescending(x => x.ms).Take(25)) Console.WriteLine($"      {name,-32} {pms,9:0.0} ms  x{calls}");
        }
        foreach (var f in doc.Features)
            Console.WriteLine($"  {f.Name,-22} {(r.Status[f.Id].Ok ? "ok" : "FAILED: " + r.Status[f.Id].Error)} ({r.Status[f.Id].Milliseconds:0} ms){string.Concat(r.Status[f.Id].Warnings.Select(w => " [" + w + "]"))}");
        foreach (var b in r.State.Bodies)
            Console.WriteLine($"  body: valid={b.Solid.Native?.Validate() ?? "ok"} open={b.Solid.Topology.OpenHalfEdges}");
        return 0;
    }
}
