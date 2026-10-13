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
        var r = new Regenerator().Rebuild(doc);
        foreach (var f in doc.Features)
            Console.WriteLine($"  {f.Name,-22} {(r.Status[f.Id].Ok ? "ok" : "FAILED: " + r.Status[f.Id].Error)} ({r.Status[f.Id].Milliseconds:0} ms){string.Concat(r.Status[f.Id].Warnings.Select(w => " [" + w + "]"))}");
        foreach (var b in r.State.Bodies)
            Console.WriteLine($"  body: valid={b.Solid.Native?.Validate() ?? "ok"} open={b.Solid.Topology.OpenHalfEdges}");
        return 0;
    }
}
